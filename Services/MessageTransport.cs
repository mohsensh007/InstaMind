using InstaMind.Models;
using InstaMind.Services.Browser;

namespace InstaMind.Services;

/// <summary>How an outgoing DM actually leaves the app.</summary>
public interface IMessageTransport
{
    Task<SendResult> SendTextAsync(ConnectedPage? page, string recipientId, string text, CancellationToken ct = default);

    Task<ConnectionTest> TestPageAsync(ConnectedPage? page, CancellationToken ct = default);
}

/// <summary>
/// Picks the transport from <c>instagram.mode</c>:
/// <list type="bullet">
///   <item><c>sandbox</c> — nothing leaves the machine.</item>
///   <item><c>browser</c> — a real Chrome window with your own signed-in session (Selenium).</item>
///   <item><c>live</c> — the official Instagram Messaging API.</item>
/// </list>
/// The rest of the app (guardrails, prompts, transcripts, budgets) never knows the difference.
/// </summary>
public sealed class MessageRouter
{
    public const string ModeBrowser = "browser";

    private readonly ConfigService _config;
    private readonly InstagramGateway _graph;
    private readonly BrowserTransport _browser;

    public MessageRouter(ConfigService config, InstagramGateway graph, BrowserTransport browser)
    {
        _config = config;
        _graph = graph;
        _browser = browser;
    }

    public string Mode => _config.Current.Instagram.Mode;

    public Task<SendResult> SendTextAsync(ConnectedPage? page, string recipientId, string text, CancellationToken ct = default) =>
        Mode == ModeBrowser
            ? _browser.SendTextAsync(page, recipientId, text, ct)
            : _graph.SendTextAsync(page, recipientId, text, ct);

    public Task<ConnectionTest> TestPageAsync(ConnectedPage? page, CancellationToken ct = default) =>
        Mode == ModeBrowser
            ? _browser.TestPageAsync(page, ct)
            : _graph.TestPageAsync(page, ct);
}
