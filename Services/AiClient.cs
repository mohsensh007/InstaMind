using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstaMind.Models;

namespace InstaMind.Services;

public sealed class AiMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";

    public AiMessage() { }

    public AiMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}

public sealed class AiResult
{
    public bool Ok { get; set; }
    public string Text { get; set; } = "";
    public string? Error { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
}

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint
/// (OpenAI, OpenRouter, Groq, Baseten, Together, Ollama, ...).
/// </summary>
public sealed class AiClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<AiClient> _log;

    public AiClient(IHttpClientFactory factory, ILogger<AiClient> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<AiResult> ChatAsync(
        AiConfig cfg,
        IEnumerable<AiMessage> messages,
        double? temperature = null,
        int? maxTokens = null,
        bool jsonMode = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            return new AiResult { Ok = false, Error = "کلید API هوش مصنوعی تنظیم نشده است." };
        }

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(cfg.Model) ? "gpt-4o-mini" : cfg.Model,
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
            ["temperature"] = temperature ?? cfg.Temperature,
            ["max_tokens"] = maxTokens ?? cfg.MaxTokens,
            ["stream"] = false
        };

        var first = await SendAsync(cfg, payload, jsonMode, ct);
        if (!first.Ok && jsonMode && first.Status is 400 or 404 or 422)
        {
            // Some gateways do not support response_format: retry as plain chat.
            payload.Remove("response_format");
            first = await SendAsync(cfg, payload, jsonMode: false, ct);
        }
        return first.Result;
    }

    private async Task<(bool Ok, int Status, AiResult Result)> SendAsync(
        AiConfig cfg, JsonObject payload, bool jsonMode, CancellationToken ct)
    {
        if (jsonMode) payload["response_format"] = new JsonObject { ["type"] = "json_object" };

        var url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        var client = _factory.CreateClient("ai");
        client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

            using var response = await client.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var message = ExtractError(body) ?? $"HTTP {status}";
                _log.LogWarning("AI call failed with {Status}: {Message}", status, message);
                return (false, status, new AiResult { Ok = false, Error = message });
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (false, status, new AiResult { Ok = false, Error = message ?? "خطای نامشخص از سرویس هوش مصنوعی" });
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return (false, status, new AiResult { Ok = false, Error = "پاسخ سرویس هوش مصنوعی خالی بود." });
            }

            var content = ReadContent(choices[0]);
            if (string.IsNullOrWhiteSpace(content))
            {
                return (false, status, new AiResult { Ok = false, Error = "مدل پاسخی تولید نکرد." });
            }

            var result = new AiResult { Ok = true, Text = content.Trim() };
            if (root.TryGetProperty("usage", out var usage))
            {
                result.PromptTokens = ReadInt(usage, "prompt_tokens");
                result.CompletionTokens = ReadInt(usage, "completion_tokens");
            }
            return (true, status, result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, 0, new AiResult { Ok = false, Error = $"سرویس هوش مصنوعی در {cfg.TimeoutSeconds} ثانیه پاسخ نداد." });
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "AI request failed");
            return (false, 0, new AiResult { Ok = false, Error = "اتصال به سرویس هوش مصنوعی برقرار نشد." });
        }
        catch (JsonException)
        {
            return (false, 0, new AiResult { Ok = false, Error = "پاسخ سرویس هوش مصنوعی قابل خواندن نبود." });
        }
    }

    public async Task<AiResult> PingAsync(AiConfig cfg, CancellationToken ct = default)
    {
        var result = await ChatAsync(
            cfg,
            new[]
            {
                new AiMessage("system", "You are a connectivity test. Reply with exactly: OK"),
                new AiMessage("user", "ping")
            },
            temperature: 0,
            maxTokens: 20,
            ct: ct);

        return result;
    }

    private static string? ReadContent(JsonElement choice)
    {
        if (!choice.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("content", out var content)) return null;

        if (content.ValueKind == JsonValueKind.String) return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? "");
                }
            }
            return string.Join("\n", parts);
        }
        return null;
    }

    private static int ReadInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                {
                    return m.GetString();
                }
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
            }
        }
        catch { /* not json */ }

        return string.IsNullOrWhiteSpace(body) ? null : body[..Math.Min(body.Length, 300)];
    }
}
