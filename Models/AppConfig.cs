using InstaMind.Infrastructure;

namespace InstaMind.Models;

/// <summary>Settings for the Instagram side of the bridge.</summary>
public sealed class InstagramConfig
{
    /// <summary>"sandbox" (no network, simulated inbox) or "live" (Meta Graph API).</summary>
    public string Mode { get; set; } = "sandbox";

    /// <summary>Instagram professional account id used to send messages.</summary>
    public string IgUserId { get; set; } = "";

    /// <summary>Public @handle, only used for display.</summary>
    public string Username { get; set; } = "";

    /// <summary>Long-lived Instagram / Meta access token (secret).</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Meta app secret, used to verify X-Hub-Signature-256 (secret).</summary>
    public string AppSecret { get; set; } = "";

    /// <summary>Token Meta echoes back during webhook verification.</summary>
    public string VerifyToken { get; set; } = "instamind-verify";

    public string GraphBaseUrl { get; set; } = "https://graph.instagram.com";

    public string ApiVersion { get; set; } = "v23.0";

    /// <summary>Meta / Instagram app id used for Business Login (OAuth).</summary>
    public string AppId { get; set; } = "";

    /// <summary>Exact OAuth redirect URI allow-listed in the Meta dashboard. Empty = derive from request.</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>When the stored long-lived token expires (UTC).</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>Master switch for automatic AI replies.</summary>
    public bool AutoReplyEnabled { get; set; } = true;
}

/// <summary>Any OpenAI-compatible chat completion endpoint.</summary>
public sealed class AiConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.5;
    public int MaxTokens { get; set; } = 400;
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>Run a second, cheap classification call before replying.</summary>
    public bool ScopeCheckEnabled { get; set; } = true;

    /// <summary>Model for the scope check. Empty = same as <see cref="Model"/>.</summary>
    public string ScopeCheckModel { get; set; } = "";
}

/// <summary>Who the assistant is and what it is allowed to talk about.</summary>
public sealed class PersonaConfig
{
    public string AssistantName { get; set; } = "دستیار";
    public string BusinessName { get; set; } = "برند من";
    public string BusinessSummary { get; set; } = "یک کسب‌وکار کوچک که از طریق دایرکت اینستاگرام به مشتری‌ها پاسخ می‌دهد.";
    public string Tone { get; set; } = "دوستانه، محترمانه و کوتاه";
    public string Language { get; set; } = "فارسی";

    public List<string> AllowedTopics { get; set; } = new()
    {
        "محصولات و خدمات",
        "قیمت‌ها و نحوهٔ سفارش",
        "ساعت کاری و ارسال"
    };

    public List<string> ForbiddenTopics { get; set; } = new()
    {
        "سیاست و مسائل مذهبی",
        "موضوعات جنسی یا خشونت‌آمیز",
        "مشاورهٔ پزشکی، حقوقی یا مالی"
    };

    /// <summary>Sent verbatim when the message is outside the allowed topics.</summary>
    public string OffTopicReply { get; set; } =
        "متأسفم، من فقط دربارهٔ محصولات و سفارش‌ها می‌تونم کمکت کنم. 🙏";

    /// <summary>Optional first message for brand new conversations.</summary>
    public string Greeting { get; set; } = "";

    public int MaxChars { get; set; } = 450;

    public bool UseEmoji { get; set; } = true;
}

/// <summary>Hard limits applied in code, on top of the prompt.</summary>
public sealed class GuardrailConfig
{
    /// <summary>Second-layer AI topic gate.</summary>
    public bool StrictMode { get; set; } = true;

    /// <summary>If the incoming text contains any of these, reply with the fallback immediately.</summary>
    public List<string> BlockedKeywords { get; set; } = new();

    public bool NeverRevealPrompt { get; set; } = true;

    /// <summary>Max automatic replies per conversation in a rolling hour.</summary>
    public int MaxRepliesPerHour { get; set; } = 20;

    /// <summary>Sent when the AI provider fails or times out.</summary>
    public string ErrorReply { get; set; } = "الان یه مشکل فنی داریم؛ لطفاً چند دقیقهٔ دیگه پیام بده. 🙏";

    /// <summary>Extra free-text rules appended to the system prompt.</summary>
    public string ExtraRules { get; set; } = "";
}

public sealed class KnowledgeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>An Instagram professional account that has been connected (one entry per page).</summary>
public sealed class ConnectedPage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Instagram professional account id used to send messages.</summary>
    public string IgUserId { get; set; } = "";

    /// <summary>Public @handle (display only).</summary>
    public string Username { get; set; } = "";

    /// <summary>Long-lived token for this page (secret).</summary>
    public string AccessToken { get; set; } = "";

    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>Id of the AI provider this page replies with. Empty = default (first) provider.</summary>
    public string AiId { get; set; } = "";

    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether the messages webhook subscription succeeded at connect time.</summary>
    public bool Subscribed { get; set; }

    // Last connection test (filled by POST /api/pages/{id}/test).
    public bool? LastTestOk { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public string LastTestMessage { get; set; } = "";

    // ---- browser mode: this page's own Chrome window and login (isolated from the rest) ----

    /// <summary>Handle that signs in inside this page's own Chrome window (no @).</summary>
    public string BrowserUsername { get; set; } = "";

    /// <summary>Password for that window (secret, like every other secret).</summary>
    public string BrowserPassword { get; set; } = "";

    /// <summary>Type the saved credentials when the window lands on the login page.</summary>
    public bool BrowserAutoLogin { get; set; } = true;

    /// <summary>Chrome profile folder for this account. Empty = data/browser-profiles/{Id}.</summary>
    public string BrowserProfileDirectory { get; set; } = "";

    /// <summary>Drive this account when browser mode is on (its own window, own rhythm, own caps).</summary>
    public bool BrowserEnabled { get; set; } = true;

    /// <summary>Comment -> DM bot for this account (its own post, its own follower check).</summary>
    public CommentBotConfig CommentBot { get; set; } = new();

    // ---- daily budget for the AI connected to this page (0 = no limit) ----

    /// <summary>Max automatic AI replies per day for this page.</summary>
    public int DailyReplyLimit { get; set; }

    /// <summary>Max tokens the AI may burn per day for this page.</summary>
    public long DailyTokenLimit { get; set; }

    /// <summary>Day the counters below belong to (yyyy-MM-dd, Tehran time).</summary>
    public string UsageDate { get; set; } = "";

    public int RepliesToday { get; set; }
    public long TokensToday { get; set; }

    public ConnectedPage Clone() => System.Text.Json.JsonSerializer.Deserialize<ConnectedPage>(
        System.Text.Json.JsonSerializer.Serialize(this, JsonConfig.Options), JsonConfig.Options)!;
}

/// <summary>An OpenAI-compatible API provider. The first one in the list is the default.</summary>
public sealed class AiProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Provider key (secret).</summary>
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.5;
    public int MaxTokens { get; set; } = 400;
    public int TimeoutSeconds { get; set; } = 45;
    public bool ScopeCheckEnabled { get; set; } = true;
    public string ScopeCheckModel { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- budget & measured usage ----

    /// <summary>Remaining token balance the owner reports from the provider dashboard (0 = unknown).</summary>
    public long TokenBalance { get; set; }

    /// <summary>Tokens this provider has actually consumed through the app (prompt + completion).</summary>
    public long UsedTokens { get; set; }

    /// <summary>Number of replies this provider produced (basis of the per-reply average).</summary>
    public long ReplyCount { get; set; }

    // Last connection test (filled by POST /api/ais/{id}/test).
    public bool? LastTestOk { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public string LastTestMessage { get; set; } = "";

    public AiProvider Clone() => System.Text.Json.JsonSerializer.Deserialize<AiProvider>(
        System.Text.Json.JsonSerializer.Serialize(this, JsonConfig.Options), JsonConfig.Options)!;

    public AiConfig ToAiConfig() => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        TimeoutSeconds = TimeoutSeconds,
        ScopeCheckEnabled = ScopeCheckEnabled,
        ScopeCheckModel = ScopeCheckModel
    };

    public static AiProvider FromAiConfig(AiConfig cfg, string name) => new()
    {
        Name = name,
        BaseUrl = cfg.BaseUrl,
        ApiKey = cfg.ApiKey,
        Model = cfg.Model,
        Temperature = cfg.Temperature,
        MaxTokens = cfg.MaxTokens,
        TimeoutSeconds = cfg.TimeoutSeconds,
        ScopeCheckEnabled = cfg.ScopeCheckEnabled,
        ScopeCheckModel = cfg.ScopeCheckModel
    };
}

/// <summary>
/// Watches the comments of one post and answers the ones you care about with a DM.
/// A keyword picks the comments, the follow check decides who gets the message, and the
/// whole thing runs inside that page's own browser window at a human pace.
/// </summary>
public sealed class CommentBotConfig
{
    /// <summary>Master switch for this account's comment bot.</summary>
    public bool Enabled { get; set; }

    /// <summary>Post or Reel to watch (full URL, or just the short code).</summary>
    public string PostUrl { get; set; } = "";

    /// <summary>Only comments containing this word get a DM. Empty = every new comment.</summary>
    public string Keyword { get; set; } = "";

    /// <summary>Check that the commenter follows this account before sending the real message.</summary>
    public bool RequireFollow { get; set; } = true;

    /// <summary>What non-followers get instead. Empty = they are skipped (quietest option).</summary>
    public string FollowPrompt { get; set; } = "";

    /// <summary>Fixed text to send. Empty = the page's own AI writes an answer to the comment.</summary>
    public string Message { get; set; } = "";

    /// <summary>Hard limit of comment DMs per day (0 = fall back to the daily send cap only).</summary>
    public int MaxPerDay { get; set; } = 20;

    /// <summary>Do not DM the same commenter again within this many minutes.</summary>
    public int CooldownMinutes { get; set; } = 120;

    /// <summary>How often the post is looked at.</summary>
    public int CheckIntervalMinutes { get; set; } = 10;

    /// <summary>How many comments one pass may answer.</summary>
    public int MaxPerPass { get; set; } = 2;

    public CommentBotConfig Clone() => System.Text.Json.JsonSerializer.Deserialize<CommentBotConfig>(
        System.Text.Json.JsonSerializer.Serialize(this, JsonConfig.Options), JsonConfig.Options)!;
}

/// <summary>
/// "browser" mode: a real Chrome window (driven by Selenium) that uses the owner's own
/// logged-in Instagram session instead of the Graph API. Everything in here is about
/// keeping the session consistent and the reply pace calm — slow scanning, one send at a
/// time, per-thread cool-downs and a hard daily cap. The window, the login and the
/// counters belong to each connected page separately.
/// </summary>
public sealed class BrowserConfig
{
    /// <summary>Master switch for the inbox worker in browser mode.</summary>
    public bool Enabled { get; set; }

    /// <summary>Show the Chrome window (recommended: you can watch and sign in yourself).</summary>
    public bool ShowWindow { get; set; } = true;

    /// <summary>Optional full path to chrome.exe. Empty = let Selenium find it.</summary>
    public string ChromeBinaryPath { get; set; } = "";

    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 880;

    public string InboxUrl { get; set; } = "https://www.instagram.com/direct/inbox/";

    // ---- one login for every account (kept only to migrate old config files) ----

    /// <summary>Legacy single-account handle. Moved onto the first page at load time.</summary>
    public string InstagramUsername { get; set; } = "";

    /// <summary>Legacy single-account password (secret). Moved onto the first page at load time.</summary>
    public string InstagramPassword { get; set; } = "";

    /// <summary>Legacy auto-login switch of the single-window days.</summary>
    public bool AutoLogin { get; set; }

    /// <summary>Legacy single Chrome profile folder. Moved onto the first page at load time.</summary>
    public string ProfileDirectory { get; set; } = "";

    // ---- how fast it answers ----

    /// <summary>
    /// "calm" | "balanced" | "fast". Picking a profile rewrites the pacing numbers below;
    /// afterwards you can still tune them by hand.
    /// </summary>
    public string ResponseProfile { get; set; } = "calm";

    // ---- how often we look at the inbox ----

    /// <summary>Base pause between two inbox scans.</summary>
    public int ScanIntervalSeconds { get; set; } = 120;

    /// <summary>Random extra pause added to every scan (0 = none).</summary>
    public int ScanJitterSeconds { get; set; } = 40;

    /// <summary>How many conversations one scan may open. Keeps each pass short.</summary>
    public int MaxThreadsPerScan { get; set; } = 3;

    // ---- reply pacing ----

    /// <summary>Minimum time between two outgoing messages, regardless of the conversation.</summary>
    public int MinGapBetweenSendsSeconds { get; set; } = 75;

    /// <summary>Do not answer the same person again within this many minutes.</summary>
    public int ConversationCooldownMinutes { get; set; } = 20;

    /// <summary>Per-character typing delay range (ms).</summary>
    public int TypingMinMsPerChar { get; set; } = 55;
    public int TypingMaxMsPerChar { get; set; } = 140;

    /// <summary>Pause before typing starts — the "read and think" part.</summary>
    public int ThinkMinMs { get; set; } = 5000;
    public int ThinkMaxMs { get; set; } = 16000;

    /// <summary>Hard limit of outgoing messages per day; new ones wait for tomorrow.</summary>
    public int DailySendCap { get; set; } = 50;

    // ---- quiet hours ----

    /// <summary>Answer around the clock. Turn it off to answer only inside the window below.</summary>
    public bool AlwaysOn { get; set; } = true;

    /// <summary>Local (Tehran) time, 24h "HH:mm". May run past midnight (start &gt; end).</summary>
    public string WorkingHoursStart { get; set; } = "09:00";
    public string WorkingHoursEnd { get; set; } = "23:30";

    /// <summary>Pause after a WebDriver error before trying again.</summary>
    public int BackoffSeconds { get; set; } = 300;

    // ---- DOM hooks (change them only if Instagram's markup changes) ----

    public string ThreadLinkSelector { get; set; } = "a[href*=\"/direct/t/\"]";
    public string ComposerSelector { get; set; } = "div[contenteditable=\"true\"][role=\"textbox\"]";
    public string MessageTextSelector { get; set; } = "div[dir=\"auto\"]";

    /// <summary>One comment on the post page (used by the comment bot).</summary>
    public string CommentSelector { get; set; } = "article ul li";

    // ---- server-owned runtime counters ----

    /// <summary>Tehran day the counter below belongs to (yyyy-MM-dd).</summary>
    public string SendsDate { get; set; } = "";
    public int SendsToday { get; set; }
    public DateTimeOffset? LastSendAt { get; set; }

    /// <summary>Rewrites the pacing numbers from <see cref="ResponseProfile"/>.</summary>
    public void ApplyProfile()
    {
        switch ((ResponseProfile ?? "").Trim().ToLowerInvariant())
        {
            case "fast":
                ResponseProfile = "fast";
                ScanIntervalSeconds = 20;
                ScanJitterSeconds = 8;
                MaxThreadsPerScan = 5;
                MinGapBetweenSendsSeconds = 12;
                ConversationCooldownMinutes = 3;
                ThinkMinMs = 1200;
                ThinkMaxMs = 3500;
                TypingMinMsPerChar = 25;
                TypingMaxMsPerChar = 65;
                DailySendCap = 120;
                break;

            case "balanced":
                ResponseProfile = "balanced";
                ScanIntervalSeconds = 45;
                ScanJitterSeconds = 15;
                MaxThreadsPerScan = 4;
                MinGapBetweenSendsSeconds = 30;
                ConversationCooldownMinutes = 8;
                ThinkMinMs = 2500;
                ThinkMaxMs = 7000;
                TypingMinMsPerChar = 40;
                TypingMaxMsPerChar = 100;
                DailySendCap = 80;
                break;

            default:
                ResponseProfile = "calm";
                ScanIntervalSeconds = 120;
                ScanJitterSeconds = 40;
                MaxThreadsPerScan = 3;
                MinGapBetweenSendsSeconds = 75;
                ConversationCooldownMinutes = 20;
                ThinkMinMs = 5000;
                ThinkMaxMs = 16000;
                TypingMinMsPerChar = 55;
                TypingMaxMsPerChar = 140;
                DailySendCap = 50;
                break;
        }
    }
}

public sealed class AppConfig
{
    public InstagramConfig Instagram { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public PersonaConfig Persona { get; set; } = new();
    public GuardrailConfig Guardrails { get; set; } = new();
    public BrowserConfig Browser { get; set; } = new();
    public List<KnowledgeItem> Knowledge { get; set; } = new();

    /// <summary>Connected Instagram pages. <see cref="Instagram"/> keeps the shared app-level settings.</summary>
    public List<ConnectedPage> Pages { get; set; } = new();

    /// <summary>Connected AI providers. The first entry is the default.</summary>
    public List<AiProvider> AiProviders { get; set; } = new();

    public AppConfig Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, JsonConfig.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, JsonConfig.Options)!;
    }
}
