using InstaMind.Infrastructure;
using InstaMind.Models;

namespace InstaMind.Services;

/// <summary>Owns data/conversations.json: inbox state and transcripts.</summary>
public sealed class ConversationService
{
    private const int MaxMessagesPerConversation = 400;
    private const int MaxConversations = 800;

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConversationStore _store;

    public ConversationService(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "data", "conversations.json");
        _store = AtomicJson.Read<ConversationStore>(_path) ?? new ConversationStore();
        _store.Items ??= new List<Conversation>();
    }

    public async Task<List<Conversation>> ListAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _store.Items
                .OrderByDescending(c => c.LastMessageAt)
                .Select(Snapshot)
                .ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<Conversation?> GetAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var conv = _store.Items.FirstOrDefault(c => c.Id == id);
            return conv is null ? null : Snapshot(conv);
        }
        finally { _lock.Release(); }
    }

    public async Task<Conversation> AppendIncomingAsync(string senderId, string username, string text, string? note = null, string? pageId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var conv = GetOrCreate(senderId);
            if (!string.IsNullOrWhiteSpace(username)) conv.Username = username.TrimStart('@');
            if (!string.IsNullOrWhiteSpace(pageId)) conv.PageId = pageId;
            conv.Messages.Add(new ChatMessage
            {
                Direction = "in",
                Author = "user",
                Text = text,
                At = DateTimeOffset.UtcNow,
                Note = note
            });
            Trim(conv);
            conv.LastMessageAt = DateTimeOffset.UtcNow;
            Persist();
            return Snapshot(conv);
        }
        finally { _lock.Release(); }
    }

    public async Task AppendOutgoingAsync(string conversationId, string text, string author, string? status = null, string? note = null)
    {
        await _lock.WaitAsync();
        try
        {
            var conv = GetOrCreate(conversationId);
            conv.Messages.Add(new ChatMessage
            {
                Direction = "out",
                Author = author,
                Text = text,
                At = DateTimeOffset.UtcNow,
                Status = status,
                Note = note
            });
            Trim(conv);
            conv.LastMessageAt = DateTimeOffset.UtcNow;
            Persist();
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> SetPausedAsync(string id, bool paused)
    {
        await _lock.WaitAsync();
        try
        {
            var conv = _store.Items.FirstOrDefault(c => c.Id == id);
            if (conv is null) return false;
            conv.AiPaused = paused;
            Persist();
            return true;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var removed = _store.Items.RemoveAll(c => c.Id == id);
            if (removed > 0) Persist();
            return removed > 0;
        }
        finally { _lock.Release(); }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _store.Items.Clear();
            Persist();
        }
        finally { _lock.Release(); }
    }

    public async Task<InboxStats> StatsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var today = DateTimeOffset.UtcNow.Date;
            var all = _store.Items.SelectMany(c => c.Messages).ToList();
            return new InboxStats
            {
                Conversations = _store.Items.Count,
                TotalMessages = all.Count,
                MessagesToday = all.Count(m => m.At.UtcDateTime >= today),
                AiRepliesToday = all.Count(m => m.Author == "ai" && m.At.UtcDateTime >= today),
                BlockedToday = all.Count(m => m.Status == "blocked" && m.At.UtcDateTime >= today),
                ActiveLast24h = _store.Items.Count(c => c.LastMessageAt > DateTimeOffset.UtcNow.AddHours(-24)),
                LastActivity = _store.Items.Count == 0 ? null : _store.Items.Max(c => c.LastMessageAt)
            };
        }
        finally { _lock.Release(); }
    }

    public async Task<int> AiRepliesInLastHourAsync(string conversationId)
    {
        await _lock.WaitAsync();
        try
        {
            var conv = _store.Items.FirstOrDefault(c => c.Id == conversationId);
            if (conv is null) return 0;
            var since = DateTimeOffset.UtcNow.AddHours(-1);
            return conv.Messages.Count(m => m.Author == "ai" && m.At > since);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Cached conversation for prompt history, without cloning the whole transcript.</summary>
    public async Task<List<ChatMessage>> RecentMessagesAsync(string conversationId, int count)
    {
        await _lock.WaitAsync();
        try
        {
            var conv = _store.Items.FirstOrDefault(c => c.Id == conversationId);
            if (conv is null) return new List<ChatMessage>();
            return conv.Messages.TakeLast(count).ToList();
        }
        finally { _lock.Release(); }
    }

    private Conversation GetOrCreate(string id)
    {
        var conv = _store.Items.FirstOrDefault(c => c.Id == id);
        if (conv is not null) return conv;

        conv = new Conversation { Id = id, CreatedAt = DateTimeOffset.UtcNow, LastMessageAt = DateTimeOffset.UtcNow };
        _store.Items.Add(conv);

        if (_store.Items.Count > MaxConversations)
        {
            var drop = _store.Items.OrderBy(c => c.LastMessageAt).First();
            _store.Items.Remove(drop);
        }
        return conv;
    }

    private static void Trim(Conversation conv)
    {
        if (conv.Messages.Count > MaxMessagesPerConversation)
        {
            conv.Messages.RemoveRange(0, conv.Messages.Count - MaxMessagesPerConversation);
        }
    }

    private void Persist() => AtomicJson.Write(_path, _store);

    private static Conversation Snapshot(Conversation c)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(c, JsonConfig.Options);
        return System.Text.Json.JsonSerializer.Deserialize<Conversation>(json, JsonConfig.Options)!;
    }
}

public sealed class InboxStats
{
    public int Conversations { get; set; }
    public int TotalMessages { get; set; }
    public int MessagesToday { get; set; }
    public int AiRepliesToday { get; set; }
    public int BlockedToday { get; set; }
    public int ActiveLast24h { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
}
