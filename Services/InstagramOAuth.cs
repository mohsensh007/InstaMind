using System.Security.Cryptography;
using System.Text.Json;
using InstaMind.Models;
using Microsoft.Extensions.Caching.Memory;

namespace InstaMind.Services;

public sealed record OAuthCompletion(
    bool Ok,
    string? Error = null,
    string? Username = null,
    string? IgUserId = null,
    bool Subscribed = false,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Official "Business Login for Instagram" flow.
/// The account owner types their password on instagram.com — this app only ever
/// receives a short-lived code and exchanges it server-side for a token.
/// </summary>
public sealed class InstagramOAuth
{
    public const string Scopes = "instagram_business_basic,instagram_business_manage_messages";

    private const string AuthorizeEndpoint = "https://www.instagram.com/oauth/authorize";
    private const string TokenEndpoint = "https://api.instagram.com/oauth/access_token";
    private const string GraphEndpoint = "https://graph.instagram.com";

    private readonly IHttpClientFactory _factory;
    private readonly ConfigService _config;
    private readonly IMemoryCache _cache;
    private readonly ILogger<InstagramOAuth> _log;

    public InstagramOAuth(
        IHttpClientFactory factory,
        ConfigService config,
        IMemoryCache cache,
        ILogger<InstagramOAuth> log)
    {
        _factory = factory;
        _config = config;
        _cache = cache;
        _log = log;
    }

    /// <summary>The exact URI that must be allow-listed in the Meta dashboard.</summary>
    public string ResolveRedirectUri(HttpRequest request)
    {
        var configured = _config.Current.Instagram.RedirectUri;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        return $"{request.Scheme}://{request.Host}{request.PathBase}/oauth/instagram/callback";
    }

    public string BuildAuthorizeUrl(string redirectUri, out string state)
    {
        var ig = _config.Current.Instagram;
        state = Base64Url(RandomNumberGenerator.GetBytes(24));
        _cache.Set(StateKey(state), true, TimeSpan.FromMinutes(15));

        var parameters = new (string Key, string Value)[]
        {
            ("client_id", ig.AppId.Trim()),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", Scopes),
            ("state", state)
        };

        var query = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return $"{AuthorizeEndpoint}?{query}";
    }

    public bool ConsumeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        var key = StateKey(state);
        if (!_cache.TryGetValue(key, out _)) return false;
        _cache.Remove(key);
        return true;
    }

    public async Task<OAuthCompletion> CompleteAsync(string code, string redirectUri, CancellationToken ct)
    {
        var ig = _config.Current.Instagram;
        if (string.IsNullOrWhiteSpace(ig.AppId) || string.IsNullOrWhiteSpace(ig.AppSecret))
            return new OAuthCompletion(false, "App ID یا App Secret تنظیم نشده است.");
        if (string.IsNullOrWhiteSpace(code))
            return new OAuthCompletion(false, "کد مجوز از اینستاگرام دریافت نشد.");

        try
        {
            var client = _factory.CreateClient("meta");
            client.Timeout = TimeSpan.FromSeconds(35);

            // 1) authorization code -> short-lived token (valid ~1h)
            using var form = new MultipartFormDataContent
            {
                { new StringContent(ig.AppId.Trim()), "client_id" },
                { new StringContent(ig.AppSecret.Trim()), "client_secret" },
                { new StringContent("authorization_code"), "grant_type" },
                { new StringContent(redirectUri), "redirect_uri" },
                { new StringContent(code), "code" }
            };

            using var tokenResponse = await client.PostAsync(TokenEndpoint, form, ct);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                _log.LogWarning("Instagram token exchange failed: {Body}", Truncate(tokenBody));
                return new OAuthCompletion(false, ReadError(tokenBody) ?? $"HTTP {(int)tokenResponse.StatusCode}");
            }

            using var tokenDoc = JsonDocument.Parse(tokenBody);
            var payload = tokenDoc.RootElement;
            if (payload.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                payload = data[0];
            }

            var shortToken = ReadString(payload, "access_token");
            var igUserId = ReadString(payload, "user_id") ?? "";
            if (string.IsNullOrWhiteSpace(shortToken))
                return new OAuthCompletion(false, "اینستاگرام توکنی برنگرداند.");

            // 2) short-lived -> long-lived (valid ~60 days)
            var longToken = shortToken;
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
            var longLived = await ExchangeLongLivedAsync(shortToken, ct);
            if (longLived.Ok)
            {
                longToken = longLived.Token!;
                expiresAt = longLived.ExpiresAt ?? expiresAt;
            }
            else
            {
                _log.LogWarning("Long-lived token exchange failed: {Error}", longLived.Error);
            }

            // 3) profile (username + canonical IG user id)
            var profile = await GetProfileAsync(longToken, ct);
            if (!string.IsNullOrWhiteSpace(profile.UserId)) igUserId = profile.UserId;

            // 4) persist connection as a page entry (pages list is the source of truth)
            _config.UpsertPage(new ConnectedPage
            {
                IgUserId = igUserId,
                Username = profile.Username ?? "",
                AccessToken = longToken,
                TokenExpiresAt = expiresAt,
                ConnectedAt = DateTimeOffset.UtcNow
            });

            // 5) subscribe this account to the app's messages webhook
            var subscribed = await SubscribeAsync(longToken, igUserId, ct);
            if (subscribed)
            {
                _config.UpdatePage(igUserId, p => p.Subscribed = true);
            }

            return new OAuthCompletion(
                true,
                null,
                profile.Username,
                igUserId,
                subscribed,
                expiresAt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new OAuthCompletion(false, "اینستاگرام در ۳۵ ثانیه پاسخ نداد.");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Instagram OAuth request failed");
            return new OAuthCompletion(false, "اتصال به اینستاگرام برقرار نشد.");
        }
        catch (JsonException)
        {
            return new OAuthCompletion(false, "پاسخ اینستاگرام قابل خواندن نبود.");
        }
    }

    public async Task<(bool Ok, string Message)> RefreshAsync(CancellationToken ct, string? pageId = null)
    {
        var page = string.IsNullOrWhiteSpace(pageId)
            ? _config.Current.Pages.FirstOrDefault()
            : _config.FindPage(pageId);

        if (page is null || string.IsNullOrWhiteSpace(page.AccessToken))
            return (false, "توکنی برای تمدید وجود ندارد.");

        try
        {
            var client = _factory.CreateClient("meta");
            client.Timeout = TimeSpan.FromSeconds(30);

            var url = $"{GraphEndpoint}/refresh_access_token?grant_type=ig_refresh_token" +
                      $"&access_token={Uri.EscapeDataString(page.AccessToken)}";

            using var response = await client.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (false, ReadError(body) ?? "تمدید توکن ممکن نشد.");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var token = ReadString(root, "access_token");
            var expiresIn = ReadInt(root, "expires_in");
            if (string.IsNullOrWhiteSpace(token))
                return (false, "پاسخ تمدید توکن نامعتبر بود.");

            var newExpiry = expiresIn > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
                : DateTimeOffset.UtcNow.AddDays(60);

            _config.UpdatePage(page.Id, p =>
            {
                p.AccessToken = token;
                p.TokenExpiresAt = newExpiry;
            });

            var who = string.IsNullOrWhiteSpace(page.Username) ? "" : " @" + page.Username;
            return (true, $"توکن{who} تمدید شد.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, "تمدید توکن ممکن نشد: " + ex.Message);
        }
    }

    private async Task<(bool Ok, string? Token, DateTimeOffset? ExpiresAt, string? Error)> ExchangeLongLivedAsync(
        string shortToken, CancellationToken ct)
    {
        var ig = _config.Current.Instagram;
        var url = $"{GraphEndpoint}/access_token?grant_type=ig_exchange_token" +
                  $"&client_secret={Uri.EscapeDataString(ig.AppSecret.Trim())}" +
                  $"&access_token={Uri.EscapeDataString(shortToken)}";

        var client = _factory.CreateClient("meta");
        using var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return (false, null, null, ReadError(body) ?? "تبدیل توکن بلندمدت ناموفق بود.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = ReadString(root, "access_token");
        var expiresIn = ReadInt(root, "expires_in");
        if (string.IsNullOrWhiteSpace(token)) return (false, null, null, "توکن بلندمدت خالی بود.");

        var expiresAt = expiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : DateTimeOffset.UtcNow.AddDays(60);

        return (true, token, expiresAt, null);
    }

    private async Task<(string? UserId, string? Username)> GetProfileAsync(string token, CancellationToken ct)
    {
        var version = _config.Current.Instagram.ApiVersion;
        var url = $"{GraphEndpoint}/{version}/me?fields=user_id,username,account_type" +
                  $"&access_token={Uri.EscapeDataString(token)}";

        try
        {
            var client = _factory.CreateClient("meta");
            using var response = await client.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Instagram profile fetch failed: {Body}", Truncate(body));
                return (null, null);
            }

            using var doc = JsonDocument.Parse(body);
            return (ReadString(doc.RootElement, "user_id"), ReadString(doc.RootElement, "username"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "Instagram profile fetch failed");
            return (null, null);
        }
    }

    private async Task<bool> SubscribeAsync(string token, string igUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(igUserId)) return false;

        try
        {
            var version = _config.Current.Instagram.ApiVersion;
            var url = $"{GraphEndpoint}/{version}/{igUserId}/subscribed_apps" +
                      $"?subscribed_fields=messages&access_token={Uri.EscapeDataString(token)}";

            var client = _factory.CreateClient("meta");
            using var response = await client.PostAsync(url, content: null, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("subscribed_apps failed: {Body}", Truncate(body));
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("success", out var success) &&
                   success.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "subscribed_apps failed");
            return false;
        }
    }

    private static string StateKey(string state) => "ig-oauth-state:" + state;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string? ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_message", out var message)) return message.GetString();
            if (root.TryGetProperty("error_description", out var description)) return description.GetString();
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested))
                    return nested.GetString();
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
            }
        }
        catch { /* not json */ }

        return string.IsNullOrWhiteSpace(body) ? null : Truncate(body);
    }

    private static string Truncate(string text) =>
        text.Length <= 300 ? text : text[..300];
}
