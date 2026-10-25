using System.Diagnostics;
using System.Text.Json;
using InstaMind.Models;

namespace InstaMind.Services;

/// <summary>
/// The brain of the app: one incoming DM in, at most one guarded reply out.
/// Order of checks matters — cheap deterministic gates run before the model.
/// </summary>
public sealed class ReplyEngine
{
    private readonly ConfigService _config;
    private readonly ConversationService _conversations;
    private readonly AiClient _ai;
    private readonly MessageRouter _router;
    private readonly ILogger<ReplyEngine> _log;

    private readonly HashSet<string> _seenMessageIds = new();
    private readonly object _seenSync = new();

    public ReplyEngine(
        ConfigService config,
        ConversationService conversations,
        AiClient ai,
        MessageRouter router,
        ILogger<ReplyEngine> log)
    {
        _config = config;
        _conversations = conversations;
        _ai = ai;
        _router = router;
        _log = log;
    }

    public async Task<ReplyResult> HandleAsync(
        IncomingMessage incoming,
        bool deliver,
        bool persist = true,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var cfg = _config.Current.Clone();
        var result = new ReplyResult();
        void Trace(string step, string status, string? detail = null) =>
            result.Trace.Add(new ReplyTraceStep { Step = step, Status = status, Detail = detail });

        var text = (incoming.Text ?? "").Trim();
        if (text.Length == 0)
        {
            result.Reason = "empty";
            Trace("پیام دریافتی", "رد", "متن خالی");
            return Finish();
        }
        if (text.Length > 2000) text = text[..2000];
        incoming.Text = text;

        if (!string.IsNullOrWhiteSpace(incoming.MessageId) && !MarkSeen(incoming.MessageId!))
        {
            result.Reason = "duplicate";
            Trace("تکراری", "رد", "این پیام قبلاً پردازش شده است");
            return Finish();
        }
        Trace("پیام دریافتی", "OK", $"{text.Length} کاراکتر");

        // Route: which page received it, and which AI answers for that page.
        var page = _config.FindPage(incoming.PageId)
                   ?? (_config.Current.Pages.Count == 1 ? _config.Current.Pages[0] : null);
        var provider = _config.FindProvider(incoming.AiId ?? page?.AiId);
        var aiConfig = provider?.ToAiConfig() ?? cfg.Ai;
        Trace("مسیریابی", "OK",
            $"صفحه: {(page is null ? "پیش‌فرض" : "@" + (string.IsNullOrWhiteSpace(page.Username) ? page.IgUserId : page.Username))}" +
            $" ← AI: {(provider is null ? cfg.Ai.Model : provider.Name)}");

        if (persist)
        {
            await _conversations.AppendIncomingAsync(
                incoming.SenderId, incoming.Username, text, pageId: page?.IgUserId ?? incoming.PageId);
        }

        if (!cfg.Instagram.AutoReplyEnabled)
        {
            result.Reason = "auto_reply_off";
            Trace("پاسخ خودکار", "خاموش", "از پنل اتصال روشنش کن");
            return Finish();
        }

        if (persist)
        {
            var conversation = await _conversations.GetAsync(incoming.SenderId);
            if (conversation?.AiPaused == true)
            {
                result.Reason = "paused";
                Trace("مکث گفتگو", "رد", "پاسخ خودکار این گفتگو متوقف شده است");
                return Finish();
            }
        }

        // Daily budget of this page (only real auto-replies consume it; lab tests never do).
        if (deliver && _config.PageLimitReached(page?.IgUserId ?? incoming.PageId, out var limitKind))
        {
            result.Blocked = true;
            result.Reason = limitKind == "token" ? "page_token_limit" : "page_reply_limit";
            Trace("سقف مصرف روزانهٔ صفحه", "رد",
                limitKind == "token"
                    ? "سقف توکن امروز این صفحه پر شده؛ فردا ریست می‌شود یا سقف را بالا ببر"
                    : "سقف پاسخ امروز این صفحه پر شده؛ فردا ریست می‌شود یا سقف را بالا ببر");
            return Finish();
        }

        var hourly = await _conversations.AiRepliesInLastHourAsync(incoming.SenderId);
        if (hourly >= cfg.Guardrails.MaxRepliesPerHour)
        {
            result.Blocked = true;
            result.Reason = "rate_limit";
            Trace("محدودیت نرخ", "رد", $"{hourly} پاسخ در یک ساعت گذشته");
            return Finish();
        }

        string? reply = null;
        var blocked = false;
        string? note = null;
        string? status = null;

        // Gate 1: deterministic keyword blocklist.
        var blockedKeyword = Guardrail.MatchBlockedKeyword(text, cfg.Guardrails.BlockedKeywords);
        if (blockedKeyword is not null)
        {
            reply = cfg.Persona.OffTopicReply;
            blocked = true;
            note = $"کلمهٔ مسدود: {blockedKeyword}";
            Trace("فیلتر کلیدواژه", "مسدود", blockedKeyword);
        }
        else
        {
            Trace("فیلتر کلیدواژه", "OK");
        }

        // Gate 2: model-based topic classification (independent of the reply model call).
        if (reply is null && cfg.Guardrails.StrictMode && aiConfig.ScopeCheckEnabled)
        {
            var (onTopic, reason, scopeTokens) = await CheckScopeAsync(cfg, aiConfig, text, ct);
            if (scopeTokens > 0)
            {
                _config.RecordUsage(page?.IgUserId ?? incoming.PageId, provider?.Id, scopeTokens, countedAsReply: false);
            }
            if (onTopic == false)
            {
                reply = cfg.Persona.OffTopicReply;
                blocked = true;
                note = reason ?? "خارج از موضوع مجاز";
                Trace("دروازهٔ موضوع", "مسدود", reason);
            }
            else if (onTopic is null)
            {
                Trace("دروازهٔ موضوع", "هشدار", "بررسی موضوع ممکن نشد؛ فقط فیلترها فعال‌اند: " + reason);
            }
            else
            {
                Trace("دروازهٔ موضوع", "OK", reason);
            }
        }
        else if (reply is null)
        {
            Trace("دروازهٔ موضوع", "غیرفعال");
        }

        // Gate 3: the model itself, with the strict system prompt.
        if (reply is null)
        {
            var messages = new List<AiMessage>
            {
                new("system", PromptBuilder.BuildSystemPrompt(cfg))
            };

            var history = persist
                ? await _conversations.RecentMessagesAsync(incoming.SenderId, 12)
                : new List<ChatMessage>();

            foreach (var message in history)
            {
                var body = message.Text ?? "";
                if (body.Length == 0) continue;
                if (body.Length > 700) body = body[..700];
                messages.Add(new AiMessage(message.Direction == "in" ? "user" : "assistant", body));
            }

            if (history.Count == 0)
            {
                messages.Add(new AiMessage("user", text));
            }

            var ai = await _ai.ChatAsync(aiConfig, messages, ct: ct);
            _config.RecordUsage(
                page?.IgUserId ?? incoming.PageId,
                provider?.Id,
                ai.PromptTokens + ai.CompletionTokens,
                countedAsReply: ai.Ok);
            if (!ai.Ok)
            {
                status = "failed";
                note = ai.Error;
                Trace("تولید پاسخ", "خطا", ai.Error);
                reply = string.IsNullOrWhiteSpace(cfg.Guardrails.ErrorReply) ? "" : cfg.Guardrails.ErrorReply;
            }
            else
            {
                Trace("تولید پاسخ", "OK", $"{ai.PromptTokens}+{ai.CompletionTokens} توکن");
                reply = Guardrail.Sanitize(ai.Text, cfg.Persona);
            }
        }

        // Gate 4: output guard (keywords, prompt leaks, length).
        if (!string.IsNullOrEmpty(reply))
        {
            var unsafeReason = Guardrail.CheckOutgoing(reply, cfg);
            if (unsafeReason is not null)
            {
                Trace("بازرسی خروجی", "مسدود", unsafeReason);
                reply = cfg.Persona.OffTopicReply;
                blocked = true;
                note = unsafeReason;
            }
            else
            {
                Trace("بازرسی خروجی", "OK", $"{reply.Length} کاراکتر");
            }
        }

        if (string.IsNullOrWhiteSpace(reply))
        {
            result.Blocked = blocked;
            result.Reason = note ?? "no_reply";
            Trace("ارسال", "رد", "پاسخی برای ارسال نبود");
            return Finish();
        }

        result.Reply = reply;
        result.Blocked = blocked;
        result.Reason = blocked ? note ?? "off_topic" : null;

        if (deliver)
        {
            var send = await _router.SendTextAsync(page, incoming.SenderId, reply, ct);
            result.Sent = send.Ok;
            if (!send.Ok)
            {
                status = "failed";
                note = send.Error;
                Trace("ارسال پیام", "خطا", send.Error);
            }
            else
            {
                status = send.MessageId == "sandbox" ? "sandbox" : "sent";
                Trace("ارسال پیام", "OK", send.MessageId == "sandbox" ? "فقط در سندباکس ثبت شد" : send.MessageId);
            }
        }
        else
        {
            status = "test";
            Trace("ارسال پیام", "تست", "در حالت تست ارسال نشد");
        }

        if (persist)
        {
            await _conversations.AppendOutgoingAsync(
                incoming.SenderId, reply, "ai", status, note);
        }

        return Finish();

        ReplyResult Finish()
        {
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }
    }

    public async Task<SendResult> SendManualAsync(string conversationId, string text, CancellationToken ct = default)
    {
        var conversation = await _conversations.GetAsync(conversationId);
        var page = _config.FindPage(conversation?.PageId) ?? _config.Current.Pages.FirstOrDefault();
        var send = await _router.SendTextAsync(page, conversationId, text, ct);
        await _conversations.AppendOutgoingAsync(
            conversationId, text, "human", send.Ok ? "sent" : "failed", send.Error);
        return send;
    }

    private async Task<(bool? OnTopic, string? Reason, int Tokens)> CheckScopeAsync(AppConfig cfg, AiConfig aiConfig, string text, CancellationToken ct)
    {
        var scopeConfig = new AiConfig
        {
            BaseUrl = aiConfig.BaseUrl,
            ApiKey = aiConfig.ApiKey,
            Model = string.IsNullOrWhiteSpace(aiConfig.ScopeCheckModel) ? aiConfig.Model : aiConfig.ScopeCheckModel,
            MaxTokens = 150,
            Temperature = 0,
            TimeoutSeconds = Math.Min(20, aiConfig.TimeoutSeconds),
            ScopeCheckEnabled = false
        };

        var messages = new[]
        {
            new AiMessage("system", PromptBuilder.BuildScopePrompt(cfg)),
            new AiMessage("user", text)
        };

        var result = await _ai.ChatAsync(scopeConfig, messages, temperature: 0, maxTokens: 150, jsonMode: true, ct: ct);
        var tokens = result.PromptTokens + result.CompletionTokens;
        if (!result.Ok)
        {
            _log.LogWarning("Scope check failed: {Error}", result.Error);
            return (null, result.Error, tokens);
        }

        var json = ExtractJson(result.Text);
        if (json is null) return (null, "پاسخ دروازهٔ موضوع قابل خواندن نبود", tokens);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var verdict = ReadBool(doc.RootElement, "on_topic", "allowed", "ok", "onTopic");
            if (verdict is null) return (null, "فیلد on_topic پیدا نشد", tokens);
            var reason = ReadString(doc.RootElement, "reason", "topic");
            return (verdict, reason, tokens);
        }
        catch (JsonException)
        {
            return (null, "JSON دروازهٔ موضوع نامعتبر بود", tokens);
        }
    }

    private bool MarkSeen(string id)
    {
        lock (_seenSync)
        {
            if (_seenMessageIds.Contains(id)) return false;
            if (_seenMessageIds.Count > 3000) _seenMessageIds.Clear();
            _seenMessageIds.Add(id);
            return true;
        }
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    private static bool? ReadBool(JsonElement element, params string[] names)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (!names.Any(n => string.Equals(n, prop.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (prop.Value.ValueKind == JsonValueKind.True) return true;
            if (prop.Value.ValueKind == JsonValueKind.False) return false;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var raw = prop.Value.GetString();
                if (bool.TryParse(raw, out var parsed)) return parsed;
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (!names.Any(n => string.Equals(n, prop.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
        }
        return null;
    }
}
