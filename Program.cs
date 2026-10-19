using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InstaMind.Infrastructure;
using InstaMind.Models;
using InstaMind.Services;
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
builder.Services.AddSingleton<InstagramOAuth>();
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

app.MapGet("/api/config", (HttpRequest request, ConfigService config, InstagramOAuth oauth) =>
{
    var cfg = config.GetMasked();
    var raw = config.Current;
    return Results.Ok(new
    {
        config = cfg,
        webhookUrl = $"{request.Scheme}://{request.Host}/webhook/instagram",
        oauthRedirectUri = oauth.ResolveRedirectUri(request),
        oauthReady = !string.IsNullOrWhiteSpace(raw.Instagram.AppId) && !string.IsNullOrWhiteSpace(raw.Instagram.AppSecret),
        connected = raw.Pages.Count > 0,
        pageCount = raw.Pages.Count,
        aiCount = raw.AiProviders.Count,
        tokenExpiresAt = raw.Pages.FirstOrDefault()?.TokenExpiresAt ?? raw.Instagram.TokenExpiresAt,
        liveReady = raw.Pages.Any(p => !string.IsNullOrWhiteSpace(p.AccessToken) && !string.IsNullOrWhiteSpace(p.IgUserId)),
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

app.MapPost("/api/instagram/test", async (HttpRequest request, InstagramGateway gateway, ConfigService config, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<TestPageRequest>(request);
    var page = config.FindPage(body?.PageId);
    if (!string.IsNullOrWhiteSpace(body?.PageId) && page is null)
        return Results.NotFound(new { error = "چنین صفحه‌ای پیدا نشد." });
    var result = await gateway.TestPageAsync(page, ct);

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

app.MapPost("/api/pages/{id}/refresh", async (string id, InstagramOAuth oauth, ConfigService config, CancellationToken ct) =>
{
    var page = config.FindPage(id);
    if (page is null) return Results.NotFound(new { error = "صفحه پیدا نشد." });
    var (ok, message) = await oauth.RefreshAsync(ct, page.Id);
    return Results.Ok(new { ok, message, config = config.GetMasked() });
});

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
// Official Instagram Business Login (OAuth). The password never reaches us.
// ---------------------------------------------------------------------------

app.MapGet("/oauth/instagram/start", (HttpRequest request, ConfigService config, InstagramOAuth oauth) =>
{
    var ig = config.Current.Instagram;
    if (string.IsNullOrWhiteSpace(ig.AppId) || string.IsNullOrWhiteSpace(ig.AppSecret))
    {
        return Results.Redirect("/?oauth=error&reason=missing_app");
    }

    var redirectUri = oauth.ResolveRedirectUri(request);
    var authorizeUrl = oauth.BuildAuthorizeUrl(redirectUri, out _);
    return Results.Redirect(authorizeUrl);
});

app.MapGet("/oauth/instagram/callback", async (
    HttpRequest request,
    ConfigService config,
    InstagramOAuth oauth,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    var query = request.Query;

    if (query.ContainsKey("error"))
    {
        var reason = query["error_description"].ToString();
        if (string.IsNullOrWhiteSpace(reason)) reason = query["error"].ToString();
        log.LogWarning("Instagram OAuth denied: {Reason}", reason);
        return Results.Redirect($"/?oauth=error&reason={Uri.EscapeDataString(reason)}");
    }

    var state = query["state"].ToString();
    var code = query["code"].ToString();

    if (!oauth.ConsumeState(state))
    {
        log.LogWarning("Instagram OAuth state rejected");
        return Results.Redirect("/?oauth=error&reason=state");
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        return Results.Redirect("/?oauth=error&reason=missing_code");
    }

    var redirectUri = oauth.ResolveRedirectUri(request);
    var result = await oauth.CompleteAsync(code, redirectUri, ct);
    if (!result.Ok)
    {
        log.LogWarning("Instagram OAuth failed: {Error}", result.Error);
        return Results.Redirect($"/?oauth=error&reason={Uri.EscapeDataString(result.Error ?? "unknown")}");
    }

    var subscription = result.Subscribed ? "1" : "0";
    return Results.Redirect($"/?oauth=success&ig={Uri.EscapeDataString(result.Username ?? string.Empty)}&sub={subscription}");
});

app.MapPost("/api/instagram/refresh", async (HttpRequest request, InstagramOAuth oauth, ConfigService config, CancellationToken ct) =>
{
    var body = await ReadJsonAsync<TestPageRequest>(request);
    var page = string.IsNullOrWhiteSpace(body?.PageId) ? null : config.FindPage(body.PageId);
    var (ok, message) = await oauth.RefreshAsync(ct, page?.Id);
    return Results.Ok(new { ok, message, config = config.GetMasked() });
});

app.MapPost("/api/instagram/disconnect", (ConfigService config) =>
{
    config.ClearInstagramConnection();
    return Results.Ok(new { ok = true });
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
