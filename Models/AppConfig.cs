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

public sealed class AppConfig
{
    public InstagramConfig Instagram { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public PersonaConfig Persona { get; set; } = new();
    public GuardrailConfig Guardrails { get; set; } = new();
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
