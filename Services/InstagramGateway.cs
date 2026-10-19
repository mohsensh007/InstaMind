using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstaMind.Models;

namespace InstaMind.Services;

public sealed record SendResult(bool Ok, string? Error = null, string? MessageId = null);

public sealed record ConnectionTest(bool Ok, string Detail, string? Username = null);

/// <summary>
/// Sends DMs through the official Meta Graph endpoint
/// (POST /{version}/{ig-user-id}/messages) when mode = live.
/// In sandbox mode nothing leaves the machine.
/// </summary>
public sealed class InstagramGateway
{
    private readonly IHttpClientFactory _factory;
    private readonly ConfigService _config;
    private readonly ILogger<InstagramGateway> _log;

    public InstagramGateway(IHttpClientFactory factory, ConfigService config, ILogger<InstagramGateway> log)
    {
        _factory = factory;
        _config = config;
        _log = log;
    }

    public bool IsLive => _config.Current.Instagram.Mode == "live";

    /// <summary>Sends through the primary (first) connected page.</summary>
    public Task<SendResult> SendTextAsync(string recipientId, string text, CancellationToken ct = default) =>
        SendTextAsync(_config.Current.Pages.FirstOrDefault(), recipientId, text, ct);

    /// <summary>Sends a DM on behalf of a specific connected page.</summary>
    public async Task<SendResult> SendTextAsync(ConnectedPage? page, string recipientId, string text, CancellationToken ct = default)
    {
        var cfg = _config.Current.Instagram;
        if (cfg.Mode != "live")
        {
            return new SendResult(true, null, "sandbox");
        }

        if (page is null)
            return new SendResult(false, "هیچ صفحهٔ اینستاگرامی وصل نیست؛ اول در بخش «صفحات اینستاگرام» یک صفحه اضافه کن.");
        if (string.IsNullOrWhiteSpace(page.AccessToken))
            return new SendResult(false, "توکن دسترسی این صفحه تنظیم نشده است؛ دوباره وصلش کن.");
        if (string.IsNullOrWhiteSpace(page.IgUserId))
            return new SendResult(false, "شناسهٔ اکانت اینستاگرام (IG User ID) تنظیم نشده است.");
        if (string.IsNullOrWhiteSpace(recipientId))
            return new SendResult(false, "شناسهٔ گیرنده خالی است.");

        var url = $"{cfg.GraphBaseUrl.TrimEnd('/')}/{cfg.ApiVersion}/{page.IgUserId.Trim()}/messages";
        var body = new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = recipientId },
            ["message"] = new JsonObject { ["text"] = TrimToLimit(text, 950) }
        };

        var client = _factory.CreateClient("meta");
        client.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AccessToken.Trim());

            using var response = await client.SendAsync(request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = ReadError(payload) ?? $"HTTP {(int)response.StatusCode}";
                _log.LogWarning("Instagram send failed: {Error}", error);
                return new SendResult(false, error);
            }

            using var doc = JsonDocument.Parse(payload);
            var messageId = doc.RootElement.TryGetProperty("message_id", out var mid) ? mid.GetString() : null;
            return new SendResult(true, null, messageId);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(false, "اینستاگرام در ۳۰ ثانیه پاسخ نداد.");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Instagram request failed");
            return new SendResult(false, "اتصال به اینستاگرام برقرار نشد.");
        }
        catch (JsonException)
        {
            return new SendResult(false, "پاسخ اینستاگرام قابل خواندن نبود.");
        }
    }

    public Task<ConnectionTest> TestAsync(CancellationToken ct = default) =>
        TestPageAsync(_config.Current.Pages.FirstOrDefault(), ct);

    /// <summary>Calls /me with the page's own token and reports username + account type.</summary>
    public async Task<ConnectionTest> TestPageAsync(ConnectedPage? page, CancellationToken ct = default)
    {
        var cfg = _config.Current.Instagram;
        if (page is null || string.IsNullOrWhiteSpace(page.AccessToken))
            return new ConnectionTest(false, "این صفحه توکن دسترسی ندارد؛ دوباره وصلش کن.");
        if (cfg.Mode != "live")
            return new ConnectionTest(false, "حالت فعلی «سندباکس» است؛ برای تست واقعی حالت را روی «واقعی» بگذار و ذخیره کن.", page.Username);

        var url = $"{cfg.GraphBaseUrl.TrimEnd('/')}/{cfg.ApiVersion}/me?fields=user_id,username,account_type";

        var client = _factory.CreateClient("meta");
        client.Timeout = TimeSpan.FromSeconds(25);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", page.AccessToken.Trim());

            using var response = await client.SendAsync(request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionTest(false, ReadError(payload) ?? $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(payload);
            var username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
            var accountType = doc.RootElement.TryGetProperty("account_type", out var a) ? a.GetString() : null;
            return new ConnectionTest(true, $"اتصال برقرار است{(accountType is null ? "" : $" (نوع اکانت: {accountType})")}.", username ?? page.Username);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException)
        {
            return new ConnectionTest(false, "اتصال به Meta برقرار نشد: " + ex.Message);
        }
    }

    private static string TrimToLimit(string text, int limit) =>
        text.Length <= limit ? text : text[..limit];

    private static string? ReadError(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var message)) return message.GetString();
                return err.ToString();
            }
        }
        catch { /* not json */ }

        return string.IsNullOrWhiteSpace(payload) ? null : payload[..Math.Min(payload.Length, 300)];
    }
}
