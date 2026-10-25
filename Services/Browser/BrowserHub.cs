using System.Collections.Concurrent;
using InstaMind.Models;

namespace InstaMind.Services.Browser;

/// <summary>
/// One Chrome window per connected page, never shared between them.
///
/// A runtime is created the first time a page is used and then reused, so each account keeps
/// its own window, its own profile folder, its own send counters and its own cool-downs.
/// Removing a page from the config drops its window on the next pass.
/// </summary>
public sealed class BrowserHub : IAsyncDisposable
{
    private readonly ConfigService _config;
    private readonly IWebHostEnvironment _env;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, BrowserRuntime> _runtimes = new();

    public BrowserHub(ConfigService config, IWebHostEnvironment env, IServiceProvider services)
    {
        _config = config;
        _env = env;
        _services = services;
    }

    /// <summary>The window of this page (created on first use).</summary>
    public BrowserRuntime ForPage(ConnectedPage page) =>
        _runtimes.GetOrAdd(page.Id, Create);

    /// <summary>An already-created window, without creating one.</summary>
    public BrowserRuntime? Find(string? pageId) =>
        !string.IsNullOrWhiteSpace(pageId) && _runtimes.TryGetValue(pageId, out var runtime)
            ? runtime
            : null;

    /// <summary>A window for every page browser mode should drive, in config order.</summary>
    public List<BrowserRuntime> Active() => _config.Current.Pages
        .Where(p => !string.IsNullOrWhiteSpace(p.Id))
        .Select(ForPage)
        .ToList();

    /// <summary>Windows selected by an optional page id (empty = every active page).</summary>
    public List<BrowserRuntime> Resolve(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return Active();
        var page = _config.FindPage(pageId);
        return page is null ? new List<BrowserRuntime>() : new List<BrowserRuntime> { ForPage(page) };
    }

    /// <summary>Everything this process ever opened — used to stop them all at once.</summary>
    public List<BrowserRuntime> All() => _runtimes.Values.OrderBy(r => r.PageId).ToList();

    /// <summary>Closes and forgets windows whose page was removed from the config.</summary>
    public async Task PruneAsync()
    {
        var known = _config.Current.Pages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var runtime in All())
        {
            if (known.Contains(runtime.PageId)) continue;
            _runtimes.TryRemove(runtime.PageId, out _);
            try { await runtime.DisposeAsync(); }
            catch { /* already gone */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in All())
        {
            try { await runtime.DisposeAsync(); }
            catch { /* already gone */ }
        }
        _runtimes.Clear();
    }

    private BrowserRuntime Create(string pageId) => new(
        pageId,
        _config,
        new BrowserSession(_services.GetRequiredService<ILogger<BrowserSession>>()),
        new BrowserStateStore(_env, pageId),
        _env,
        _services.GetRequiredService<ILogger<BrowserRuntime>>());
}
