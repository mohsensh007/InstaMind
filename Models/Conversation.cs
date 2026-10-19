namespace InstaMind.Models;

public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>"in" or "out".</summary>
    public string Direction { get; set; } = "in";

    /// <summary>"user", "ai" or "human".</summary>
    public string Author { get; set; } = "user";

    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>"sent", "failed", "blocked", "skipped" or null.</summary>
    public string? Status { get; set; }

    /// <summary>Guardrail / provider note, shown as a badge in the UI.</summary>
    public string? Note { get; set; }
}

public sealed class Conversation
{
    /// <summary>Instagram-scoped sender id (IGSID) or sandbox id.</summary>
    public string Id { get; set; } = "";

    public string Username { get; set; } = "";

    /// <summary>IgUserId of the connected page this conversation belongs to (null = unknown/sandbox).</summary>
    public string? PageId { get; set; }

    public bool AiPaused { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();

    public ChatMessage? LastMessage => Messages.Count == 0 ? null : Messages[^1];
}

public sealed class ConversationStore
{
    public List<Conversation> Items { get; set; } = new();
}

public sealed class IncomingMessage
{
    public string SenderId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Text { get; set; } = "";
    public string? MessageId { get; set; }

    /// <summary>IgUserId of the page the webhook delivered this message for.</summary>
    public string? PageId { get; set; }

    /// <summary>Optional AI provider hint (lab tests): answer with this provider instead of the page's one.</summary>
    public string? AiId { get; set; }
}

public sealed class ReplyTraceStep
{
    public string Step { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Detail { get; set; }
}

public sealed class ReplyResult
{
    public string Reply { get; set; } = "";
    public bool Sent { get; set; }
    public bool Blocked { get; set; }
    public string? Reason { get; set; }
    public long ElapsedMs { get; set; }
    public List<ReplyTraceStep> Trace { get; set; } = new();
}
