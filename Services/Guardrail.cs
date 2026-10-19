using System.Text.RegularExpressions;
using InstaMind.Models;

namespace InstaMind.Services;

/// <summary>
/// Second and third layers of protection, independent of the model:
/// keyword gate on input, cleanup + leak/length checks on output.
/// </summary>
public static partial class Guardrail
{
    private static readonly string[] LeakMarkers =
    {
        "system prompt", "my instructions", "my prompt", "the prompt above",
        "دستورالعمل سیستم", "پرامپت سیستم", "دستورهای محرمانه", "پرامپت من"
    };

    public static string? MatchBlockedKeyword(string text, IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (keyword.Length < 2) continue;
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return keyword;
        }
        return null;
    }

    /// <summary>Removes markdown artefacts the model still slips in and normalises whitespace.</summary>
    public static string Sanitize(string raw, PersonaConfig persona)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Trim();

        // Drop a leading "Assistant:" / "دستیار:" speaker label.
        text = SpeakerPrefix().Replace(text, "");
        // Strip the most common markdown markers.
        text = MarkdownBold().Replace(text, "$1");
        text = MarkdownHeading().Replace(text, "");
        text = text.Replace("`", "");
        // Normalise line breaks and collapse spaces.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = ExcessiveNewlines().Replace(text, "\n\n");
        text = Spaces().Replace(text, " ");
        text = text.Trim().Trim('"', '\'', '«', '»').Trim();

        if (!persona.UseEmoji)
        {
            text = Emoji().Replace(text, "").Trim();
            text = Spaces().Replace(text, " ");
        }

        return Clamp(text, persona.MaxChars);
    }

    /// <summary>Returns a reason when the outgoing text must not be sent as-is.</summary>
    public static string? CheckOutgoing(string reply, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(reply)) return "پاسخ خالی بود";

        var blocked = MatchBlockedKeyword(reply, cfg.Guardrails.BlockedKeywords);
        if (blocked is not null) return $"کلمهٔ مسدود در پاسخ: {blocked}";

        if (cfg.Guardrails.NeverRevealPrompt)
        {
            foreach (var marker in LeakMarkers)
            {
                if (reply.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return "پاسخ به پرامپت/دستورالعمل اشاره می‌کرد";
                }
            }
        }

        return null;
    }

    public static string Clamp(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text[..Math.Max(0, maxChars - 1)];
        var lastSpace = cut.LastIndexOfAny(new[] { ' ', '\n', '،', '.', '!', '?' });
        if (lastSpace > maxChars / 2) cut = cut[..lastSpace];
        return cut.TrimEnd(' ', '،', ',', '.') + "…";
    }

    [GeneratedRegex(@"(?i)^(assistant|دستیار|ai|bot|پشتیبان)\s*[:：]\s*")]
    private static partial Regex SpeakerPrefix();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex MarkdownBold();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlines();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\u2600-\u27BF\uD83C-\uDBFF\uDC00-\uDFFF\uFE0F]")]
    private static partial Regex Emoji();
}
