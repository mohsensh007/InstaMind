using InstaMind.Models;
using OpenQA.Selenium;

namespace InstaMind.Services.Browser;

/// <summary>Snapshot returned to the UI (one per connected page).</summary>
public sealed class BrowserStatus
{
    public string PageId { get; set; } = "";
    public string PageUsername { get; set; } = "";
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "sandbox";
    public bool Running { get; set; }
    public string State { get; set; } = "stopped";
    public string Message { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
    public DateTimeOffset? NextScanAt { get; set; }
    public int ThreadsSeen { get; set; }
    public int RepliesSent { get; set; }
    public int SendsToday { get; set; }
    public int DailySendCap { get; set; }
    public bool InWorkingHours { get; set; } = true;
    public string WorkingHours { get; set; } = "";
    public string LocalTime { get; set; } = "";
    public string? LastError { get; set; }
    public bool WaitingForLogin { get; set; }

    /// <summary>The window is asking for the one-time code Instagram sent.</summary>
    public bool NeedsCode { get; set; }
    public string CurrentUrl { get; set; } = "";
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record BrowserReady(bool Ready, string State, string Message);

/// <summary>
/// Orchestrates browser mode for one connected page: owns that page's Chrome window and
/// profile, decides whether it is a good moment to look at the inbox or to send something,
/// and publishes a status the panel can poll. Two pages never share a window, a profile or
/// a counter.
///
/// Every driver interaction goes through one lock — one window, one action at a time.
/// </summary>
public sealed class BrowserRuntime : IAsyncDisposable
{
    private readonly ConfigService _config;
    private readonly BrowserSession _session;
    private readonly BrowserStateStore _state;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BrowserRuntime> _log;
    private readonly string _pageId;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private string _stateName = "stopped";
    private string _message = "مرورگر بسته است.";
    private string _username = "";
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastScanAt;
    private DateTimeOffset? _nextScanAt;
    private DateTimeOffset? _nextCommentAt;
    private int _threadsSeen;
    private int _repliesSent;
    private string? _lastError;

    public BrowserRuntime(
        string pageId,
        ConfigService config,
        BrowserSession session,
        BrowserStateStore state,
        IWebHostEnvironment env,
        ILogger<BrowserRuntime> log)
    {
        _pageId = pageId;
        _config = config;
        _session = session;
        _state = state;
        _env = env;
        _log = log;
    }

    /// <summary>Connected page this window belongs to.</summary>
    public string PageId => _pageId;

    /// <summary>Handle of that page, for labels in logs and the panel.</summary>
    public string PageUsername => Page?.Username ?? "";

    /// <summary>The page itself, when it still exists in the config.</summary>
    public ConnectedPage? Page => _config.Current.Pages.FirstOrDefault(p => p.Id == _pageId);

    public BrowserStateStore Store => _state;

    public bool IsRunning => _session.IsRunning;

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    public BrowserStatus Status()
    {
        var cfg = _config.Current.Browser;
        var page = Page;
        var now = DateTimeOffset.UtcNow;
        var needsCode = false;
        try { needsCode = _session.IsRunning && _session.IsVerificationPage(); }
        catch { needsCode = false; }

        lock (_sync)
        {
            var (day, count) = _state.SendsToday;
            return new BrowserStatus
            {
                PageId = _pageId,
                PageUsername = page?.Username ?? "",
                Enabled = cfg.Enabled && (page?.BrowserEnabled ?? true),
                Mode = _config.Current.Instagram.Mode,
                Running = _session.IsRunning,
                State = _stateName,
                Message = _message,
                Username = _username,
                StartedAt = _startedAt,
                LastScanAt = _lastScanAt,
                NextScanAt = _nextScanAt,
                ThreadsSeen = _threadsSeen,
                RepliesSent = _repliesSent,
                SendsToday = day == HumanPacing.TehranDay(now) ? count : 0,
                DailySendCap = cfg.DailySendCap,
                InWorkingHours = HumanPacing.WithinWorkingHours(cfg, now),
                WorkingHours = cfg.AlwaysOn ? "۲۴ ساعته" : $"{cfg.WorkingHoursStart}–{cfg.WorkingHoursEnd}",
                LocalTime = HumanPacing.LocalClock(now),
                LastError = _lastError,
                WaitingForLogin = _stateName == "waiting_login",
                NeedsCode = needsCode || _stateName == "waiting_code",
                CurrentUrl = _session.CurrentUrl
            };
        }
    }

    public void SetState(string state, string message)
    {
        lock (_sync)
        {
            _stateName = state;
            _message = message;
        }
    }

    public void NoteScan(int threads, TimeSpan nextIn)
    {
        lock (_sync)
        {
            _lastScanAt = DateTimeOffset.UtcNow;
            _nextScanAt = _lastScanAt + nextIn;
            _threadsSeen = threads;
        }
    }

    /// <summary>When the next pass for this page may run.</summary>
    public void ScheduleNext(TimeSpan delay)
    {
        lock (_sync) _nextScanAt = DateTimeOffset.UtcNow + delay;
    }

    /// <summary>True when this page is waiting for its turn (never checked before, or due now).</summary>
    public bool IsDue()
    {
        lock (_sync) return _nextScanAt is null || _nextScanAt <= DateTimeOffset.UtcNow;
    }

    /// <summary>The comment bot of this page has its own timer, independent of the inbox scan.</summary>
    public void ScheduleComments(TimeSpan delay)
    {
        lock (_sync) _nextCommentAt = DateTimeOffset.UtcNow + delay;
    }

    public bool CommentsDue()
    {
        lock (_sync) return _nextCommentAt is null || _nextCommentAt <= DateTimeOffset.UtcNow;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>Saved handle/password of this page's own window (empty when nothing is saved).</summary>
    private (string Username, string Password, bool AutoLogin) Credentials()
    {
        var page = Page;
        if (page is null) return ("", "", false);
        return (page.BrowserUsername ?? "", page.BrowserPassword ?? "", page.BrowserAutoLogin);
    }

    /// <summary>Profile folder of this window; every account keeps its own.</summary>
    private string ResolveProfileDirectory()
    {
        var configured = Page?.BrowserProfileDirectory;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return Path.Combine(_env.ContentRootPath, "data", "browser-profiles", _pageId);
    }

    public async Task<BrowserStatus> StartAsync(CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        lock (_sync) _startedAt = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct);
        try
        {
            SetState("starting", "در حال باز کردن پنجرهٔ کروم…");
            await _session.StartAsync(cfg, _env.ContentRootPath, ct, ResolveProfileDirectory());
            await _session.NavigateInboxAsync(cfg, ct);

            // Optional: sign in with this page's saved handle and password. A one-time code
            // or a challenge is never solved here — it is asked for in the panel instead.
            var (username, password, autoLogin) = Credentials();
            if (autoLogin && _session.IsLoginPage() &&
                !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                SetState("starting", "صفحهٔ ورود آمد؛ در حال ورود با حساب ذخیره‌شده…");
                var (loggedIn, message) = await _session.LoginAsync(cfg, username, password, ct);

                if (loggedIn)
                {
                    await _session.NavigateInboxAsync(cfg, ct);
                    lock (_sync) _lastError = null;
                }
                else
                {
                    lock (_sync) _lastError = message;
                    _log.LogWarning("Automatic login stopped: {Reason}", message);
                }
            }

            return await ProbeAsync(ct, attempts: 4);
        }
        catch (Exception ex) when (ex is WebDriverException or InvalidOperationException or IOException)
        {
            _log.LogWarning(ex, "Chrome could not be started");
            var friendly = FriendlyStartError(ex);
            lock (_sync) _lastError = friendly;
            SetState("error", friendly);
            await SafeStopAsync();
            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Explicit "log in now" from the panel, using the saved credentials.</summary>
    public async Task<(bool Ok, string Message, BrowserStatus Status)> LoginWithSavedCredentialsAsync(
        CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        var (username, password, _) = Credentials();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "یوزرنیم و پسورد ذخیره نشده‌اند؛ اول در همین صفحه ذخیره کن.", Status());

        if (!_session.IsRunning)
        {
            var started = await StartAsync(ct);
            if (!started.Running) return (false, started.Message, started);
            if (started.State == "ready") return (true, "از قبل وارد شده‌ای.", started);
        }

        await _gate.WaitAsync(ct);
        try
        {
            SetState("starting", "در حال ورود با حساب ذخیره‌شده…");
            var (ok, message) = await _session.LoginAsync(cfg, username, password, ct);

            if (!ok)
            {
                lock (_sync) _lastError = message;
                // Instagram asked for a one-time code: the panel shows the box for it.
                if (_session.IsVerificationPage())
                {
                    SetState("waiting_code",
                        "اینستاگرام کد تأیید فرستاد (ایمیل یا برنامهٔ احراز هویت)؛ کد را در همین صفحه وارد کن.");
                    return (false, "کد تأیید لازم است.", Status());
                }
                SetState("waiting_login", message);
                return (false, message, Status());
            }

            await _session.NavigateInboxAsync(cfg, ct);
            var handle = string.IsNullOrWhiteSpace(_username) ? _session.DetectUsername() : _username;
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(handle)) _username = handle;
                _stateName = "ready";
                _message = string.IsNullOrWhiteSpace(handle) ? "ورود انجام شد." : $"وارد شدی: @{handle}";
                _lastError = null;
            }
            SyncPageHandle(handle);
            return (true, _message, Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BrowserStatus> StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await SafeStopAsync();
            lock (_sync)
            {
                _username = "";
                _startedAt = null;
                _nextScanAt = null;
            }
            SetState("stopped", "مرورگر بسته شد.");
            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SafeStopAsync()
    {
        try { await _session.StopAsync(); }
        catch (Exception ex) { _log.LogDebug(ex, "Stopping Chrome failed"); }
    }

    /// <summary>Sends the one-time code Instagram asked for (e-mail or authenticator app).</summary>
    public async Task<(bool Ok, string Message)> SubmitCodeAsync(string code, CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsRunning)
                return (false, "مرورگر باز نیست؛ اول «روشن کردن مرورگر» را بزن.");

            SetState("starting", "در حال فرستادن کد تأیید…");
            var (ok, message) = await _session.SubmitCodeAsync(cfg, code, ct);
            if (!ok)
            {
                lock (_sync) _lastError = message;
                if (_session.IsVerificationPage())
                {
                    SetState("waiting_code", message);
                    return (false, message);
                }
                SetState("error", message);
                return (false, message);
            }

            await _session.NavigateInboxAsync(cfg, ct);
            var handle = _session.DetectUsername();
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(handle)) _username = handle;
                _stateName = "ready";
                _message = string.IsNullOrWhiteSpace(handle) ? "کد پذیرفته شد؛ ورود انجام شد." : $"وارد شدی: @{handle}";
                _lastError = null;
            }
            SyncPageHandle(handle);
            return (true, _message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Remembers which account actually signed in to this window.</summary>
    private void SyncPageHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return;
        var page = Page;
        if (page is null) return;

        var changed = !string.Equals(page.Username, handle, StringComparison.OrdinalIgnoreCase) ||
                      string.IsNullOrWhiteSpace(page.IgUserId);
        if (!changed) return;

        try
        {
            _config.UpdatePage(page.Id, p =>
            {
                p.Username = handle;
                if (string.IsNullOrWhiteSpace(p.IgUserId)) p.IgUserId = handle;
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not store the signed-in handle");
        }
    }

    /// <summary>Checks the window state; starts Chrome when needed.</summary>
    public async Task<BrowserReady> EnsureReadyAsync(CancellationToken ct)
    {
        if (!_session.IsRunning)
        {
            var started = await StartAsync(ct);
            return new BrowserReady(started.State == "ready", started.State, started.Message);
        }

        if (_session.IsVerificationPage())
        {
            SetState("waiting_code",
                "اینستاگرام کد تأیید می‌خواهد (ایمیل یا برنامهٔ احراز هویت)؛ کد را در پنل وارد کن.");
            return new BrowserReady(false, "waiting_code", _message);
        }

        if (_session.IsLoginPage())
        {
            SetState("waiting_login", "در پنجرهٔ کروم وارد اینستاگرام شو؛ بعد از ورود خودش ادامه می‌دهد.");
            return new BrowserReady(false, "waiting_login", _message);
        }

        if (string.IsNullOrWhiteSpace(_username))
        {
            await _gate.WaitAsync(ct);
            try
            {
                var handle = _session.DetectUsername();
                if (!string.IsNullOrWhiteSpace(handle))
                {
                    lock (_sync)
                    {
                        _username = handle;
                        _stateName = "ready";
                        _message = $"وارد شدی: @{handle}";
                        _lastError = null;
                    }
                    SyncPageHandle(handle);
                }
                else
                {
                    SetState("ready", "مرورگر باز است ولی هنوز صفحهٔ دایرکت آماده نیست.");
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return new BrowserReady(_stateName == "ready", _stateName, _message);
    }

    /// <summary>Reads the fresh window state: login page, signed in, or still loading.</summary>
    private async Task<BrowserStatus> ProbeAsync(CancellationToken ct, int attempts)
    {
        var cfg = _config.Current.Browser;

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            if (_session.IsVerificationPage())
            {
                SetState("waiting_code",
                    "اینستاگرام کد تأیید فرستاده (ایمیل یا برنامهٔ احراز هویت)؛ کد را در پنل وارد کن.");
                return Status();
            }

            if (_session.IsLoginPage())
            {
                SetState("waiting_login",
                    "در پنجرهٔ کروم وارد اینستاگرام شو (کد دو مرحله‌ای هم همان‌جا). بعد از ورود، خودکار ادامه می‌دهد.");
                return Status();
            }

            var handle = _session.DetectUsername();
            if (!string.IsNullOrWhiteSpace(handle))
            {
                lock (_sync)
                {
                    _username = handle;
                    _stateName = "ready";
                    _message = $"وارد شدی: @{handle}";
                    _lastError = null;
                }
                SyncPageHandle(handle);
                return Status();
            }

            if (attempt == 0)
            {
                await _session.NavigateInboxAsync(cfg, ct);
            }
            await Task.Delay(800, ct);
        }

        SetState("ready", "مرورگر باز است. اگر صفحهٔ ورود را می‌بینی، در همان پنجره وارد شو.");
        return Status();
    }

    // ------------------------------------------------------------------
    // Pacing decisions
    // ------------------------------------------------------------------

    public bool DailyCapReached()
    {
        var cfg = _config.Current.Browser;
        if (cfg.DailySendCap <= 0) return false;
        var (day, count) = _state.SendsToday;
        return day == HumanPacing.TehranDay(DateTimeOffset.UtcNow) && count >= cfg.DailySendCap;
    }

    /// <summary>True when nothing stands in the way of answering this conversation right now.</summary>
    public bool CanReplyNow(string threadId, out string reason)
    {
        var cfg = _config.Current.Browser;
        reason = "";

        if (_config.Current.Instagram.Mode != "browser")
        {
            reason = "حالت اتصال روی «مرورگر» نیست.";
            return false;
        }
        if (!cfg.Enabled)
        {
            reason = "کارگر مرورگر خاموش است.";
            return false;
        }
        if (Page is { BrowserEnabled: false })
        {
            reason = "پاسخ‌دهی خودکار این صفحه در حالت مرورگر خاموش است.";
            return false;
        }
        if (!HumanPacing.WithinWorkingHours(cfg, DateTimeOffset.UtcNow))
        {
            reason = $"خارج از ساعت کاری ({cfg.WorkingHoursStart}–{cfg.WorkingHoursEnd}).";
            return false;
        }
        if (DailyCapReached())
        {
            reason = $"سقف پیام امروز ({cfg.DailySendCap}) پر شده است.";
            return false;
        }

        var gap = _state.GlobalGapLeft(cfg.MinGapBetweenSendsSeconds);
        if (gap > TimeSpan.Zero)
        {
            reason = $"فاصلهٔ بین دو پیام: {Math.Ceiling(gap.TotalSeconds)} ثانیهٔ دیگر.";
            return false;
        }

        var cooldown = _state.CooldownLeft(threadId, cfg.ConversationCooldownMinutes);
        if (cooldown > TimeSpan.Zero)
        {
            reason = $"این گفتگو تا {Math.Ceiling(cooldown.TotalMinutes)} دقیقهٔ دیگر پاسخ نمی‌گیرد.";
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Driver actions (serialised)
    // ------------------------------------------------------------------

    public async Task<List<SidebarThread>> ScanSidebarAsync(CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsRunning) return new List<SidebarThread>();

            SetState("scanning", "در حال خواندن لیست دایرکت…");
            if (_session.IsLoginPage())
            {
                SetState("waiting_login", "اینستاگرام از حساب خارج شده؛ در پنجرهٔ کروم دوباره وارد شو.");
                return new List<SidebarThread>();
            }

            var url = _session.CurrentUrl;
            if (!url.Contains("/direct/", StringComparison.OrdinalIgnoreCase))
            {
                await _session.NavigateInboxAsync(cfg, ct);
            }

            var threads = _session.ScanSidebar(cfg);
            if (threads.Count == 0)
            {
                await Task.Delay(1200, ct);
                threads = _session.ScanSidebar(cfg);
            }

            SetState("ready", $"لیست دایرکت خوانده شد ({threads.Count} گفتگو).");
            return threads;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThreadSnapshot?> ReadThreadAsync(string threadId, CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsRunning) return null;
            var snapshot = await _session.ReadThreadAsync(cfg, threadId, ct);
            if (snapshot is not null) SetState("scanning", "گفتگو خوانده شد؛ در حال بررسی…");
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Types and sends one reply, after a thinking pause.</summary>
    // ------------------------------------------------------------------
    // Comment bot actions (serialised like everything else)
    // ------------------------------------------------------------------

    /// <summary>Reads the comments of this page's post.</summary>
    public async Task<List<PostComment>> ReadCommentsAsync(string postUrl, CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsRunning) return new List<PostComment>();
            return await _session.ReadCommentsAsync(cfg, postUrl, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Does the commenter follow this account? Null = could not tell.</summary>
    public async Task<bool?> CheckFollowsAsync(string username, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsRunning) return null;
            return await _session.CheckFollowsAsync(username, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens a DM with the commenter (existing thread or a new one).</summary>
    public async Task<string?> OpenThreadAsync(string username, CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsRunning) return null;
            return await _session.OpenThreadWithAsync(cfg, username, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SendResult> SendReplyAsync(string threadId, string text, CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        if (_config.Current.Instagram.Mode != "browser")
            return new SendResult(false, "حالت اتصال روی «مرورگر» نیست.");
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(text))
            return new SendResult(false, "گیرنده یا متن پیام خالی است.");
        if (!CanReplyNow(threadId, out var reason))
            return new SendResult(false, reason);

        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsRunning)
                return new SendResult(false, "مرورگر باز نیست.");

            // Read, decide, then start typing — a person never answers instantly.
            var think = HumanPacing.Between(cfg.ThinkMinMs, cfg.ThinkMaxMs);
            SetState("typing", "در حال خواندن و نوشتن پاسخ…");
            await Task.Delay(think, ct);

            var (ok, error) = await _session.SendAsync(cfg, threadId, text, ct);
            if (!ok)
            {
                lock (_sync) _lastError = error;
                SetState("ready", error ?? "ارسال ناموفق بود.");
                return new SendResult(false, error);
            }

            _state.RecordSend(threadId);
            lock (_sync) _repliesSent++;
            SetState("ready", "پاسخ ارسال شد.");
            return new SendResult(true, null, "web:" + threadId);
        }
        catch (WebDriverException ex)
        {
            _log.LogWarning(ex, "Browser send failed");
            var message = "ارسال با خطای مرورگر متوقف شد.";
            lock (_sync) _lastError = message;
            SetState("error", message);
            return new SendResult(false, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public InboxDiagnostics Diagnose() => _session.Diagnose(_config.Current.Browser);

    /// <summary>Used by the panel button: opens Chrome, reports what it can see.</summary>
    public async Task<(ConnectionTest Result, BrowserStatus Status)> TestAsync(CancellationToken ct)
    {
        if (!_session.IsRunning)
        {
            var started = await StartAsync(ct);
            if (started.State != "ready")
                return (new ConnectionTest(false, started.Message, started.Username), started);
        }

        var diagnostics = Diagnose();
        if (diagnostics.LoginPage)
        {
            SetState("waiting_login", "اینستاگرام صفحهٔ ورود نشان می‌دهد؛ در پنجرهٔ کروم وارد شو.");
            return (new ConnectionTest(false, "باید در پنجرهٔ کروم وارد شوی.", diagnostics.Title), Status());
        }
        if (!string.IsNullOrWhiteSpace(diagnostics.Error))
            return (new ConnectionTest(false, "خطای مرورگر: " + diagnostics.Error), Status());

        var handle = string.IsNullOrWhiteSpace(_username) ? _session.DetectUsername() : _username;
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(handle)) _username = handle;
            _stateName = "ready";
            _message = diagnostics.Threads > 0
                ? $"اتصال برقرار است؛ {diagnostics.Threads} گفتگو در لیست دایرکت."
                : "اتصال برقرار است؛ لیست دایرکت خالی یا هنوز بارگذاری نشده.";
        }

        var detail = diagnostics.Threads > 0
            ? $"مرورگر وصل است: {diagnostics.Threads} گفتگو در لیست دایرکت."
            : "مرورگر وصل است ولی لیستی دیده نشد؛ یک بار صفحهٔ دایرکت را در پنجرهٔ کروم باز کن.";
        return (new ConnectionTest(true, detail, string.IsNullOrWhiteSpace(handle) ? null : handle), Status());
    }

    public async ValueTask DisposeAsync()
    {
        await SafeStopAsync();
        _gate.Dispose();
    }

    private static string FriendlyStartError(Exception ex)
    {
        var raw = ex.Message ?? "";
        if (raw.Contains("user data directory is already in use", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("cannot create default profile directory", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("SingletonLock", StringComparison.OrdinalIgnoreCase))
        {
            return "پوشهٔ پروفایل مرورگر در حال استفاده است. همهٔ پنجره‌های کرومی که با این پروفایل بازند را ببند (یا در تنظیمات یک پوشهٔ پروفایل دیگر بده) و دوباره تلاش کن.";
        }
        if (raw.Contains("session not created", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Chrome failed to start", StringComparison.OrdinalIgnoreCase))
        {
            return "کروم بالا نیامد. مطمئن شو Chrome نصب است و مسیر chrome.exe درست است. (" + Shorten(raw, 160) + ")";
        }
        return "باز کردن مرورگر ممکن نشد: " + Shorten(raw, 220);
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
