using InstaMind.Infrastructure;
using InstaMind.Models;

namespace InstaMind.Services;

/// <summary>
/// Owns data/config.json. Secrets are encrypted on write and decrypted on read;
/// the API only ever sees masked values.
/// </summary>
public sealed class ConfigService
{
    private readonly string _path;
    private readonly string _contentRoot;
    private readonly SecretBox _box;
    private readonly object _sync = new();
    private AppConfig _config;

    public ConfigService(IWebHostEnvironment env, SecretBox box)
    {
        _path = Path.Combine(env.ContentRootPath, "data", "config.json");
        _contentRoot = env.ContentRootPath;
        _box = box;
        _config = Load();
    }

    public AppConfig Current
    {
        get { lock (_sync) return _config; }
    }

    public AppConfig GetMasked()
    {
        var copy = Current.Clone();
        copy.Instagram.AccessToken = Mask(copy.Instagram.AccessToken);
        copy.Instagram.AppSecret = Mask(copy.Instagram.AppSecret);
        copy.Ai.ApiKey = Mask(copy.Ai.ApiKey);
        copy.Browser.InstagramPassword = Mask(copy.Browser.InstagramPassword);
        foreach (var page in copy.Pages)
        {
            page.AccessToken = Mask(page.AccessToken);
            page.BrowserPassword = Mask(page.BrowserPassword);
        }
        foreach (var provider in copy.AiProviders) provider.ApiKey = Mask(provider.ApiKey);
        return copy;
    }

    /// <summary>Merges a client-submitted config, preserving secrets that were sent back masked or blank.</summary>
    public AppConfig Save(AppConfig incoming)
    {
        lock (_sync)
        {
            incoming.Instagram.AccessToken = MergeSecret(incoming.Instagram.AccessToken, _config.Instagram.AccessToken);
            incoming.Instagram.AppSecret = MergeSecret(incoming.Instagram.AppSecret, _config.Instagram.AppSecret);
            incoming.Ai.ApiKey = MergeSecret(incoming.Ai.ApiKey, _config.Ai.ApiKey);

            incoming.Browser ??= new BrowserConfig();
            // The single-account login is legacy: it lives on each page now.
            incoming.Browser.InstagramUsername = "";
            incoming.Browser.InstagramPassword = "";
            incoming.Browser.AutoLogin = false;

            // Picking a different speed profile rewrites the pacing numbers; hand-tuned
            // numbers under the same profile are kept as they are.
            if (!string.Equals(incoming.Browser.ResponseProfile, _config.Browser.ResponseProfile,
                    StringComparison.OrdinalIgnoreCase))
            {
                incoming.Browser.ApplyProfile();
            }

            // Pages and providers are managed through their own endpoints; a client-submitted
            // list never carries usable secrets, so restore the stored one (or merge by id).
            incoming.Pages = MergePages(incoming.Pages);
            incoming.AiProviders = MergeProviders(incoming.AiProviders);

            Normalize(incoming);
            MirrorLegacy(incoming);
            Persist(incoming);
            _config = incoming;
            return incoming;
        }
    }

    private List<ConnectedPage> MergePages(List<ConnectedPage>? submitted)
    {
        if (submitted is null || submitted.Count == 0) return _config.Pages
            .Select(p => p.Clone()).ToList();

        return submitted.Select(page =>
        {
            var existing = _config.Pages.FirstOrDefault(p => p.Id == page.Id);
            page.AccessToken = MergeSecret(page.AccessToken, existing?.AccessToken ?? "");
            page.BrowserPassword = MergeSecret(page.BrowserPassword, existing?.BrowserPassword ?? "");
            if (existing is not null)
            {
                page.TokenExpiresAt ??= existing.TokenExpiresAt;
                page.ConnectedAt = existing.ConnectedAt;
            }
            return page;
        }).ToList();
    }

    private List<AiProvider> MergeProviders(List<AiProvider>? submitted)
    {
        if (submitted is null || submitted.Count == 0) return _config.AiProviders
            .Select(p => p.Clone()).ToList();

        return submitted.Select(provider =>
        {
            var existing = _config.AiProviders.FirstOrDefault(p => p.Id == provider.Id);
            provider.ApiKey = MergeSecret(provider.ApiKey, existing?.ApiKey ?? "");
            if (existing is not null) provider.CreatedAt = existing.CreatedAt;
            return provider;
        }).ToList();
    }

    // ------------------------------------------------------------------
    // Pages & AI providers
    // ------------------------------------------------------------------

    /// <summary>Adds or updates a page (matched by Instagram user id). Keeps its assigned AI.</summary>
    public ConnectedPage UpsertPage(ConnectedPage page)
    {
        lock (_sync)
        {
            var cfg = _config.Clone();
            var existing = cfg.Pages.FirstOrDefault(p =>
                p.IgUserId == page.IgUserId && !string.IsNullOrWhiteSpace(p.IgUserId))
                ?? cfg.Pages.FirstOrDefault(p => p.Id == page.Id);

            if (existing is null)
            {
                cfg.Pages.Add(page);
            }
            else
            {
                existing.IgUserId = page.IgUserId;
                existing.Username = page.Username;
                existing.AccessToken = page.AccessToken;
                existing.TokenExpiresAt = page.TokenExpiresAt;
                existing.Subscribed = page.Subscribed;
                page = existing;
            }

            Normalize(cfg);
            MirrorLegacy(cfg);
            Persist(cfg);
            _config = cfg;
            return page;
        }
    }

    public bool RemovePage(string id)
    {
        lock (_sync)
        {
            var cfg = _config.Clone();
            var removed = cfg.Pages.RemoveAll(p => p.Id == id || p.IgUserId == id);
            if (removed == 0) return false;
            Normalize(cfg);
            MirrorLegacy(cfg);
            Persist(cfg);
            _config = cfg;
            return true;
        }
    }

    public ConnectedPage? UpdatePage(string id, Action<ConnectedPage> mutate)
    {
        lock (_sync)
        {
            var cfg = _config.Clone();
            var page = cfg.Pages.FirstOrDefault(p => p.Id == id || p.IgUserId == id);
            if (page is null) return null;
            mutate(page);
            Normalize(cfg);
            MirrorLegacy(cfg);
            Persist(cfg);
            _config = cfg;
            return page.Clone();
        }
    }

    /// <summary>Adds or updates an AI provider (matched by id, or by name when the id is new).</summary>
    public AiProvider UpsertProvider(AiProvider provider)
    {
        lock (_sync)
        {
            var cfg = _config.Clone();
            var existing = cfg.AiProviders.FirstOrDefault(p => p.Id == provider.Id);
            if (existing is null)
            {
                provider.Id = string.IsNullOrWhiteSpace(provider.Id) ? Guid.NewGuid().ToString("N") : provider.Id;
                cfg.AiProviders.Add(provider);
            }
            else
            {
                var key = existing.ApiKey;
                provider.ApiKey = MergeSecret(provider.ApiKey, key);
                provider.CreatedAt = existing.CreatedAt;
                provider.LastTestOk = existing.LastTestOk;
                provider.LastTestAt = existing.LastTestAt;
                provider.LastTestMessage = existing.LastTestMessage;
                // Measured usage is server-owned; a client edit must not reset it.
                provider.UsedTokens = existing.UsedTokens;
                provider.ReplyCount = existing.ReplyCount;
                cfg.AiProviders[cfg.AiProviders.IndexOf(existing)] = provider;
            }

            Normalize(cfg);
            MirrorLegacy(cfg);
            Persist(cfg);
            _config = cfg;
            return provider;
        }
    }

    /// <summary>Tehran calendar day (UTC+3:30, no DST) used for the daily budgets.</summary>
    public static string TodayTehran =>
        DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(210)).ToString("yyyy-MM-dd");

    private static void RollDay(ConnectedPage page)
    {
        var today = TodayTehran;
        if (page.UsageDate == today) return;
        page.UsageDate = today;
        page.RepliesToday = 0;
        page.TokensToday = 0;
    }

    /// <summary>True when the page already spent its daily budget; tells which one.</summary>
    public bool PageLimitReached(string? pageRef, out string? which)
    {
        which = null;
        if (string.IsNullOrWhiteSpace(pageRef)) return false;

        lock (_sync)
        {
            var page = _config.Pages.FirstOrDefault(x => x.IgUserId == pageRef || x.Id == pageRef);
            if (page is null) return false;

            RollDay(page);
            if (page.DailyReplyLimit > 0 && page.RepliesToday >= page.DailyReplyLimit)
            {
                which = "reply";
                Persist(_config);
                return true;
            }
            if (page.DailyTokenLimit > 0 && page.TokensToday >= page.DailyTokenLimit)
            {
                which = "token";
                Persist(_config);
                return true;
            }

            Persist(_config);
            return false;
        }
    }

    /// <summary>Accumulates measured token usage on the provider and on the page (day counters).</summary>
    public void RecordUsage(string? pageRef, string? providerId, int tokens, bool countedAsReply)
    {
        lock (_sync)
        {
            if (tokens < 0) tokens = 0;

            if (!string.IsNullOrWhiteSpace(providerId))
            {
                var provider = _config.AiProviders.FirstOrDefault(p => p.Id == providerId);
                if (provider is not null)
                {
                    provider.UsedTokens += tokens;
                    if (countedAsReply) provider.ReplyCount++;
                }
            }

            if (!string.IsNullOrWhiteSpace(pageRef))
            {
                var page = _config.Pages.FirstOrDefault(x => x.IgUserId == pageRef || x.Id == pageRef);
                if (page is not null)
                {
                    RollDay(page);
                    page.TokensToday += tokens;
                    if (countedAsReply) page.RepliesToday++;
                }
            }

            Persist(_config);
        }
    }

    /// <summary>Updates (and optionally resets) the daily budget of one page.</summary>
    public ConnectedPage? SetPageLimits(string id, int? replyLimit, long? tokenLimit, bool resetUsage)
    {
        return UpdatePage(id, p =>
        {
            if (replyLimit.HasValue) p.DailyReplyLimit = Math.Clamp(replyLimit.Value, 0, 100000);
            if (tokenLimit.HasValue) p.DailyTokenLimit = Math.Clamp(tokenLimit.Value, 0, 100_000_000_000L);
            if (resetUsage)
            {
                p.UsageDate = TodayTehran;
                p.RepliesToday = 0;
                p.TokensToday = 0;
            }
        });
    }

    public AiProvider? UpdateProvider(string id, Action<AiProvider> mutate)
    {
        lock (_sync)
        {
            var cfg = _config.Clone();
            var provider = cfg.AiProviders.FirstOrDefault(p => p.Id == id);
            if (provider is null) return null;
            mutate(provider);
            Normalize(cfg);
            MirrorLegacy(cfg);
            Persist(cfg);
            _config = cfg;
            return provider.Clone();
        }
    }

    public bool RemoveProvider(string id)
    {
        lock (_sync)
        {
            if (_config.AiProviders.Count <= 1) return false; // keep at least one
            var cfg = _config.Clone();
            var removed = cfg.AiProviders.RemoveAll(p => p.Id == id);
            if (removed == 0) return false;
            Normalize(cfg);
            MirrorLegacy(cfg);
            Persist(cfg);
            _config = cfg;
            return true;
        }
    }

    public AiProvider? FindProvider(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Current.AiProviders.FirstOrDefault()
            : Current.AiProviders.FirstOrDefault(p => p.Id == id) ?? Current.AiProviders.FirstOrDefault();

    public ConnectedPage? FindPage(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var pages = Current.Pages;
        return pages.FirstOrDefault(p => p.IgUserId == id) ?? pages.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// Removes every stored Instagram connection. Secrets need an explicit path like this:
    /// <see cref="Save"/> treats a blank secret as "keep the existing one".
    /// </summary>
    public void ClearInstagramConnection()
    {
        lock (_sync)
        {
            var cfg = _config.Clone();
            cfg.Instagram.AccessToken = "";
            cfg.Instagram.IgUserId = "";
            cfg.Instagram.Username = "";
            cfg.Instagram.TokenExpiresAt = null;
            cfg.Instagram.Mode = "sandbox";
            cfg.Pages.Clear();
            Persist(cfg);
            _config = cfg;
        }
    }

    /// <summary>
    /// Removes the saved handle/password of every browser account. A blank secret in
    /// <see cref="Save"/> means "keep the stored one", so forgetting needs its own path.
    /// </summary>
    public void ClearBrowserCredentials()
    {
        lock (_sync)
        {
            var cfg = _config.Clone();
            foreach (var page in cfg.Pages)
            {
                page.BrowserUsername = "";
                page.BrowserPassword = "";
                page.BrowserAutoLogin = false;
            }
            Persist(cfg);
            _config = cfg;
        }
    }

    private static string MergeSecret(string submitted, string existing)
    {
        if (string.IsNullOrWhiteSpace(submitted) || submitted == SecretBox.Mask) return existing;
        return submitted.Trim();
    }

    /// <summary>
    /// Moves the single-account browser login (and its Chrome profile) that older versions
    /// stored on <c>browser.*</c> onto the first connected page, so nothing is lost and from
    /// then on every account keeps its own window and its own credentials.
    /// </summary>
    private void MigrateLegacyCredentials(AppConfig cfg)
    {
        var legacy = cfg.Browser;
        var hasLegacy = !string.IsNullOrWhiteSpace(legacy.InstagramUsername) ||
                        !string.IsNullOrWhiteSpace(legacy.InstagramPassword);
        if (!hasLegacy && string.IsNullOrWhiteSpace(legacy.ProfileDirectory)) return;

        var target = cfg.Pages.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.BrowserUsername))
                     ?? cfg.Pages.FirstOrDefault();
        if (target is not null)
        {
            if (hasLegacy && string.IsNullOrWhiteSpace(target.BrowserUsername))
            {
                target.BrowserUsername = (legacy.InstagramUsername ?? "").Trim().TrimStart('@');
                target.BrowserPassword = legacy.InstagramPassword ?? "";
                target.BrowserAutoLogin = legacy.AutoLogin;
            }
            if (string.IsNullOrWhiteSpace(target.BrowserProfileDirectory) &&
                !string.IsNullOrWhiteSpace(legacy.ProfileDirectory))
            {
                target.BrowserProfileDirectory = legacy.ProfileDirectory;
            }
        }

        // A profile folder from the single-window days keeps the login the owner already did.
        if (target is not null && string.IsNullOrWhiteSpace(target.BrowserProfileDirectory))
        {
            var oldProfile = Path.Combine(_contentRoot, "data", "browser-profile");
            if (Directory.Exists(oldProfile)) target.BrowserProfileDirectory = oldProfile;
        }

        legacy.InstagramUsername = "";
        legacy.InstagramPassword = "";
        legacy.AutoLogin = false;
        legacy.ProfileDirectory = "";
    }

    private AppConfig Load()
    {
        var cfg = AtomicJson.Read<AppConfig>(_path) ?? new AppConfig();
        cfg.Instagram ??= new InstagramConfig();
        cfg.Ai ??= new AiConfig();
        cfg.Persona ??= new PersonaConfig();
        cfg.Guardrails ??= new GuardrailConfig();
        cfg.Browser ??= new BrowserConfig();
        cfg.Knowledge ??= new List<KnowledgeItem>();
        cfg.Pages ??= new List<ConnectedPage>();
        cfg.AiProviders ??= new List<AiProvider>();

        cfg.Instagram.AccessToken = _box.Unprotect(cfg.Instagram.AccessToken);
        cfg.Instagram.AppSecret = _box.Unprotect(cfg.Instagram.AppSecret);
        cfg.Ai.ApiKey = _box.Unprotect(cfg.Ai.ApiKey);
        cfg.Browser.InstagramPassword = _box.Unprotect(cfg.Browser.InstagramPassword);
        foreach (var page in cfg.Pages) page.AccessToken = _box.Unprotect(page.AccessToken);
        foreach (var page in cfg.Pages) page.BrowserPassword = _box.Unprotect(page.BrowserPassword);
        foreach (var provider in cfg.AiProviders) provider.ApiKey = _box.Unprotect(provider.ApiKey);

        MigrateLegacyCredentials(cfg);
        Normalize(cfg);
        MirrorLegacy(cfg);
        return cfg;
    }

    private void Persist(AppConfig cfg)
    {
        var stored = cfg.Clone();
        stored.Instagram.AccessToken = _box.Protect(stored.Instagram.AccessToken);
        stored.Instagram.AppSecret = _box.Protect(stored.Instagram.AppSecret);
        stored.Ai.ApiKey = _box.Protect(stored.Ai.ApiKey);
        foreach (var page in stored.Pages) page.AccessToken = _box.Protect(page.AccessToken);
        foreach (var page in stored.Pages) page.BrowserPassword = _box.Protect(page.BrowserPassword);
        foreach (var provider in stored.AiProviders) provider.ApiKey = _box.Protect(provider.ApiKey);
        AtomicJson.Write(_path, stored);
    }

    /// <summary>
    /// Keeps the legacy single-account fields (<c>instagram.igUserId</c>, <c>ai.*</c>) as a mirror of
    /// the first connected page / first AI provider, so older code paths keep working.
    /// Also migrates an old single-page config into the pages list.
    /// </summary>
    private static void MirrorLegacy(AppConfig cfg)
    {
        // Migrate: a previously connected single account becomes the first page.
        if (cfg.Pages.Count == 0 &&
            !string.IsNullOrWhiteSpace(cfg.Instagram.IgUserId) &&
            !string.IsNullOrWhiteSpace(cfg.Instagram.AccessToken))
        {
            cfg.Pages.Add(new ConnectedPage
            {
                IgUserId = cfg.Instagram.IgUserId,
                Username = cfg.Instagram.Username,
                AccessToken = cfg.Instagram.AccessToken,
                TokenExpiresAt = cfg.Instagram.TokenExpiresAt,
                ConnectedAt = DateTimeOffset.UtcNow
            });
        }

        if (cfg.AiProviders.Count == 0 && !string.IsNullOrWhiteSpace(cfg.Ai.ApiKey))
        {
            cfg.AiProviders.Add(AiProvider.FromAiConfig(cfg.Ai, "پیش‌فرض"));
        }

        // Mirror the primary page into the legacy fields.
        var primary = cfg.Pages.FirstOrDefault();
        if (primary is not null)
        {
            cfg.Instagram.IgUserId = primary.IgUserId;
            cfg.Instagram.Username = primary.Username;
            cfg.Instagram.AccessToken = primary.AccessToken;
            cfg.Instagram.TokenExpiresAt = primary.TokenExpiresAt;
        }
        else
        {
            cfg.Instagram.IgUserId = "";
            cfg.Instagram.Username = "";
            cfg.Instagram.AccessToken = "";
            cfg.Instagram.TokenExpiresAt = null;
        }

        // Mirror the default (first) provider into the legacy ai.* block.
        var defaultProvider = cfg.AiProviders.FirstOrDefault();
        if (defaultProvider is not null)
        {
            cfg.Ai = defaultProvider.ToAiConfig();
        }

        // A page must never point at a provider that no longer exists.
        var knownProviders = cfg.AiProviders.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var page in cfg.Pages)
        {
            if (!string.IsNullOrWhiteSpace(page.AiId) && !knownProviders.Contains(page.AiId))
            {
                page.AiId = "";
            }
        }
    }

    private static void Normalize(AppConfig cfg)
    {
        cfg.Pages ??= new List<ConnectedPage>();
        cfg.AiProviders ??= new List<AiProvider>();
        cfg.Pages = cfg.Pages.Where(p => p is not null &&
            (!string.IsNullOrWhiteSpace(p.IgUserId) || !string.IsNullOrWhiteSpace(p.BrowserUsername))).ToList();
        cfg.AiProviders = cfg.AiProviders.Where(p => p is not null).ToList();
        foreach (var page in cfg.Pages)
        {
            page.Id = string.IsNullOrWhiteSpace(page.Id) ? Guid.NewGuid().ToString("N") : page.Id;
            page.Username = (page.Username ?? "").Trim().TrimStart('@');
            page.AiId = (page.AiId ?? "").Trim();
            page.LastTestMessage = page.LastTestMessage ?? "";
            page.BrowserUsername = (page.BrowserUsername ?? "").Trim().TrimStart('@');
            page.BrowserPassword ??= "";
            page.BrowserProfileDirectory = (page.BrowserProfileDirectory ?? "").Trim();
            page.CommentBot ??= new CommentBotConfig();
            NormalizeCommentBot(page.CommentBot);
            page.DailyReplyLimit = Math.Clamp(page.DailyReplyLimit, 0, 100000);
            page.DailyTokenLimit = Math.Clamp(page.DailyTokenLimit, 0, 100_000_000_000L);
            page.RepliesToday = Math.Max(0, page.RepliesToday);
            page.TokensToday = Math.Max(0, page.TokensToday);
            page.UsageDate = page.UsageDate ?? "";
        }
        foreach (var provider in cfg.AiProviders)
        {
            provider.Id = string.IsNullOrWhiteSpace(provider.Id) ? Guid.NewGuid().ToString("N") : provider.Id;
            provider.Name = string.IsNullOrWhiteSpace(provider.Name) ? "سرویس AI" : provider.Name.Trim();
            provider.BaseUrl = Clean(provider.BaseUrl, "https://api.openai.com/v1");
            provider.Model = Clean(provider.Model, "gpt-4o-mini");
            provider.ScopeCheckModel ??= "";
            provider.Temperature = Math.Clamp(provider.Temperature, 0, 2);
            provider.MaxTokens = Math.Clamp(provider.MaxTokens, 50, 4000);
            provider.TimeoutSeconds = Math.Clamp(provider.TimeoutSeconds, 5, 120);
            provider.LastTestMessage = provider.LastTestMessage ?? "";
            provider.TokenBalance = Math.Clamp(provider.TokenBalance, 0, 100_000_000_000L);
            provider.UsedTokens = Math.Max(0, provider.UsedTokens);
            provider.ReplyCount = Math.Max(0, provider.ReplyCount);
        }

        cfg.Instagram.Mode = (cfg.Instagram.Mode ?? "").Trim().ToLowerInvariant() switch
        {
            "live" => "live",
            "browser" => "browser",
            _ => "sandbox"
        };
        cfg.Instagram.AppId = (cfg.Instagram.AppId ?? "").Trim();
        cfg.Instagram.RedirectUri = (cfg.Instagram.RedirectUri ?? "").Trim();
        cfg.Instagram.GraphBaseUrl = Clean(cfg.Instagram.GraphBaseUrl, "https://graph.instagram.com");
        cfg.Instagram.ApiVersion = string.IsNullOrWhiteSpace(cfg.Instagram.ApiVersion) ? "v23.0" : cfg.Instagram.ApiVersion.Trim();
        cfg.Instagram.VerifyToken = string.IsNullOrWhiteSpace(cfg.Instagram.VerifyToken) ? "instamind-verify" : cfg.Instagram.VerifyToken.Trim();

        cfg.Ai.BaseUrl = Clean(cfg.Ai.BaseUrl, "https://api.openai.com/v1");
        cfg.Ai.Model = Clean(cfg.Ai.Model, "gpt-4o-mini");
        cfg.Ai.ScopeCheckModel ??= "";
        cfg.Ai.Temperature = Math.Clamp(cfg.Ai.Temperature, 0, 2);
        cfg.Ai.MaxTokens = Math.Clamp(cfg.Ai.MaxTokens, 50, 4000);
        cfg.Ai.TimeoutSeconds = Math.Clamp(cfg.Ai.TimeoutSeconds, 5, 120);

        var browser = cfg.Browser ??= new BrowserConfig();
        browser.ChromeBinaryPath = (browser.ChromeBinaryPath ?? "").Trim();
        browser.ProfileDirectory = (browser.ProfileDirectory ?? "").Trim();
        browser.InboxUrl = string.IsNullOrWhiteSpace(browser.InboxUrl)
            ? "https://www.instagram.com/direct/inbox/"
            : browser.InboxUrl.Trim();
        browser.ThreadLinkSelector = string.IsNullOrWhiteSpace(browser.ThreadLinkSelector)
            ? "a[href*=\"/direct/t/\"]"
            : browser.ThreadLinkSelector.Trim();
        browser.ComposerSelector = string.IsNullOrWhiteSpace(browser.ComposerSelector)
            ? "div[contenteditable=\"true\"][role=\"textbox\"]"
            : browser.ComposerSelector.Trim();
        browser.MessageTextSelector = string.IsNullOrWhiteSpace(browser.MessageTextSelector)
            ? "div[dir=\"auto\"]"
            : browser.MessageTextSelector.Trim();
        browser.CommentSelector = string.IsNullOrWhiteSpace(browser.CommentSelector)
            ? "article ul li"
            : browser.CommentSelector.Trim();
        browser.WindowWidth = Math.Clamp(browser.WindowWidth, 900, 2560);
        browser.WindowHeight = Math.Clamp(browser.WindowHeight, 600, 1600);
        browser.ScanIntervalSeconds = Math.Clamp(browser.ScanIntervalSeconds, 10, 3600);
        browser.ScanJitterSeconds = Math.Clamp(browser.ScanJitterSeconds, 0, 600);
        browser.MaxThreadsPerScan = Math.Clamp(browser.MaxThreadsPerScan, 1, 10);
        browser.MinGapBetweenSendsSeconds = Math.Clamp(browser.MinGapBetweenSendsSeconds, 5, 3600);
        browser.ConversationCooldownMinutes = Math.Clamp(browser.ConversationCooldownMinutes, 0, 1440);
        browser.TypingMinMsPerChar = Math.Clamp(browser.TypingMinMsPerChar, 15, 400);
        browser.TypingMaxMsPerChar = Math.Clamp(browser.TypingMaxMsPerChar, browser.TypingMinMsPerChar + 5, 800);
        browser.ThinkMinMs = Math.Clamp(browser.ThinkMinMs, 300, 120000);
        browser.ThinkMaxMs = Math.Clamp(browser.ThinkMaxMs, browser.ThinkMinMs, 300000);
        browser.DailySendCap = Math.Clamp(browser.DailySendCap, 0, 500);
        browser.BackoffSeconds = Math.Clamp(browser.BackoffSeconds, 30, 7200);
        browser.WorkingHoursStart = NormalizeClock(browser.WorkingHoursStart, "09:00");
        browser.WorkingHoursEnd = NormalizeClock(browser.WorkingHoursEnd, "23:30");
        browser.SendsDate = browser.SendsDate ?? "";
        browser.SendsToday = Math.Max(0, browser.SendsToday);

        browser.InstagramUsername = (browser.InstagramUsername ?? "").Trim().TrimStart('@');
        browser.InstagramPassword ??= "";
        browser.ProfileDirectory = (browser.ProfileDirectory ?? "").Trim();
        browser.ResponseProfile = (browser.ResponseProfile ?? "").Trim().ToLowerInvariant() switch
        {
            "fast" => "fast",
            "balanced" => "balanced",
            _ => "calm"
        };

        cfg.Persona.MaxChars = Math.Clamp(cfg.Persona.MaxChars, 60, 900);
        cfg.Persona.AllowedTopics = CleanList(cfg.Persona.AllowedTopics);
        cfg.Persona.ForbiddenTopics = CleanList(cfg.Persona.ForbiddenTopics);
        cfg.Guardrails.BlockedKeywords = CleanList(cfg.Guardrails.BlockedKeywords);
        cfg.Guardrails.MaxRepliesPerHour = Math.Clamp(cfg.Guardrails.MaxRepliesPerHour, 1, 200);
        cfg.Knowledge = cfg.Knowledge
            .Where(k => !string.IsNullOrWhiteSpace(k.Title) || !string.IsNullOrWhiteSpace(k.Content))
            .ToList();
    }

    private static string Clean(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void NormalizeCommentBot(CommentBotConfig bot)
    {
        bot.PostUrl = (bot.PostUrl ?? "").Trim();
        bot.Keyword = (bot.Keyword ?? "").Trim();
        bot.FollowPrompt = (bot.FollowPrompt ?? "").Trim();
        bot.Message = bot.Message ?? "";
        bot.MaxPerDay = Math.Clamp(bot.MaxPerDay, 0, 500);
        bot.CooldownMinutes = Math.Clamp(bot.CooldownMinutes, 0, 1440);
        bot.CheckIntervalMinutes = Math.Clamp(bot.CheckIntervalMinutes, 1, 1440);
        bot.MaxPerPass = Math.Clamp(bot.MaxPerPass, 1, 20);
    }

    private static string NormalizeClock(string? value, string fallback) =>
        TimeOnly.TryParse(value, out var parsed) ? parsed.ToString("HH:mm") : fallback;

    private static List<string> CleanList(List<string>? items) =>
        (items ?? new List<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Mask(string value) => string.IsNullOrEmpty(value) ? "" : SecretBox.Mask;
}
