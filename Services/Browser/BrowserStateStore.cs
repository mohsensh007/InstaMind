using System.Security.Cryptography;
using System.Text;
using InstaMind.Infrastructure;

namespace InstaMind.Services.Browser;

/// <summary>Persisted bookkeeping for browser mode (data/browser-state.json).</summary>
public sealed class BrowserState
{
    /// <summary>True once the inbox has been read once; the very first pass only records what exists.</summary>
    public bool Primed { get; set; }

    /// <summary>thread id -> last sidebar preview we saw (used to skip untouched conversations).</summary>
    public Dictionary<string, string> Previews { get; set; } = new();

    /// <summary>thread id -> fingerprint of the last message we already reacted to.</summary>
    public Dictionary<string, string> Handled { get; set; } = new();

    /// <summary>thread id -> when we last sent something there.</summary>
    public Dictionary<string, DateTimeOffset> RepliedAt { get; set; } = new();

    public DateTimeOffset? LastSendAt { get; set; }

    public string SendsDate { get; set; } = "";
    public int SendsToday { get; set; }

    // ---- comment bot (per account) ----

    /// <summary>comment key -&gt; fingerprint of a comment we already acted on.</summary>
    public Dictionary<string, string> SeenComments { get; set; } = new();

    /// <summary>Tehran day the comment counter below belongs to.</summary>
    public string CommentDate { get; set; } = "";
    public int CommentsToday { get; set; }
}

/// <summary>
/// Small lock-guarded store around <see cref="BrowserState"/>. It remembers which
/// conversations already got an answer, so restarting the app does not produce a second one,
/// and enforces the per-conversation cool-down.
/// </summary>
public sealed class BrowserStateStore
{
    private const int MaxEntries = 800;

    private readonly string _path;
    private readonly object _sync = new();
    private BrowserState _state;

    /// <param name="pageKey">
    /// Each connected page keeps its own state file, so one account's rhythm and
    /// bookkeeping never bleed into another one's.
    /// </param>
    public BrowserStateStore(IWebHostEnvironment env, string? pageKey = null)
    {
        var name = string.IsNullOrWhiteSpace(pageKey) || pageKey == "default"
            ? "browser-state.json"
            : $"browser-state-{Sanitize(pageKey)}.json";
        _path = Path.Combine(env.ContentRootPath, "data", name);
        _state = AtomicJson.Read<BrowserState>(_path) ?? new BrowserState();
        _state.Previews ??= new Dictionary<string, string>();
        _state.Handled ??= new Dictionary<string, string>();
        _state.RepliedAt ??= new Dictionary<string, DateTimeOffset>();
        _state.SeenComments ??= new Dictionary<string, string>();
    }

    public bool Primed
    {
        get { lock (_sync) return _state.Primed; }
    }

    public void Prime()
    {
        lock (_sync)
        {
            _state.Primed = true;
            Persist();
        }
    }

    public string? Preview(string threadId)
    {
        lock (_sync) return _state.Previews.TryGetValue(threadId, out var value) ? value : null;
    }

    public void RememberPreview(string threadId, string preview)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        lock (_sync)
        {
            _state.Previews[threadId] = preview ?? "";
            Trim(_state.Previews);
        }
    }

    public void SavePreviews()
    {
        lock (_sync) Persist();
    }

    // ------------------------------------------------------------------
    // Comment bot
    // ------------------------------------------------------------------

    public bool WasCommentSeen(string key, string fingerprint)
    {
        lock (_sync)
            return _state.SeenComments.TryGetValue(key, out var stored) && stored == fingerprint;
    }

    public void MarkCommentSeen(string key, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_sync)
        {
            _state.SeenComments[key] = fingerprint;
            Trim(_state.SeenComments);
            Persist();
        }
    }

    /// <summary>Comment DMs already sent today (Tehran day), rolling over on its own.</summary>
    public int CommentsSentToday()
    {
        lock (_sync)
        {
            var today = HumanPacing.TehranDay(DateTimeOffset.UtcNow);
            if (_state.CommentDate != today)
            {
                _state.CommentDate = today;
                _state.CommentsToday = 0;
                Persist();
            }
            return _state.CommentsToday;
        }
    }

    public void RecordCommentSend()
    {
        lock (_sync)
        {
            var today = HumanPacing.TehranDay(DateTimeOffset.UtcNow);
            if (_state.CommentDate != today)
            {
                _state.CommentDate = today;
                _state.CommentsToday = 0;
            }
            _state.CommentsToday++;
            Persist();
        }
    }

    /// <summary>Puts a cool-down key on an account without touching the send counters.</summary>
    public void RememberAt(string key, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_sync)
        {
            _state.RepliedAt[key] = at;
            Trim(_state.RepliedAt);
            Persist();
        }
    }

    public bool WasHandled(string threadId, string fingerprint)
    {
        lock (_sync)
        {
            return _state.Handled.TryGetValue(threadId, out var stored) && stored == fingerprint;
        }
    }

    public void MarkHandled(string threadId, string fingerprint)
    {
        lock (_sync)
        {
            _state.Handled[threadId] = fingerprint;
            Trim(_state.Handled);
            Persist();
        }
    }

    /// <summary>Time left before this conversation may be answered again.</summary>
    public TimeSpan CooldownLeft(string threadId, int cooldownMinutes)
    {
        if (cooldownMinutes <= 0) return TimeSpan.Zero;
        lock (_sync)
        {
            if (!_state.RepliedAt.TryGetValue(threadId, out var at)) return TimeSpan.Zero;
            var left = at.AddMinutes(cooldownMinutes) - DateTimeOffset.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Time left before the next message anywhere may be sent.</summary>
    public TimeSpan GlobalGapLeft(int gapSeconds)
    {
        if (gapSeconds <= 0) return TimeSpan.Zero;
        lock (_sync)
        {
            if (_state.LastSendAt is null) return TimeSpan.Zero;
            var left = _state.LastSendAt.Value.AddSeconds(gapSeconds) - DateTimeOffset.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public void RecordSend(string threadId)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            _state.LastSendAt = now;
            _state.RepliedAt[threadId] = now;
            Trim(_state.RepliedAt);

            var today = HumanPacing.TehranDay(now);
            if (_state.SendsDate != today)
            {
                _state.SendsDate = today;
                _state.SendsToday = 0;
            }
            _state.SendsToday++;
            Persist();
        }
    }

    public (string Day, int Count) SendsToday
    {
        get
        {
            lock (_sync) return (_state.SendsDate, _state.SendsToday);
        }
    }

    /// <summary>Fingerprint of the message we are about to react to.</summary>
    public static string Fingerprint(int messageCount, string text) =>
        $"{messageCount}:{ShortHash(text)}";

    private static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes, 0, 6);
    }

    private static void Trim<TValue>(Dictionary<string, TValue> map)
    {
        if (map.Count <= MaxEntries) return;
        foreach (var key in map.Keys.Take(map.Count - MaxEntries).ToList())
        {
            map.Remove(key);
        }
    }

    private static string Sanitize(string value)
    {
        var clean = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return clean.Length == 0 ? "default" : clean;
    }

    private void Persist() => AtomicJson.Write(_path, _state);
}
