using InstaMind.Models;

namespace InstaMind.Services.Browser;

/// <summary>
/// Sends through the Selenium window that belongs to the destination page. The recipient id
/// is the Instagram thread id the worker read from that page's own inbox.
///
/// The page must be known: with more than one account connected there is no "default"
/// window, and answering page 1's messages from page 2's window is exactly the mix-up this
/// transport exists to prevent.
/// </summary>
public sealed class BrowserTransport : IMessageTransport
{
    private readonly BrowserHub _hub;

    public BrowserTransport(BrowserHub hub) => _hub = hub;

    private BrowserRuntime? Resolve(ConnectedPage? page)
    {
        if (page is not null) return _hub.ForPage(page);

        var active = _hub.Active();
        return active.Count == 1 ? active[0] : null;
    }

    public async Task<SendResult> SendTextAsync(
        ConnectedPage? page, string recipientId, string text, CancellationToken ct = default)
    {
        var runtime = Resolve(page);
        if (runtime is null)
        {
            return new SendResult(false,
                "صفحهٔ فرستنده مشخص نیست؛ برای هر پیام باید صفحهٔ متعلق به همان گفتگو انتخاب شود.");
        }

        return await runtime.SendReplyAsync(recipientId, text, ct);
    }

    public async Task<ConnectionTest> TestPageAsync(ConnectedPage? page, CancellationToken ct = default)
    {
        var runtime = Resolve(page);
        if (runtime is null)
        {
            return new ConnectionTest(false, "صفحه‌ای برای تست مشخص نشده است.", null);
        }

        var (result, _) = await runtime.TestAsync(ct);
        return result;
    }
}
