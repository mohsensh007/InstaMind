using System.Text;
using InstaMind.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Interactions;

namespace InstaMind.Services.Browser;

/// <summary>One conversation as it appears in the direct-inbox sidebar.</summary>
public sealed record SidebarThread(string ThreadId, string Username, string Preview, int Position);

/// <summary>The last bubble of an open conversation.</summary>
public sealed record ThreadSnapshot(int Count, string LastText, bool IsIncoming);

/// <summary>One comment as it appears under a post.</summary>
public sealed record PostComment(string Username, string Text, string Key);

/// <summary>What the browser currently shows, for the diagnostics panel.</summary>
public sealed record InboxDiagnostics(
    bool Running,
    bool LoginPage,
    int Threads,
    bool HasComposer,
    string Url,
    string Title,
    string Error,
    string Snippet = "");

/// <summary>
/// A real Chrome window driven by Selenium. It uses its own persistent profile, so you sign
/// in once by hand inside that window and the session survives restarts; the app never sees
/// the password.
///
/// Deliberately plain: no user-agent rewriting, no navigator/WebRTC patches, no automation
/// hiding flags. It is a normal browser doing normal things slowly, which is also the only
/// thing that keeps the code honest about what it is.
///
/// Not thread safe: <see cref="BrowserRuntime"/> serialises every call.
/// </summary>
public sealed class BrowserSession
{
    private readonly ILogger<BrowserSession> _log;
    private IWebDriver? _driver;
    private string _currentThreadId = "";

    public BrowserSession(ILogger<BrowserSession> log) => _log = log;

    public bool IsRunning => _driver is not null;

    public string CurrentUrl
    {
        get
        {
            try { return _driver?.Url ?? ""; }
            catch { return ""; }
        }
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public Task StartAsync(BrowserConfig cfg, string contentRoot, CancellationToken ct, string? profileDirectory = null)
    {
        if (_driver is not null) return Task.CompletedTask;

        var profile = !string.IsNullOrWhiteSpace(profileDirectory)
            ? profileDirectory
            : string.IsNullOrWhiteSpace(cfg.ProfileDirectory)
                ? Path.Combine(contentRoot, "data", "browser-profile")
                : cfg.ProfileDirectory;
        Directory.CreateDirectory(profile);

        var options = new ChromeOptions();
        options.AddArgument($"--user-data-dir={profile}");
        options.AddArgument("--profile-directory=Default");
        options.AddArgument("--no-first-run");
        options.AddArgument("--no-default-browser-check");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--disable-popup-blocking");
        options.AddArgument("--disable-background-timer-throttling");
        options.AddArgument("--disable-renderer-backgrounding");
        options.AddArgument($"--window-size={cfg.WindowWidth},{cfg.WindowHeight}");
        options.PageLoadStrategy = PageLoadStrategy.Eager;

        if (cfg.ShowWindow is false) options.AddArgument("--headless=new");
        if (!string.IsNullOrWhiteSpace(cfg.ChromeBinaryPath) && File.Exists(cfg.ChromeBinaryPath))
        {
            options.BinaryLocation = cfg.ChromeBinaryPath;
        }

        // A fixed, ordinary viewport keeps the DOM geometry stable between runs.
        options.AddUserProfilePreference("intl.accept_languages", "fa-IR,fa,en-US,en");
        options.AddUserProfilePreference("credentials_enable_service", false);
        options.AddUserProfilePreference("profile.password_manager_enabled", false);

        _driver = new ChromeDriver(options);
        _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
        _driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
        _driver.Manage().Window.Size = new System.Drawing.Size(cfg.WindowWidth, cfg.WindowHeight);

        _log.LogInformation("Chrome started with profile {Profile}", profile);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_driver is null) return Task.CompletedTask;
        try
        {
            _driver.Quit();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Chrome did not close cleanly");
        }
        finally
        {
            try { _driver.Dispose(); } catch { /* already gone */ }
            _driver = null;
            _currentThreadId = "";
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Navigation & login
    // ------------------------------------------------------------------

    public async Task NavigateInboxAsync(BrowserConfig cfg, CancellationToken ct)
    {
        var driver = Require();
        try
        {
            driver.Navigate().GoToUrl(string.IsNullOrWhiteSpace(cfg.InboxUrl)
                ? "https://www.instagram.com/direct/inbox/"
                : cfg.InboxUrl);
        }
        catch (WebDriverTimeoutException)
        {
            // The SPA keeps the document loading; whatever rendered is enough for us.
            _log.LogDebug("Inbox navigation hit the page-load timeout; continuing");
        }

        await WaitAsync(() => driver.FindElements(By.CssSelector("body")).Count > 0, 10_000, ct);
        DismissDialogs();
    }

    public bool IsLoginPage()
    {
        var driver = _driver;
        if (driver is null) return false;
        try
        {
            if (driver.Url.Contains("/accounts/login", StringComparison.OrdinalIgnoreCase)) return true;
            return driver.FindElements(By.CssSelector("input[name='username'], form#login_form")).Count > 0;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    /// <summary>Reads the @handle from the profile link in the left navigation.</summary>
    public string DetectUsername()
    {
        var driver = _driver;
        if (driver is null) return "";
        try
        {
            var result = Js("""
                const skip = new Set(['explore','reels','direct','accounts','em','p','reel','stories','about','legal','api','privacy','terms','challenge','web','developer','directory','topics','lite','session']);
                const links = Array.from(document.querySelectorAll('a[href]'));
                for (const a of links) {
                    const href = a.getAttribute('href') || '';
                    const m = href.match(/^\/([A-Za-z0-9._]{2,30})\/$/);
                    if (!m) continue;
                    const name = m[1].toLowerCase();
                    if (skip.has(name)) continue;
                    return m[1];
                }
                return '';
            """);
            return result?.ToString() ?? "";
        }
        catch (WebDriverException ex)
        {
            _log.LogDebug(ex, "Could not read the Instagram handle");
            return "";
        }
    }

    // ------------------------------------------------------------------
    // Login with saved credentials (optional)
    // ------------------------------------------------------------------

    /// <summary>
    /// Types the saved handle and password into Instagram's own form and submits it.
    /// Two-factor codes and security challenges are never solved here: they are reported so
    /// the owner can finish them in the same window.
    /// </summary>
    public async Task<(bool Ok, string Message)> LoginAsync(
        BrowserConfig cfg, string username, string password, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return (false, "مرورگر باز نیست.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "یوزرنیم یا پسورد ذخیره نشده است.");
        if (!IsLoginPage()) return (true, "از قبل وارد شده‌ای.");

        DismissDialogs();

        // Instagram has shipped both names over time: username/password and email/pass.
        var userBox = await WaitForAnyAsync(new[]
        {
            By.CssSelector("input[name='username']"),
            By.CssSelector("input[name='email']"),
            By.CssSelector("input[aria-label*='username' i]"),
            By.CssSelector("input[aria-label*='Mobile' i]")
        }, 15_000, ct);

        if (userBox is null)
        {
            // The landing page sometimes hides the form behind a "Log in" link.
            var entry = await WaitForAsync(
                By.CssSelector("a[href='/accounts/login/'], a[href*='/accounts/login']"), 3_000, ct);
            if (entry is not null)
            {
                Click(entry);
                userBox = await WaitForAnyAsync(new[]
                {
                    By.CssSelector("input[name='username']"),
                    By.CssSelector("input[name='email']")
                }, 10_000, ct);
            }
        }
        if (userBox is null) return (false, "فرم ورود اینستاگرام پیدا نشد.");

        var passBox = await WaitForAnyAsync(new[]
        {
            By.CssSelector("input[name='password']"),
            By.CssSelector("input[name='pass']"),
            By.CssSelector("input[type='password']")
        }, 8_000, ct);
        if (passBox is null) return (false, "کادر رمز پیدا نشد.");

        await FillSlowlyAsync(cfg, userBox, username, ct);
        await Task.Delay(HumanPacing.Between(200, 600), ct);
        await FillSlowlyAsync(cfg, passBox, password, ct);
        await Task.Delay(HumanPacing.Between(400, 900), ct);

        // A person presses Enter first; the button is the fallback.
        passBox.SendKeys(Keys.Enter);
        await Task.Delay(1500, ct);

        if (IsLoginPage())
        {
            var submit = FindLoginButton();
            if (submit is not null) Click(submit);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(700, ct);

            var url = SafeUrl();
            if (url.Contains("/challenge", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("two_factor", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("two-factor", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "اینستاگرام کد تأیید می‌خواهد؛ کد را در همین صفحهٔ برنامه وارد کن (یا در پنجرهٔ کروم خودت کاملش کن).");
            }

            var problem = FindLoginProblem();
            if (problem is not null) return (false, problem);

            if (!IsLoginPage()) return (true, "ورود انجام شد.");
        }

        return (false, "نتیجهٔ ورود مشخص نشد؛ متن خطای پنجرهٔ کروم را بخوان.");
    }

    /// <summary>
    /// True when the window is waiting for a one-time code: two-factor, e-mail confirmation
    /// or a security challenge. The panel shows the code box when this is true.
    /// </summary>
    public bool IsVerificationPage()
    {
        var driver = _driver;
        if (driver is null) return false;
        try
        {
            if (FindCodeBox() is not null) return true;

            var url = SafeUrl();
            return url.Contains("two_factor", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("two-factor", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/challenge/", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("challenge_required", StringComparison.OrdinalIgnoreCase);
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    /// <summary>Types the code Instagram sent to the e-mail / authenticator and submits it.</summary>
    public async Task<(bool Ok, string Message)> SubmitCodeAsync(
        BrowserConfig cfg, string code, CancellationToken ct)
    {
        if (_driver is null) return (false, "مرورگر باز نیست.");
        if (string.IsNullOrWhiteSpace(code)) return (false, "کد را وارد کن.");

        var box = FindCodeBox();
        if (box is null)
            return (false, "صفحهٔ وارد کردن کد دیده نمی‌شود؛ اول «همین حالا وارد شو» را بزن.");

        try
        {
            Click(box);
            await Task.Delay(HumanPacing.Between(150, 400), ct);
            box.SendKeys(Keys.Control + "a");
            await Task.Delay(HumanPacing.Between(90, 180), ct);
            box.SendKeys(Keys.Delete);

            foreach (var rune in code.Trim().EnumerateRunes())
            {
                box.SendKeys(rune.ToString());
                await Task.Delay(HumanPacing.KeyDelayMs(rune.ToString(), cfg.TypingMinMsPerChar, cfg.TypingMaxMsPerChar), ct);
            }

            await Task.Delay(HumanPacing.Between(500, 1100), ct);
            box.SendKeys(Keys.Enter);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(700, ct);
                if (IsLoginPage()) return (false, "کد پذیرفته نشد یا هنوز صفحهٔ ورود است؛ دوباره تلاش کن.");
                if (!IsVerificationPage()) return (true, "کد پذیرفته شد؛ ورود انجام شد.");

                var problem = FindLoginProblem();
                if (problem is not null) return (false, problem);
            }

            return (false, "نتیجهٔ کد مشخص نشد؛ وضعیت پنجرهٔ کروم را ببین.");
        }
        catch (WebDriverException ex)
        {
            return (false, "وارد کردن کد با خطای مرورگر متوقف شد: " + Shorten(ex.Message, 180));
        }
    }

    /// <summary>The input that takes the one-time code (two-factor, e-mail or challenge).</summary>
    private IWebElement? FindCodeBox()
    {
        var driver = _driver;
        if (driver is null) return null;
        try
        {
            var url = SafeUrl();
            var inCodeFlow = url.Contains("two_factor", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("two-factor", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("/challenge", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("/accounts/", StringComparison.OrdinalIgnoreCase);

            var exact = new[]
            {
                By.CssSelector("input[name='verification_code']"),
                By.CssSelector("input[name='confirmation_code']"),
                By.CssSelector("input[aria-label*='security code' i]"),
                By.CssSelector("input[aria-label*='verification code' i]"),
                By.CssSelector("input[aria-label*='کد تأیید' i]")
            };

            foreach (var by in exact)
            {
                var found = driver.FindElements(by).FirstOrDefault(el => el.Displayed && el.Enabled);
                if (found is not null) return found;
            }

            if (!inCodeFlow) return null;

            foreach (var by in new[]
            {
                By.CssSelector("input[inputmode='numeric']"),
                By.CssSelector("input[maxlength='6']"),
                By.CssSelector("input[maxlength='8']")
            })
            {
                var found = driver.FindElements(by).FirstOrDefault(el => el.Displayed && el.Enabled);
                if (found is not null) return found;
            }
        }
        catch (WebDriverException)
        {
            // The page is mid-render; the caller will try again.
        }
        return null;
    }

    private async Task FillSlowlyAsync(
        BrowserConfig cfg, IWebElement element, string text, CancellationToken ct)
    {
        Click(element);
        await Task.Delay(HumanPacing.Between(150, 400), ct);

        foreach (var rune in text.EnumerateRunes())
        {
            var piece = rune.ToString();
            if (rune.Value > 0xFFFF) InsertText(piece);
            else element.SendKeys(piece);

            await Task.Delay(HumanPacing.KeyDelayMs(piece, cfg.TypingMinMsPerChar, cfg.TypingMaxMsPerChar), ct);
        }
    }

    private async Task<IWebElement?> WaitForAnyAsync(
        IEnumerable<By> selectors, int timeoutMs, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return null;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var by in selectors)
            {
                try
                {
                    var found = driver.FindElements(by).FirstOrDefault(el => el.Displayed && el.Enabled);
                    if (found is not null) return found;
                }
                catch (WebDriverException)
                {
                    // Keep trying the next selector; the page may be mid-render.
                }
            }
            await Task.Delay(250, ct);
        }
        return null;
    }

    /// <summary>The visible "Log in" button (a div with role=button on the current markup).</summary>
    private IWebElement? FindLoginButton()
    {
        var driver = _driver;
        if (driver is null) return null;
        try
        {
            foreach (var el in driver.FindElements(By.CssSelector("button[type='submit'], button, div[role='button']")))
            {
                if (!el.Displayed || !el.Enabled) continue;
                var text = (el.Text ?? "").Trim().ToLowerInvariant();
                if (text is "log in" or "login" or "ورود" or "ورود به حساب") return el;
            }
        }
        catch (WebDriverException)
        {
            // Fall through to Enter on the password field.
        }
        return null;
    }

    private async Task<IWebElement?> WaitForAsync(By by, int timeoutMs, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return null;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var found = driver.FindElements(by).FirstOrDefault(el => el.Displayed && el.Enabled);
                if (found is not null) return found;
            }
            catch (WebDriverException)
            {
                // The page is mid-navigation; try again.
            }
            await Task.Delay(250, ct);
        }
        return null;
    }

    private string SafeUrl()
    {
        try { return _driver?.Url ?? ""; }
        catch (WebDriverException) { return ""; }
    }

    /// <summary>
    /// Instagram's own error text (wrong password, unknown user, rate limit, checkpoint).
    /// Read from the visible page text: the markup around it changes far more often than the
    /// sentences Instagram puts there.
    /// </summary>
    private string? FindLoginProblem()
    {
        var driver = _driver;
        if (driver is null) return null;
        try
        {
            var result = Js("""
                const lines = (document.body.innerText || '').split('\n').map(l => l.trim()).filter(Boolean);
                // Field labels are not errors, so the Persian patterns stay specific.
                const problem = /(password was incorrect|didn't match|couldn't find your account|doesn't belong to an account|try again|wait a few minutes|too many|suspicious|confirm it|blocked|اطلاعات ورود|نادرست|حسابتان|دوباره تلاش|تلاش‌های|مسدود|کد تأیید|چالش)/i;
                for (const line of lines) {
                    if (line.length < 8 || line.length > 200) continue;
                    if (problem.test(line)) return line;
                }
                return '';
            """)?.ToString();

            return string.IsNullOrWhiteSpace(result) ? null : Shorten(result, 200);
        }
        catch (WebDriverException)
        {
            return null;
        }
    }

    public InboxDiagnostics Diagnose(BrowserConfig cfg)
    {
        var driver = _driver;
        if (driver is null) return new InboxDiagnostics(false, false, 0, false, "", "", "مرورگر باز نیست.");

        try
        {
            var threads = CountThreads(cfg);
            var composer = CountComposer(cfg);
            string snippet = "";
            try
            {
                snippet = Shorten((driver.FindElement(By.TagName("body")).Text ?? "").Replace("\n", " · ").Trim(), 240);
            }
            catch (WebDriverException)
            {
                // Body not readable yet.
            }

            return new InboxDiagnostics(
                true,
                IsLoginPage(),
                threads,
                composer > 0,
                driver.Url,
                driver.Title,
                "",
                snippet);
        }
        catch (WebDriverException ex)
        {
            return new InboxDiagnostics(true, false, 0, false, "", "", ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Inbox reading
    // ------------------------------------------------------------------

    public List<SidebarThread> ScanSidebar(BrowserConfig cfg)
    {
        var driver = _driver;
        if (driver is null) return new List<SidebarThread>();

        try
        {
            var raw = Js("""
                const sel = arguments[0];
                const seen = new Set();
                const out = [];
                for (const a of document.querySelectorAll(sel)) {
                    const href = a.getAttribute('href') || '';
                    const m = href.match(/\/direct\/t\/([^\/?#]+)/);
                    if (!m) continue;
                    const id = m[1];
                    if (seen.has(id)) continue;
                    seen.add(id);
                    const row = a.closest('div[role="listitem"]') || a.parentElement || a;
                    const text = (row.innerText || '').trim();
                    const lines = text.split('\n').map(x => x.trim()).filter(Boolean);
                    out.push({
                        threadId: id,
                        username: lines.length ? lines[0] : '',
                        preview: lines.slice(1).join(' · '),
                        position: out.length
                    });
                }
                return out;
            """, cfg.ThreadLinkSelector) as IList<object>;

            var list = new List<SidebarThread>();
            if (raw is null) return list;

            foreach (var item in raw)
            {
                if (item is not IDictionary<string, object> row) continue;
                var id = Str(row, "threadId");
                if (id.Length == 0) continue;
                list.Add(new SidebarThread(
                    id,
                    Str(row, "username"),
                    Str(row, "preview"),
                    (int)Num(row, "position")));
            }
            return list;
        }
        catch (WebDriverException ex)
        {
            _log.LogWarning(ex, "Sidebar scan failed");
            return new List<SidebarThread>();
        }
    }

    /// <summary>Opens a conversation and returns its last message plus its direction.</summary>
    public async Task<ThreadSnapshot?> ReadThreadAsync(BrowserConfig cfg, string threadId, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return null;

        if (!IsOpenThread(driver, threadId))
        {
            try
            {
                driver.Navigate().GoToUrl($"https://www.instagram.com/direct/t/{threadId}/");
            }
            catch (WebDriverTimeoutException)
            {
                // SPA: the composer appears long before the load event would fire.
            }
            _currentThreadId = threadId;
            DismissDialogs();
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var composer = FindComposer(cfg);
            if (composer is not null)
            {
                var snapshot = ReadSnapshot(cfg);
                if (snapshot is not null) return snapshot;
            }
            await Task.Delay(400, ct);
        }

        _log.LogInformation("No composer appeared in thread {Thread}", threadId);
        return null;
    }

    private ThreadSnapshot? ReadSnapshot(BrowserConfig cfg)
    {
        var driver = _driver;
        if (driver is null) return null;

        try
        {
            var raw = Js("""
                const composerSel = arguments[0];
                const textSel = arguments[1];
                const composer = document.querySelector(composerSel);
                const composerRect = composer ? composer.getBoundingClientRect() : null;
                const limitY = composerRect ? composerRect.top : window.innerHeight;
                const paneLeft = composerRect ? composerRect.left : 0;
                const paneRight = composerRect ? composerRect.right : window.innerWidth;
                const paneCenter = (paneLeft + paneRight) / 2;

                function bubbleOf(el) {
                    let node = el;
                    for (let i = 0; i < 6 && node && node.getBoundingClientRect; i++) {
                        const style = getComputedStyle(node);
                        const radius = parseFloat(style.borderTopLeftRadius) || 0;
                        const bg = style.backgroundColor || '';
                        if (radius >= 6 && bg && bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent') return node;
                        node = node.parentElement;
                    }
                    return el;
                }

                const noise = /^(seen|delivered|sent|now|just now|active now|خوانده شد|فرستاده شد|الان|\d{1,2}:\d{2}( [ap]\.?m\.?)?|\d+\s*[smhdwyg]|\d+\s*(دقیقه|ساعت|روز|هفته|ماه|سال)( پیش)?)$/i;

                const items = [];
                for (const el of document.querySelectorAll(textSel)) {
                    if (el.isContentEditable) continue;
                    if (el.closest('[contenteditable="true"]')) continue;
                    const text = (el.innerText || '').trim();
                    if (!text || noise.test(text)) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 2 || rect.height < 2) continue;
                    if (rect.top >= limitY - 2 || rect.top < 0) continue;
                    const bubble = bubbleOf(el);
                    const br = bubble.getBoundingClientRect();
                    const center = br.width > 0 ? br.left + br.width / 2 : rect.left + rect.width / 2;
                    items.push({ text: text.slice(0, 900), top: rect.top, outgoing: center > paneCenter });
                }
                items.sort((a, b) => a.top - b.top);
                const last = items.length ? items[items.length - 1] : null;
                return {
                    count: items.length,
                    lastText: last ? last.text : '',
                    lastOutgoing: last ? last.outgoing : false,
                    samples: items.slice(-4).map(x => (x.outgoing ? '>' : '<') + x.text.slice(0, 60))
                };
            """, cfg.ComposerSelector, cfg.MessageTextSelector) as IDictionary<string, object>;

            if (raw is null) return null;
            var count = (int)Num(raw, "count");
            if (count == 0) return null;
            return new ThreadSnapshot(count, Str(raw, "lastText"), !Bool(raw, "lastOutgoing"));
        }
        catch (WebDriverException ex)
        {
            _log.LogDebug(ex, "Reading the thread failed");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------

    public async Task<(bool Ok, string? Error)> SendAsync(
        BrowserConfig cfg, string threadId, string text, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return (false, "مرورگر باز نیست.");

        if (!IsOpenThread(driver, threadId))
        {
            try
            {
                driver.Navigate().GoToUrl($"https://www.instagram.com/direct/t/{threadId}/");
            }
            catch (WebDriverTimeoutException) { /* SPA */ }
            _currentThreadId = threadId;
            DismissDialogs();
        }

        IWebElement? composer = null;
        for (var attempt = 0; attempt < 15 && composer is null; attempt++)
        {
            composer = FindComposer(cfg);
            if (composer is null) await Task.Delay(400, ct);
        }
        if (composer is null) return (false, "کادر نوشتن پیام در این گفتگو پیدا نشد.");

        try
        {
            Click(composer);
            await Task.Delay(HumanPacing.Between(250, 700), ct);

            // A leftover draft would be sent together with the reply; clear it first.
            if (!string.IsNullOrWhiteSpace(composer.Text))
            {
                composer.SendKeys(Keys.Control + "a");
                await Task.Delay(HumanPacing.Between(90, 180), ct);
                composer.SendKeys(Keys.Delete);
                await Task.Delay(HumanPacing.Between(120, 260), ct);
            }

            foreach (var rune in text.EnumerateRunes())
            {
                var piece = rune.ToString();
                if (piece == "\n")
                {
                    composer.SendKeys(Keys.Shift + Keys.Enter);
                }
                else if (rune.Value > 0xFFFF)
                {
                    // Non-BMP characters (emoji) are unreachable through SendKeys.
                    InsertText(piece);
                }
                else
                {
                    composer.SendKeys(piece);
                }

                await Task.Delay(HumanPacing.KeyDelayMs(piece, cfg.TypingMinMsPerChar, cfg.TypingMaxMsPerChar), ct);
            }

            await Task.Delay(HumanPacing.Between(600, 1600), ct);
            composer.SendKeys(Keys.Enter);

            var snippet = FirstLine(text);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(500, ct);
                var snapshot = ReadSnapshot(cfg);
                if (snapshot is not null && !snapshot.IsIncoming &&
                    snapshot.LastText.Replace(" ", "").Contains(snippet.Replace(" ", ""), StringComparison.Ordinal))
                {
                    return (true, null);
                }
            }

            return (false, "پیام ارسال شد ولی تأییدش در گفتگو دیده نشد.");
        }
        catch (WebDriverException ex)
        {
            _log.LogWarning(ex, "Sending in thread {Thread} failed", threadId);
            return (false, "ارسال با خطای مرورگر متوقف شد: " + Shorten(ex.Message, 200));
        }
    }

    private void InsertText(string piece) =>
        Js("const el = document.activeElement; if (el) document.execCommand('insertText', false, arguments[0]);", piece);

    // ------------------------------------------------------------------
    // Comment bot: read the post, check the follow, open a DM
    // ------------------------------------------------------------------

    /// <summary>Reads the comments currently visible under a post.</summary>
    public async Task<List<PostComment>> ReadCommentsAsync(
        BrowserConfig cfg, string postUrl, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return new List<PostComment>();

        var url = NormalizePostUrl(postUrl);
        if (url.Length == 0) return new List<PostComment>();

        try
        {
            if (!SafeUrl().StartsWith(url, StringComparison.OrdinalIgnoreCase))
            {
                try { driver.Navigate().GoToUrl(url); }
                catch (WebDriverTimeoutException) { /* SPA */ }
                DismissDialogs();
                await Task.Delay(HumanPacing.Between(1500, 3000), ct);
            }

            ExpandCommentList();
            await Task.Delay(HumanPacing.Between(700, 1600), ct);

            var raw = Js("""
                const sel = arguments[0];
                const skip = new Set(['explore','reels','direct','accounts','p','reel','stories','about','legal','api','privacy','terms','challenge','web','developer','directory','topics','lite','em','session','follow','liked_by']);
                const out = [];
                const seen = new Set();
                for (const li of document.querySelectorAll(sel)) {
                    const text = (li.innerText || '').trim();
                    if (!text || text.length > 1200) continue;

                    let username = '';
                    for (const a of li.querySelectorAll('a[href]')) {
                        const href = (a.getAttribute('href') || '').split('?')[0];
                        const m = href.match(/^\/([A-Za-z0-9._]{1,30})\/$/);
                        if (!m) continue;
                        const name = m[1];
                        if (skip.has(name.toLowerCase())) continue;
                        username = name;
                        break;
                    }
                    if (!username) continue;

                    const bodyEl = li.querySelector('span[dir="auto"], div[dir="auto"]');
                    const lines = text.split('\n').map(x => x.trim()).filter(Boolean);
                    const body = bodyEl && bodyEl.innerText.trim()
                        ? bodyEl.innerText.trim()
                        : lines.filter(l => l.toLowerCase() !== username.toLowerCase()).join(' ');
                    if (!body) continue;

                    const key = username.toLowerCase() + '|' + body.slice(0, 120);
                    if (seen.has(key)) continue;
                    seen.add(key);
                    out.push({ username, text: body.slice(0, 500), key });
                }
                return out;
            """, cfg.CommentSelector) as IList<object>;

            var list = new List<PostComment>();
            if (raw is null) return list;

            foreach (var item in raw)
            {
                if (item is not IDictionary<string, object> row) continue;
                var username = Str(row, "username").Trim().TrimStart('@');
                var text = Str(row, "text");
                if (username.Length == 0 || text.Length == 0) continue;
                list.Add(new PostComment(username, text, Str(row, "key")));
            }
            return list;
        }
        catch (WebDriverException ex)
        {
            _log.LogWarning(ex, "Reading comments failed");
            return new List<PostComment>();
        }
    }

    /// <summary>
    /// Does this account follow us? Instagram prints a small "Follows you" label on the
    /// profile when it does. Returns null when the profile could not be read at all.
    /// </summary>
    public async Task<bool?> CheckFollowsAsync(string username, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return null;
        if (string.IsNullOrWhiteSpace(username)) return null;

        try
        {
            try { driver.Navigate().GoToUrl($"https://www.instagram.com/{username.Trim().TrimStart('@')}/"); }
            catch (WebDriverTimeoutException) { /* SPA */ }
            DismissDialogs();

            for (var attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(700, ct);
                var verdict = Js("""
                    const wanted = /^(follows you|following you|دنبال‌شدهٔ شما|شما را دنبال می‌کند|دنبال کننده شما)$/i;
                    for (const el of document.querySelectorAll('span, div')) {
                        const own = (el.children && el.children.length === 0) ? (el.textContent || '').trim() : '';
                        if (own && own.length <= 40 && wanted.test(own)) return true;
                    }
                    const profile = document.querySelector('header') || document.querySelector('main section') || document.querySelector('main');
                    if (!profile) return null;          // still loading
                    const notFound = /sorry, this page isn't available|این صفحه در دسترس نیست/i.test(document.body.innerText || '');
                    return notFound ? null : false;     // loaded, but no label = does not follow
                """);

                if (verdict is bool value) return value;
            }
            return null;
        }
        catch (WebDriverException)
        {
            return null;
        }
    }

    /// <summary>Opens a DM with somebody (existing thread or a brand new one) and returns its id.</summary>
    public async Task<string?> OpenThreadWithAsync(BrowserConfig cfg, string username, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return null;
        var handle = username.Trim().TrimStart('@');
        if (handle.Length == 0) return null;

        try
        {
            // Path 1: their profile has a "Message" button.
            try { driver.Navigate().GoToUrl($"https://www.instagram.com/{handle}/"); }
            catch (WebDriverTimeoutException) { /* SPA */ }
            DismissDialogs();
            await Task.Delay(HumanPacing.Between(800, 1600), ct);

            var clicked = Js("""
                const wanted = /^(message|send message|پیام|ارسال پیام|پیام دادن|پیام خصوصی)$/i;
                for (const b of document.querySelectorAll('button, div[role="button"], a[role="link"], a')) {
                    const t = (b.innerText || '').trim();
                    if (!t || t.length > 24) continue;
                    if (!wanted.test(t)) continue;
                    b.click();
                    return true;
                }
                return false;
            """) is bool ok && ok;

            if (clicked)
            {
                var threadId = await WaitForThreadIdAsync(handle, 12_000, ct);
                if (threadId is not null)
                {
                    // The new-conversation screen sometimes asks for one more confirmation.
                    await ConfirmNewThreadAsync(ct);
                    return threadId ?? await WaitForThreadIdAsync(handle, 6_000, ct);
                }
            }

            // Path 2: the "new message" composer and its search box.
            try { driver.Navigate().GoToUrl("https://www.instagram.com/direct/new/"); }
            catch (WebDriverTimeoutException) { /* SPA */ }
            DismissDialogs();

            var search = await WaitForAnyAsync(new[]
            {
                By.CssSelector("input[placeholder*='Search' i]"),
                By.CssSelector("input[aria-label*='Search' i]"),
                By.CssSelector("input[aria-label*='جستجو' i]"),
                By.CssSelector("input[placeholder*='جستجو' i]")
            }, 12_000, ct);

            if (search is null) return null;

            Click(search);
            await Task.Delay(HumanPacing.Between(200, 500), ct);
            foreach (var rune in handle.EnumerateRunes())
            {
                search.SendKeys(rune.ToString());
                await Task.Delay(HumanPacing.KeyDelayMs(rune.ToString(), cfg.TypingMinMsPerChar, cfg.TypingMaxMsPerChar), ct);
            }

            await Task.Delay(HumanPacing.Between(900, 1800), ct);
            var picked = Js("""
                const wanted = arguments[0].toLowerCase();
                const nodes = document.querySelectorAll('div[role="dialog"] a, div[role="listbox"] *, main a, main div[role="button"]');
                for (const el of nodes) {
                    const href = (el.getAttribute && el.getAttribute('href') || '').toLowerCase();
                    const t = (el.innerText || '').trim().toLowerCase();
                    if (href === '/' + wanted + '/' || t.replace('@','') === wanted || t.startsWith('@' + wanted)) {
                        el.click();
                        return true;
                    }
                }
                return false;
            """, handle) is bool hit && hit;

            if (!picked) return null;

            await ConfirmNewThreadAsync(ct);
            return await WaitForThreadIdAsync(handle, 12_000, ct);
        }
        catch (WebDriverException ex)
        {
            _log.LogWarning(ex, "Opening a conversation with {User} failed", username);
            return null;
        }
    }

    private async Task ConfirmNewThreadAsync(CancellationToken ct)
    {
        await Task.Delay(HumanPacing.Between(500, 1100), ct);
        Js("""
            const wanted = /^(next|chat|create|بعدی|بعد|گفتگو|ایجاد|شروع|شروع گفتگو)$/i;
            for (const b of document.querySelectorAll('button, div[role="button"]')) {
                const t = (b.innerText || '').trim();
                if (!t || t.length > 24 || !wanted.test(t)) continue;
                b.click();
                return;
            }
        """);
    }

    private async Task<string?> WaitForThreadIdAsync(string handle, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var url = SafeUrl();
            var index = url.IndexOf("/direct/t/", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var rest = url[(index + "/direct/t/".Length)..];
                var id = rest.Split('/', '?', '#')[0].Trim();
                if (id.Length > 0) return id;
            }
            await Task.Delay(400, ct);
        }
        return null;
    }

    /// <summary>Accepts a post URL, a /p/… path or just the shortcode.</summary>
    internal static string NormalizePostUrl(string input)
    {
        var value = (input ?? "").Trim().TrimEnd('/');
        if (value.Length == 0) return "";
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value;

        value = value.TrimStart('/');
        if (value.StartsWith("p/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("reel/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("tv/", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://www.instagram.com/{value}/";
        }
        return $"https://www.instagram.com/p/{value}/";
    }

    /// <summary>Opens the full comment list when the post only shows a few.</summary>
    private void ExpandCommentList()
    {
        var driver = _driver;
        if (driver is null) return;
        try
        {
            Js("""
                const wanted = /(view all|view more comments|مشاهده همه|نمایش همه|مشاهدهٔ همه|بیشتر)/i;
                for (const el of document.querySelectorAll('button, a, div[role="button"]')) {
                    const t = (el.innerText || '').trim();
                    if (!t || t.length > 60 || /^\d+$/.test(t)) continue;
                    if (!wanted.test(t)) continue;
                    el.click();
                    return true;
                }
                return false;
            """);
        }
        catch (WebDriverException)
        {
            // Nothing to expand.
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private IWebElement? FindComposer(BrowserConfig cfg)
    {
        var driver = _driver;
        if (driver is null) return null;
        try
        {
            return driver.FindElements(By.CssSelector(cfg.ComposerSelector))
                .FirstOrDefault(el => el.Displayed && el.Enabled);
        }
        catch (WebDriverException)
        {
            return null;
        }
    }

    private int CountThreads(BrowserConfig cfg)
    {
        var driver = _driver;
        if (driver is null) return 0;
        try
        {
            var value = Js("return document.querySelectorAll(arguments[0]).length;", cfg.ThreadLinkSelector);
            return value is long n ? (int)n : 0;
        }
        catch (WebDriverException)
        {
            return 0;
        }
    }

    private int CountComposer(BrowserConfig cfg)
    {
        var driver = _driver;
        if (driver is null) return 0;
        try
        {
            var value = Js("return document.querySelectorAll(arguments[0]).length;", cfg.ComposerSelector);
            return value is long n ? (int)n : 0;
        }
        catch (WebDriverException)
        {
            return 0;
        }
    }

    private void Click(IWebElement element)
    {
        var driver = Require();
        try
        {
            (driver as IJavaScriptExecutor)?.ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", element);
            new Actions(driver).MoveToElement(element).Click().Perform();
        }
        catch (WebDriverException)
        {
            element.Click();
        }
    }

    /// <summary>Clicks away Instagram's own "not now" prompts so they cannot block the composer.</summary>
    private void DismissDialogs()
    {
        var driver = _driver;
        if (driver is null) return;
        try
        {
            var clicked = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const phrases = ['not now','not Now','اکنون نه','الان نه','بعداً','بعدا','شاید بعداً'];
                const wanted = phrases.map(p => p.toLowerCase());
                let clicked = 0;
                for (const b of document.querySelectorAll('div[role="button"], button')) {
                    const text = (b.innerText || '').trim().toLowerCase();
                    if (!text || text.length > 40) continue;
                    if (!wanted.some(p => text === p)) continue;
                    try { b.click(); clicked++; } catch (e) { }
                }
                const close = document.querySelector('div[role="dialog"] [aria-label="Close"]');
                if (close) { try { close.click(); clicked++; } catch (e) { } }
                return clicked;
            """);
            if (clicked is long n && n > 0) _log.LogDebug("Dismissed {Count} Instagram prompts", n);
        }
        catch (WebDriverException)
        {
            // Nothing to dismiss.
        }
    }

    private async Task WaitAsync(Func<bool> condition, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// The URL wins over the cached id: the owner may have clicked around in the window and
    /// we must not read or answer in the wrong conversation.
    /// </summary>
    private static bool IsOpenThread(IWebDriver driver, string threadId)
    {
        try
        {
            return driver.Url.Contains($"/direct/t/{threadId}", StringComparison.Ordinal);
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private IWebDriver Require() =>
        _driver ?? throw new InvalidOperationException("مرورگر باز نیست.");

    private object? Js(string script, params object[] args) =>
        ((IJavaScriptExecutor)Require()).ExecuteScript(script, args);

    private static string Str(IDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static long Num(IDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return 0;
        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : 0
        };
    }

    private static bool Bool(IDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) && value is bool b && b;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 60 ? line[..60] : line;
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
