using System.Security.Cryptography;
using System.Text;
using InstaMind.Models;
using OpenQA.Selenium;

namespace InstaMind.Services.Browser;

/// <summary>
/// The background loop of browser mode.
///
/// Every connected page gets its own turn and its own window: the loop walks the pages one
/// after another, opens only the conversations that changed in that page's inbox, and lets
/// <see cref="ReplyEngine"/> decide what (if anything) to answer with that page's own AI.
/// Page 1 never reads, types in or even looks at page 2's window — the two accounts share
/// nothing but the machine they run on.
///
/// The comment bot runs on the same window: it watches the post the account configured,
/// checks whether the commenter follows it and sends a DM at the same human pace.
///
/// The first pass after a restart only records what already exists, so an old backlog is not
/// suddenly answered — from then on it reacts to messages that arrive while it is running.
/// </summary>
public sealed class BrowserInboxWorker : BackgroundService
{
    private readonly ConfigService _config;
    private readonly BrowserHub _hub;
    private readonly ReplyEngine _engine;
    private readonly ILogger<BrowserInboxWorker> _log;

    private readonly SemaphoreSlim _cycle = new(1, 1);

    public BrowserInboxWorker(
        ConfigService config,
        BrowserHub hub,
        ReplyEngine engine,
        ILogger<BrowserInboxWorker> log)
    {
        _config = config;
        _hub = hub;
        _engine = engine;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(6), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Browser cycle failed");
                delay = TimeSpan.FromSeconds(Math.Max(30, _config.Current.Browser.BackoffSeconds));
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One turn of the loop: gives every page that is due its own pass.</summary>
    private async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        var runtimes = _hub.Active();
        if (runtimes.Count == 0) return TimeSpan.FromSeconds(30);

        // The mode and the worker switch are global; everything below is per page.
        if (_config.Current.Instagram.Mode != MessageRouter.ModeBrowser)
        {
            foreach (var runtime in runtimes)
            {
                if (!runtime.IsRunning) runtime.SetState("stopped", "حالت اتصال روی «مرورگر» نیست.");
            }
            return TimeSpan.FromSeconds(30);
        }

        if (!cfg.Enabled)
        {
            foreach (var runtime in runtimes)
            {
                if (!runtime.IsRunning) runtime.SetState("disabled", "کارگر مرورگر خاموش است.");
            }
            return TimeSpan.FromSeconds(30);
        }

        foreach (var runtime in runtimes)
        {
            ct.ThrowIfCancellationRequested();
            if (runtime.Page is { BrowserEnabled: false }) continue;
            if (!runtime.IsDue()) continue;

            await RunPageAsync(runtime, ct);

            // The windows take turns; a short pause keeps the machine calm too.
            await Task.Delay(HumanPacing.Between(1200, 3500), ct);
        }

        await _hub.PruneAsync();
        return TimeSpan.FromSeconds(5);
    }

    /// <summary>One page's full pass: gate, comment bot, then the inbox.</summary>
    private async Task RunPageAsync(BrowserRuntime runtime, CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        var page = runtime.Page;
        if (page is null)
        {
            runtime.ScheduleNext(TimeSpan.FromMinutes(1));
            return;
        }

        if (!HumanPacing.WithinWorkingHours(cfg, DateTimeOffset.UtcNow))
        {
            runtime.SetState("outside_hours",
                $"خارج از ساعت کاری ({cfg.WorkingHoursStart}–{cfg.WorkingHoursEnd}); تا شروع دوباره دست به اینستاگرام نمی‌زند.");
            runtime.ScheduleNext(TimeSpan.FromMinutes(5));
            return;
        }

        if (runtime.DailyCapReached())
        {
            runtime.SetState("daily_cap", $"سقف {cfg.DailySendCap} پیام امروز پر شد؛ فردا ادامه می‌دهد.");
            runtime.ScheduleNext(TimeSpan.FromMinutes(10));
            return;
        }

        var ready = await runtime.EnsureReadyAsync(ct);
        if (!ready.Ready)
        {
            var wait = ready.State is "waiting_login" or "waiting_code" ? 10 : 25;
            runtime.ScheduleNext(TimeSpan.FromSeconds(wait));
            return;
        }

        try
        {
            if (page.CommentBot is { Enabled: true } && runtime.CommentsDue())
            {
                await RunCommentPassAsync(runtime, page, ct);
            }

            await ScanAsync(runtime, ct, manual: false);
        }
        catch (WebDriverException ex)
        {
            _log.LogWarning(ex, "Browser pass for {User} failed", page.Username);
            var message = "خطای مرورگر؛ کمی بعد دوباره تلاش می‌کند.";
            runtime.SetState("error", message);
            runtime.ScheduleNext(TimeSpan.FromSeconds(Math.Max(30, cfg.BackoffSeconds)));
        }
    }

    /// <summary>One manual pass from the panel (one page, or all of them).</summary>
    public async Task<object> ScanOnceAsync(bool manual, string? pageId, CancellationToken ct)
    {
        if (_config.Current.Instagram.Mode != MessageRouter.ModeBrowser)
            return new
            {
                ok = false,
                message = "برای اسکن خودکار، حالت اتصال را روی «مرورگر» بگذار و ذخیره کن. برای یک تست سریع، «تست مرورگر» کافی است."
            };

        var targets = _hub.Resolve(pageId);
        if (targets.Count == 0)
            return new { ok = false, message = "حسابی با این شناسه پیدا نشد؛ اول یک حساب اضافه کن." };

        var threads = 0;
        var newMessages = 0;
        var sent = 0;
        var notes = new List<string>();

        foreach (var runtime in targets)
        {
            ct.ThrowIfCancellationRequested();

            var ready = await runtime.EnsureReadyAsync(ct);
            if (!ready.Ready)
            {
                var name = NameOf(runtime);
                notes.Add($"{name}: {ready.Message}");
                continue;
            }

            var summary = await ScanAsync(runtime, ct, manual);
            threads += summary.Threads;
            newMessages += summary.NewMessages;
            sent += summary.Sent;
            notes.Add($"{NameOf(runtime)}: {summary.Message}");
        }

        return new
        {
            ok = true,
            message = string.Join(" | ", notes),
            threads,
            newMessages,
            sent
        };
    }

    private static string NameOf(BrowserRuntime runtime) =>
        string.IsNullOrWhiteSpace(runtime.PageUsername)
            ? (string.IsNullOrWhiteSpace(runtime.PageId) ? "حساب" : "حساب کوتاه")
            : "@" + runtime.PageUsername;

    private async Task<ScanSummary> ScanAsync(BrowserRuntime runtime, CancellationToken ct, bool manual)
    {
        var cfg = _config.Current.Browser;
        var state = runtime.Store;
        var page = runtime.Page;

        await _cycle.WaitAsync(ct);
        try
        {
            var next = TimeSpan.FromSeconds(cfg.ScanIntervalSeconds + HumanPacing.Between(0, cfg.ScanJitterSeconds));
            var threads = await runtime.ScanSidebarAsync(ct);
            if (threads.Count == 0)
            {
                runtime.NoteScan(0, next);
                return new ScanSummary(next, "لیست دایرکت خالی یا هنوز بارگذاری نشده است.", 0, 0, 0);
            }

            if (!manual && !state.Primed)
            {
                foreach (var thread in threads) state.RememberPreview(thread.ThreadId, thread.Preview);
                state.SavePreviews();
                state.Prime();
                runtime.NoteScan(threads.Count, next);
                runtime.SetState("ready",
                    $"اولین پاس: {threads.Count} گفتگو ثبت شد؛ از الان فقط به پیام‌های تازه جواب می‌دهد.");
                return new ScanSummary(next, $"اولین پاس: {threads.Count} گفتگو ثبت شد.", threads.Count, 0, 0);
            }

            var queue = threads.OrderBy(t => t.Position).AsEnumerable();
            if (!manual)
            {
                queue = queue.Where(t =>
                    !string.Equals(state.Preview(t.ThreadId) ?? "", t.Preview, StringComparison.Ordinal));
            }
            var candidates = queue.Take(cfg.MaxThreadsPerScan).ToList();

            var newMessages = 0;
            var sent = 0;

            foreach (var thread in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var snapshot = await runtime.ReadThreadAsync(thread.ThreadId, ct);
                if (snapshot is null)
                {
                    _log.LogInformation("Thread {Thread} could not be read", thread.ThreadId);
                    continue;
                }

                var fingerprint = BrowserStateStore.Fingerprint(snapshot.Count, snapshot.LastText);
                if (state.WasHandled(thread.ThreadId, fingerprint))
                {
                    state.RememberPreview(thread.ThreadId, thread.Preview);
                    continue;
                }

                if (!snapshot.IsIncoming)
                {
                    // Our own message is the last one: nothing to answer.
                    state.MarkHandled(thread.ThreadId, fingerprint);
                    state.RememberPreview(thread.ThreadId, thread.Preview);
                    continue;
                }

                if (!runtime.CanReplyNow(thread.ThreadId, out var reason))
                {
                    _log.LogInformation("Reply to {Thread} postponed: {Reason}", thread.ThreadId, reason);
                    continue; // preview is not remembered, so the next pass retries
                }

                newMessages++;
                var result = await _engine.HandleAsync(
                    new IncomingMessage
                    {
                        SenderId = thread.ThreadId,
                        Username = thread.Username,
                        Text = snapshot.LastText,
                        // This page's own window, this page's own AI — never another account's.
                        PageId = page?.Id ?? ""
                    },
                    deliver: true,
                    persist: true,
                    ct: ct);

                if (result.Sent || result.Blocked)
                {
                    state.MarkHandled(thread.ThreadId, fingerprint);
                    state.RememberPreview(thread.ThreadId, thread.Preview);
                }
                if (result.Sent) sent++;

                _log.LogInformation(
                    "Browser DM from {Thread}: sent={Sent} blocked={Blocked} reason={Reason}",
                    thread.ThreadId, result.Sent, result.Blocked, result.Reason);

                if (candidates.Count > 1)
                {
                    await Task.Delay(HumanPacing.Between(1500, 4500), ct);
                }
            }

            state.SavePreviews();
            runtime.NoteScan(threads.Count, next);

            var message = newMessages == 0
                ? "پیام جدیدی برای پاسخ نبود."
                : $"{newMessages} پیام تازه بررسی شد؛ {sent} پاسخ ارسال شد.";
            runtime.SetState("ready", message);

            return new ScanSummary(next, message, threads.Count, newMessages, sent);
        }
        finally
        {
            _cycle.Release();
        }
    }

    // ------------------------------------------------------------------
    // Comment -> DM bot
    // ------------------------------------------------------------------

    /// <summary>
    /// Watches this page's post and answers the comments that deserve a DM: the keyword
    /// picks them, the follow check decides who gets the real message, and everything is
    /// paced by the same rules the inbox replies use.
    /// </summary>
    private async Task RunCommentPassAsync(BrowserRuntime runtime, ConnectedPage page, CancellationToken ct)
    {
        var cfg = _config.Current.Browser;
        var bot = page.CommentBot;
        var store = runtime.Store;

        // Reschedule first: a failing pass must never become a tight loop.
        runtime.ScheduleComments(TimeSpan.FromMinutes(bot.CheckIntervalMinutes) +
                                 TimeSpan.FromSeconds(HumanPacing.Between(0, 90)));

        if (string.IsNullOrWhiteSpace(bot.PostUrl)) return;
        if (!HumanPacing.WithinWorkingHours(cfg, DateTimeOffset.UtcNow)) return;
        if (runtime.DailyCapReached()) return;

        var commentsToday = store.CommentsSentToday();
        if (bot.MaxPerDay > 0 && commentsToday >= bot.MaxPerDay)
        {
            _log.LogDebug("Comment bot of {User} is done for today ({Count})", page.Username, commentsToday);
            return;
        }

        var comments = await runtime.ReadCommentsAsync(bot.PostUrl, ct);
        if (comments.Count == 0) return;

        var attempted = 0;

        foreach (var comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            if (attempted >= bot.MaxPerPass) break;

            var fingerprint = Hash(comment.Text);
            var key = $"comment:{comment.Username.ToLowerInvariant()}:{fingerprint}";
            if (store.WasCommentSeen(key, fingerprint)) continue;

            if (!string.IsNullOrWhiteSpace(bot.Keyword) &&
                !comment.Text.Contains(bot.Keyword, StringComparison.OrdinalIgnoreCase))
            {
                store.MarkCommentSeen(key, fingerprint);
                continue;
            }

            // Never answer ourselves.
            if (!string.IsNullOrWhiteSpace(page.Username) &&
                string.Equals(comment.Username, page.Username, StringComparison.OrdinalIgnoreCase))
            {
                store.MarkCommentSeen(key, fingerprint);
                continue;
            }

            // Same pacing as a normal reply: hours, daily cap, global gap, per-person pause.
            var cooldownKey = $"comment:{comment.Username.ToLowerInvariant()}";
            if (!runtime.CanReplyNow(cooldownKey, out var reason))
            {
                _log.LogInformation("Comment from {User} postponed: {Reason}", comment.Username, reason);
                continue; // still unseen: a later pass retries
            }

            string text = "";

            if (bot.RequireFollow)
            {
                var follows = await runtime.CheckFollowsAsync(comment.Username, ct);
                if (follows is null)
                {
                    // Nothing could be read: the quiet answer is no answer.
                    store.MarkCommentSeen(key, fingerprint);
                    _log.LogInformation("Could not tell whether {User} follows this account", comment.Username);
                    continue;
                }

                if (follows == false)
                {
                    if (string.IsNullOrWhiteSpace(bot.FollowPrompt))
                    {
                        store.MarkCommentSeen(key, fingerprint);
                        continue;
                    }
                    text = bot.FollowPrompt.Trim();
                }
            }

            if (text.Length == 0)
            {
                text = (bot.Message ?? "").Trim();

                if (text.Length == 0)
                {
                    // No fixed text: this page's own AI drafts an answer to the comment,
                    // through the same guardrails a normal DM would go through.
                    var draft = await _engine.HandleAsync(
                        new IncomingMessage
                        {
                            SenderId = $"comment:{comment.Username}",
                            Username = comment.Username,
                            Text = comment.Text,
                            PageId = page.Id
                        },
                        deliver: false,
                        persist: false,
                        ct: ct);

                    text = (draft.Reply ?? "").Trim();
                    var failed = draft.Trace.Any(t => t.Status == "خطا");
                    if (text.Length == 0 || draft.Blocked || failed)
                    {
                        store.MarkCommentSeen(key, fingerprint);
                        _log.LogInformation(
                            "Comment from {User} got no DM (blocked={Blocked})",
                            comment.Username, draft.Blocked);
                        continue;
                    }
                }
            }

            attempted++;

            var threadId = await runtime.OpenThreadAsync(comment.Username, ct);
            if (threadId is null)
            {
                store.MarkCommentSeen(key, fingerprint);
                store.RememberAt(cooldownKey, DateTimeOffset.UtcNow);
                _log.LogInformation("Could not open a conversation with {User}", comment.Username);
                continue;
            }

            var send = await runtime.SendReplyAsync(threadId, text, ct);
            if (send.Ok)
            {
                store.MarkCommentSeen(key, fingerprint);
                store.RecordCommentSend();
                store.RememberAt(cooldownKey, DateTimeOffset.UtcNow);
                _log.LogInformation("Comment DM sent to {User}", comment.Username);
            }
            else
            {
                // Not seen, but paused: a broken composer must not be hammered.
                store.RememberAt(cooldownKey, DateTimeOffset.UtcNow);
                _log.LogInformation("Comment DM to {User} failed: {Error}", comment.Username, send.Error);
            }

            if (attempted < bot.MaxPerPass)
            {
                await Task.Delay(HumanPacing.Between(2000, 6000), ct);
            }
        }
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private sealed record ScanSummary(TimeSpan Next, string Message, int Threads, int NewMessages, int Sent);
}
