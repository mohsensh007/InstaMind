using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InstaMind.Infrastructure;
using InstaMind.Models;
using InstaMind.Services;
using InstaMind.Services.Browser;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
builder.Services.AddSingleton(new SecretBox(dataDirectory));
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<ConversationService>();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AiClient>();
builder.Services.AddSingleton<InstagramGateway>();

// Browser (Selenium) mode: one real Chrome window per connected page, each with its own
// profile, its own pacing counters and its own AI.
builder.Services.AddSingleton<BrowserHub>();
builder.Services.AddSingleton<BrowserTransport>();
builder.Services.AddSingleton<BrowserInboxWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrowserInboxWorker>());

builder.Services.AddSingleton<MessageRouter>();
builder.Services.AddSingleton<ReplyEngine>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Trust the local tunnel (ngrok / cloudflared) so the OAuth redirect URI keeps https.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                               ForwardedHeaders.XForwardedProto |
                               ForwardedHeaders.XForwardedHost;
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------------------
// Config & diagnostics
// ---------------------------------------------------------------------------

app.MapGet("/api/config", (HttpRequest request, ConfigService config) =>
{
    var cfg = config.GetMasked();
    var raw = config.Current;
    return Results.Ok(new
    {
        config = cfg,
        connected = raw.Pages.Count > 0,
        pageCount = raw.Pages.Count,
        browserAccounts = raw.Pages.Count(p => !string.IsNullOrWhiteSpace(p.BrowserUsername)),
        aiCount = raw.AiProviders.Count,
        tokenExpiresAt = raw.Pages.FirstOrDefault()?.TokenExpiresAt,
        liveReady = raw.Pages.Any(p => !string.IsNullOrWhiteSpace(p.AccessToken) && !string.IsNullOrWhiteSpace(p.IgUserId)),
        browserMode = raw.Instagram.Mode == MessageRouter.ModeBrowser,
        browserWorker = raw.Browser.Enabled,
        aiReady = raw.AiProviders.Any(p => !string.IsNullOrWhiteSpace(p.ApiKey)) ||
                  !string.IsNullOrWhiteSpace(raw.Ai.ApiKey),
        serverTime = DateTimeOffset.UtcNow
    });
});

app.MapPut("/api/config", (AppConfig incoming, ConfigService config) =>
{
    if (incoming is null) return Results.BadRequest(new { error = "بدنهٔ درخواست خالی است." });
    try
    {
        config.Save(incoming);
        return Results.Ok(new { ok = true, config = config.GetMasked() });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "ذخیرهٔ تنظیمات ممکن نشد: " + ex.Message });
    }
});

app.MapGet("/api/stats", async (ConversationService conversations, ConfigService config) =>
{
    var stats = await conversations.StatsAsync();
    var cfg = config.Current;
    return Results.Ok(new
    {
        stats,
        autoReply = cfg.Instagram.AutoReplyEnabled,
        mode = cfg.Instagram.Mode,
        strictMode = cfg.Guardrails.StrictMode,
        scopeCheck = cfg.Ai.ScopeCheckEnabled,
        model = cfg.Ai.Model,
        pageCount = cfg.Pages.Count,
        aiCount = cfg.AiProviders.Count,
        liveReady = cfg.Pages.Any(p => !string.IsNullOrWhiteSpace(p.AccessToken) &&
                                      !string.IsNullOrWhiteSpace(p.IgUserId)),
        aiReady = cfg.AiProviders.Any(p => !string.IsNullOrWhiteSpace(p.ApiKey))
    });
});

app.MapGet("/api/prompt/preview", (ConfigService config) =>
    Results.Ok(new { prompt = PromptBuilder.BuildSystemPrompt(config.Current) }));

// ---------------------------------------------------------------------------
// Connection tests
// ---------------------------------------------------------------------------

app.MapPost("/api/ai/test", async (HttpRequest request, AiClient ai, ConfigService config, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<TestAiRequest>(request);
    var provider = config.FindProvider(body?.AiId);
    var cfg = provider?.ToAiConfig() ?? config.Current.Ai;
    var result = await ai.PingAsync(cfg, ct);

    var message = result.Ok
        ? $"مدل {cfg.Model} پاسخ داد: {result.Text}"
        : result.Error ?? "تست ناموفق بود.";

    if (provider is not null)
    {
        config.UpdateProvider(provider.Id, p =>
        {
            p.LastTestOk = result.Ok;
            p.LastTestAt = DateTimeOffset.UtcNow;
            p.LastTestMessage = result.Ok ? $"مدل {cfg.Model} پاسخ داد" : (result.Error ?? "");
        });
    }

    return Results.Ok(new
    {
        ok = result.Ok,
        message,
        aiId = provider?.Id,
        tokens = result.PromptTokens + result.CompletionTokens,
        config = config.GetMasked()
    });
});

app.MapPost("/api/instagram/test", async (HttpRequest request, MessageRouter router, ConfigService config, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<TestPageRequest>(request);
    var page = config.FindPage(body?.PageId);
    if (!string.IsNullOrWhiteSpace(body?.PageId) && page is null)
        return Results.NotFound(new { error = "چنین صفحه‌ای پیدا نشد." });
    var result = await router.TestPageAsync(page, ct);

    if (page is not null)
    {
        config.UpdatePage(page.Id, p =>
        {
            p.LastTestOk = result.Ok;
            p.LastTestAt = DateTimeOffset.UtcNow;
            p.LastTestMessage = result.Detail;
            if (result.Ok && !string.IsNullOrWhiteSpace(result.Username)) p.Username = result.Username;
        });
    }

    return Results.Ok(new
    {
        ok = result.Ok,
        message = result.Detail,
        username = result.Username ?? page?.Username,
        pageId = page?.Id,
        config = config.GetMasked()
    });
});

// ---------------------------------------------------------------------------
// Browser mode (Selenium): one real Chrome window per connected page
// ---------------------------------------------------------------------------

app.MapGet("/api/browser/status", (BrowserHub hub, ConfigService config) =>
{
    var sessions = hub.Active().Select(rt => rt.Status()).ToList();
    return Results.Ok(new
    {
        running = sessions.Any(s => s.Running),
        mode = config.Current.Instagram.Mode,
        worker = config.Current.Browser.Enabled,
        sessions
    });
});

app.MapPost("/api/browser/start", async (HttpRequest request, BrowserHub hub, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<PageRequest>(request);
    var targets = hub.Resolve(body?.PageId);
    if (targets.Count == 0) return Results.Ok(new { ok = false, message = "حسابی پیدا نشد؛ اول یک حساب اضافه کن." });

    var results = new List<BrowserStatus>();
    foreach (var runtime in targets) results.Add(await runtime.StartAsync(ct));

    return Results.Ok(new
    {
        ok = results.Any(r => r.Running),
        state = results.FirstOrDefault()?.State ?? "stopped",
        message = Join(results, r => r.Message),
        sessions = results
    });
});

app.MapPost("/api/browser/stop", async (HttpRequest request, BrowserHub hub, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<PageRequest>(request);
    var targets = hub.Resolve(body?.PageId);
    if (targets.Count == 0) return Results.Ok(new { ok = true, message = "چیزی برای بستن باز نبود." });

    var results = new List<BrowserStatus>();
    foreach (var runtime in targets) results.Add(await runtime.StopAsync());
    return Results.Ok(new { ok = true, message = "پنجره‌ها بسته شدند.", sessions = results });
});

app.MapPost("/api/browser/login", async (HttpRequest request, BrowserHub hub, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<PageRequest>(request);
    var targets = hub.Resolve(body?.PageId);
    if (targets.Count == 0) return Results.Ok(new { ok = false, message = "حسابی پیدا نشد؛ اول یک حساب اضافه کن." });

    var reports = new List<string>();
    var anyOk = false;
    foreach (var runtime in targets)
    {
        var (ok, message, _) = await runtime.LoginWithSavedCredentialsAsync(ct);
        anyOk |= ok;
        reports.Add($"{LabelOf(runtime)}: {message}");
    }

    return Results.Ok(new
    {
        ok = anyOk,
        state = targets.FirstOrDefault()?.Status().State,
        message = string.Join(" | ", reports),
        sessions = targets.Select(rt => rt.Status()).ToList()
    });
});

app.MapPost("/api/browser/forget-credentials", (ConfigService config) =>
{
    config.ClearBrowserCredentials();
    return Results.Ok(new { ok = true, config = config.GetMasked() });
});

app.MapPost("/api/browser/code", async (HttpRequest request, BrowserHub hub, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<CodeRequest>(request);
    var code = (body?.Code ?? "").Trim();
    if (code.Length == 0) return Results.Ok(new { ok = false, message = "کد تأیید را وارد کن." });

    var targets = hub.Resolve(body?.PageId);
    if (targets.Count == 0) return Results.Ok(new { ok = false, message = "حسابی پیدا نشد." });
    if (targets.Count > 1)
        return Results.Ok(new { ok = false, message = "چند پنجره باز است؛ کد را برای یک حساب مشخص وارد کن." });

    var (ok, message) = await targets[0].SubmitCodeAsync(code, ct);
    return Results.Ok(new { ok, message, session = targets[0].Status() });
});

app.MapPost("/api/browser/accounts", (AddAccountRequest body, ConfigService config) =>
{
    var username = (body?.Username ?? "").Trim().TrimStart('@');
    if (username.Length == 0)
        return Results.BadRequest(new { error = "یوزرنیم اینستاگرام را وارد کن." });

    if (config.Current.Pages.Any(p =>
            string.Equals(p.BrowserUsername, username, StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { error = "این حساب قبلاً اضافه شده است." });

    var password = (body?.Password ?? "").Trim();
    var page = config.UpsertPage(new ConnectedPage
    {
        Id = Guid.NewGuid().ToString("N"),
        Username = username,
        BrowserUsername = username,
        BrowserPassword = password,
        BrowserAutoLogin = password.Length > 0,
        BrowserEnabled = true
    });

    return Results.Ok(new { ok = true, pageId = page.Id, config = config.GetMasked() });
});

app.MapPost("/api/browser/test", async (HttpRequest request, BrowserHub hub, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<PageRequest>(request);
    var targets = hub.Resolve(body?.PageId);
    if (targets.Count == 0) return Results.Ok(new { ok = false, message = "حسابی پیدا نشد." });

    var reports = new List<string>();
    var anyOk = false;
    foreach (var runtime in targets)
    {
        var (result, _) = await runtime.TestAsync(ct);
        anyOk |= result.Ok;
        reports.Add($"{LabelOf(runtime)}: {result.Detail}");
    }

    return Results.Ok(new
    {
        ok = anyOk,
        message = string.Join(" | ", reports),
        sessions = targets.Select(rt => rt.Status()).ToList()
    });
});

app.MapPost("/api/browser/scan", async (HttpRequest request, BrowserInboxWorker worker, CancellationToken ct) =>
{
    try
    {
        var body = await ReadJsonAsync<PageRequest>(request);
        return Results.Ok(await worker.ScanOnceAsync(manual: true, body?.PageId, ct));
    }
    catch (OperationCanceledException)
    {
        return Results.Ok(new { ok = false, message = "اسکن لغو شد." });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, message = "اسکن ناموفق بود: " + ex.Message });
    }
});

app.MapGet("/api/browser/diagnostics", (HttpRequest request, BrowserHub hub) =>
{
    var pageId = request.Query["pageId"].ToString();
    var targets = hub.Resolve(string.IsNullOrWhiteSpace(pageId) ? null : pageId);
    return Results.Ok(targets.Select(rt => new
    {
        pageId = rt.PageId,
        username = rt.PageUsername,
        diagnostics = rt.Diagnose()
    }).ToList());
});

static string LabelOf(BrowserRuntime runtime) =>
    string.IsNullOrWhiteSpace(runtime.PageUsername) ? "حساب" : "@" + runtime.PageUsername;

static string Join(List<BrowserStatus> sessions, Func<BrowserStatus, string> pick) =>
    string.Join(" | ", sessions.Select(s => $"{(string.IsNullOrWhiteSpace(s.PageUsername) ? "حساب" : "@" + s.PageUsername)}: {pick(s)}"));

// ---------------------------------------------------------------------------
// Connected Instagram pages (multiple pages supported)
// ---------------------------------------------------------------------------

app.MapPost("/api/pages/{id}/assign", (string id, AssignAiRequest body, ConfigService config) =>
{
    var aiId = (body?.AiId ?? "").Trim();
    if (aiId.Length > 0 && !config.Current.AiProviders.Any(p => p.Id == aiId))
        return Results.BadRequest(new { error = "سرویس هوش مصنوعی انتخاب‌شده پیدا نشد." });

    var page = config.UpdatePage(id, p => p.AiId = aiId);
    return page is null
        ? Results.NotFound(new { error = "صفحه پیدا نشد." })
        : Results.Ok(new { ok = true, config = config.GetMasked() });
});

app.MapDelete("/api/pages/{id}", (string id, ConfigService config) =>
    config.RemovePage(id)
        ? Results.Ok(new { ok = true, config = config.GetMasked() })
        : Results.NotFound(new { error = "صفحه پیدا نشد." }));

// ---------------------------------------------------------------------------
// AI providers (multiple providers supported; the first is the default)
// ---------------------------------------------------------------------------

app.MapGet("/api/ais", (ConfigService config) => Results.Ok(config.GetMasked().AiProviders));

app.MapPost("/api/ais", (AiProvider provider, ConfigService config) =>
{
    if (provider is null) return Results.BadRequest(new { error = "بدنهٔ درخواست خالی است." });
    if (string.IsNullOrWhiteSpace(provider.Name)) return Results.BadRequest(new { error = "نام سرویس را وارد کن." });
    var saved = config.UpsertProvider(provider);
    return Results.Ok(new { ok = true, id = saved.Id, config = config.GetMasked() });
});

app.MapDelete("/api/ais/{id}", (string id, ConfigService config) =>
{
    if (!config.RemoveProvider(id))
        return Results.BadRequest(new { error = "امکان حذف نیست؛ حداقل یک سرویس هوش مصنوعی باید بماند." });
    return Results.Ok(new { ok = true, config = config.GetMasked() });
});

// ---------------------------------------------------------------------------
// Token usage: estimate how many replies each AI can still give,
// and the daily budget each page is allowed to spend.
// ---------------------------------------------------------------------------

const int DefaultAvgTokensPerReply = 600; // assumed until real calls measure it

app.MapGet("/api/usage", (ConfigService config) =>
{
    var cfg = config.Current;
    var day = ConfigService.TodayTehran;

    var providers = cfg.AiProviders.Select(p =>
    {
        var measured = p.ReplyCount > 0 ? (long)Math.Round((double)p.UsedTokens / p.ReplyCount) : 0L;
        var avg = measured > 0 ? measured : DefaultAvgTokensPerReply;
        return new
        {
            p.Id,
            p.Name,
            p.Model,
            p.TokenBalance,
            p.UsedTokens,
            p.ReplyCount,
            avgTokensPerReply = avg,
            measured = p.ReplyCount > 0,
            estimatedMessagesLeft = p.TokenBalance > 0 ? p.TokenBalance / avg : (long?)null,
            lastTestOk = p.LastTestOk
        };
    }).ToList();

    var pages = cfg.Pages.Select(p => new
    {
        p.Id,
        p.Username,
        p.IgUserId,
        p.AiId,
        p.DailyReplyLimit,
        p.DailyTokenLimit,
        repliesToday = p.UsageDate == day ? p.RepliesToday : 0,
        tokensToday = p.UsageDate == day ? p.TokensToday : 0
    }).ToList();

    return Results.Ok(new
    {
        day,
        defaultAvgTokensPerReply = DefaultAvgTokensPerReply,
        providers,
        pages
    });
});

app.MapPost("/api/pages/{id}/limit", async (string id, HttpRequest request, ConfigService config, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<PageLimitRequest>(request);
    if (body is null) return Results.BadRequest(new { error = "بدنهٔ درخواست خالی است." });

    var page = config.SetPageLimits(
        id,
        body.DailyReplyLimit,
        body.DailyTokenLimit,
        body.ResetUsage == true);

    return page is null
        ? Results.NotFound(new { error = "صفحه پیدا نشد." })
        : Results.Ok(new
        {
            ok = true,
            page = new
            {
                page.Id,
                page.DailyReplyLimit,
                page.DailyTokenLimit,
                page.RepliesToday,
                page.TokensToday,
                day = ConfigService.TodayTehran
            },
            config = config.GetMasked()
        });
});

// ---------------------------------------------------------------------------
// Inbox
// ---------------------------------------------------------------------------

app.MapGet("/api/conversations", async (ConversationService conversations) =>
{
    var items = await conversations.ListAsync();
    return Results.Ok(items.Select(c => new
    {
        id = c.Id,
        username = c.Username,
        aiPaused = c.AiPaused,
        lastMessageAt = c.LastMessageAt,
        messageCount = c.Messages.Count,
        lastText = c.LastMessage?.Text ?? "",
        lastDirection = c.LastMessage?.Direction ?? "in",
        lastAuthor = c.LastMessage?.Author ?? "",
        lastStatus = c.LastMessage?.Status
    }));
});

app.MapGet("/api/conversations/{id}", async (string id, ConversationService conversations) =>
{
    var conversation = await conversations.GetAsync(id);
    return conversation is null ? Results.NotFound() : Results.Ok(conversation);
});

app.MapPost("/api/conversations/{id}/pause", async (string id, PauseRequest body, ConversationService conversations) =>
{
    var ok = await conversations.SetPausedAsync(id, body.Paused);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapPost("/api/conversations/{id}/reply", async (string id, ManualReplyRequest body, ReplyEngine engine, CancellationToken ct) =>
{
    var text = (body.Text ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { error = "متن پیام خالی است." });

    var send = await engine.SendManualAsync(id, text, ct);
    var message = !send.Ok
        ? send.Error
        : send.MessageId == "sandbox"
            ? "در سندباکس ثبت شد (ارسال واقعی انجام نشد)."
            : "ارسال شد.";
    return Results.Ok(new { ok = send.Ok, message });
});

app.MapDelete("/api/conversations/{id}", async (string id, ConversationService conversations) =>
{
    var ok = await conversations.DeleteAsync(id);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapDelete("/api/conversations", async (ConversationService conversations) =>
{
    await conversations.ClearAsync();
    return Results.Ok(new { ok = true });
});

// ---------------------------------------------------------------------------
// Playground: run the full guardrail pipeline without touching Instagram
// ---------------------------------------------------------------------------

app.MapPost("/api/sandbox/incoming", async (SandboxRequest body, ReplyEngine engine, CancellationToken ct) =>
{
    var text = (body.Text ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { error = "پیام آزمایشی خالی است." });

    var result = await engine.HandleAsync(
        new IncomingMessage
        {
            SenderId = string.IsNullOrWhiteSpace(body.SenderId) ? "sandbox-playground" : body.SenderId!,
            Username = body.Username ?? "sandbox",
            Text = text,
            PageId = body.PageId,
            AiId = body.AiId
        },
        deliver: false,
        persist: false,
        ct: ct);

    return Results.Ok(result);
});

// ---------------------------------------------------------------------------
// Meta webhook
// ---------------------------------------------------------------------------

app.MapGet("/webhook/instagram", (HttpRequest request, ConfigService config) =>
{
    var mode = request.Query["hub.mode"].ToString();
    var token = request.Query["hub.verify_token"].ToString();
    var challenge = request.Query["hub.challenge"].ToString();
    var expected = config.Current.Instagram.VerifyToken;

    if (mode == "subscribe" && !string.IsNullOrEmpty(expected) &&
        string.Equals(token, expected, StringComparison.Ordinal))
    {
        return Results.Text(challenge, "text/plain");
    }

    return Results.StatusCode(StatusCodes.Status403Forbidden);
});

app.MapPost("/webhook/instagram", async (
    HttpRequest request,
    ConfigService config,
    ReplyEngine engine,
    ILogger<Program> log) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync();
    var cfg = config.Current.Instagram;

    if (!string.IsNullOrWhiteSpace(cfg.AppSecret))
    {
        var signature = request.Headers["X-Hub-Signature-256"].ToString();
        if (!VerifySignature(raw, signature, cfg.AppSecret))
        {
            log.LogWarning("Webhook signature rejected");
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
    }
    else if (cfg.Mode == "live")
    {
        log.LogWarning("Webhook rejected: app secret is not configured");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var incoming = ParseMessages(raw);
    if (incoming.Count == 0) return Results.Ok("EVENT_RECEIVED");

    // Meta needs a fast 200; the AI call happens in the background.
    _ = Task.Run(async () =>
    {
        foreach (var message in incoming)
        {
            try
            {
                var result = await engine.HandleAsync(message, deliver: true, persist: true);
                log.LogInformation(
                    "Handled DM from {Sender}: reply={Sent} blocked={Blocked} reason={Reason}",
                    message.SenderId, result.Sent, result.Blocked, result.Reason);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to handle DM from {Sender}", message.SenderId);
            }
        }
    });

    return Results.Ok("EVENT_RECEIVED");
});

app.MapGet("/api/health", () => Results.Ok(new { ok = true, at = DateTimeOffset.UtcNow }));

app.Run();

static async Task<T?> ReadJsonAsync<T>(HttpRequest request) where T : class
{
    try
    {
        if (request.ContentLength is null or 0) return null;
        return await JsonSerializer.DeserializeAsync<T>(
            request.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch
    {
        return null;
    }
}

static bool VerifySignature(string body, string signatureHeader, string appSecret)
{
    if (string.IsNullOrWhiteSpace(signatureHeader) ||
        !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var expected = Convert.ToHexString(
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(appSecret),
            Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    var provided = signatureHeader["sha256=".Length..].Trim().ToLowerInvariant();
    return CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(expected),
        Encoding.ASCII.GetBytes(provided));
}

static List<IncomingMessage> ParseMessages(string raw)
{
    var messages = new List<IncomingMessage>();
    try
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("entry", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("messaging", out var events) ||
                events.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // entry.id is the IG user id of the page the event belongs to.
            var entryPageId = entry.TryGetProperty("id", out var entryId) &&
                              entryId.ValueKind == JsonValueKind.String
                ? entryId.GetString()
                : null;

            foreach (var evt in events.EnumerateArray())
            {
                if (!evt.TryGetProperty("message", out var message)) continue;
                if (message.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue;

                var text = message.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var senderId = evt.TryGetProperty("sender", out var sender) &&
                               sender.TryGetProperty("id", out var sid)
                    ? sid.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(senderId)) continue;

                var username = sender.TryGetProperty("username", out var un) ? un.GetString() : null;

                messages.Add(new IncomingMessage
                {
                    SenderId = senderId!,
                    Username = username ?? "",
                    Text = text!,
                    MessageId = message.TryGetProperty("mid", out var mid) ? mid.GetString() : null,
                    PageId = entryPageId
                });
            }
        }
    }
    catch (JsonException)
    {
        // Malformed payload: treat as empty so Meta gets its 200 and stops retrying.
    }

    return messages;
}

public sealed record SandboxRequest(string? Text, string? SenderId, string? Username, string? PageId, string? AiId);
public sealed record ManualReplyRequest(string? Text);
public sealed record PauseRequest(bool Paused);
public sealed record TestPageRequest(string? PageId);
public sealed record TestAiRequest(string? AiId);
public sealed record AssignAiRequest(string? AiId);
public sealed record PageLimitRequest(int? DailyReplyLimit, long? DailyTokenLimit, bool? ResetUsage);
public sealed record PageRequest(string? PageId);
public sealed record CodeRequest(string? Code, string? PageId);
public sealed record AddAccountRequest(string? Username, string? Password);
