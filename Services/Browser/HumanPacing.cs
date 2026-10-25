using InstaMind.Models;

namespace InstaMind.Services.Browser;

/// <summary>
/// Timing helpers for browser mode.
///
/// The goal is not to look "undetectable" — it is to behave like a person: read a message,
/// think for a moment, type at a human speed and never machine-gun replies. All pauses are
/// jittered and clustered around the middle of their range, so a run does not produce a
/// perfectly regular rhythm.
/// </summary>
public static class HumanPacing
{
    private static readonly TimeSpan TehranOffset = TimeSpan.FromMinutes(210);

    /// <summary>Random value in [min, max], clustered around the middle.</summary>
    public static int Between(int min, int max)
    {
        if (max <= min) return Math.Max(0, min);
        var t = (Random.Shared.NextDouble() + Random.Shared.NextDouble()) / 2.0;
        return min + (int)Math.Round((max - min) * t);
    }

    /// <summary>Delay for a single keystroke, with longer pauses at punctuation.</summary>
    public static int KeyDelayMs(string rune, int minMs, int maxMs)
    {
        var baseMs = Between(minMs, maxMs);
        if (rune is "." or "!" or "?" or "…" or "،" or "؛" or ":" or "؟")
            baseMs += Between(90, 280);
        else if (rune == " ")
            baseMs += Between(10, 60);
        return baseMs;
    }

    /// <summary>True when the local (Tehran) clock is inside the configured window.</summary>
    public static bool WithinWorkingHours(BrowserConfig cfg, DateTimeOffset now)
    {
        if (cfg.AlwaysOn) return true;

        var local = now.ToOffset(TehranOffset);
        var time = TimeOnly.FromDateTime(local.DateTime);

        if (!TimeOnly.TryParse(cfg.WorkingHoursStart, out var start)) start = new TimeOnly(9, 0);
        if (!TimeOnly.TryParse(cfg.WorkingHoursEnd, out var end)) end = new TimeOnly(23, 30);

        return start < end
            ? time >= start && time < end
            : time >= start || time < end; // window crosses midnight
    }

    public static string LocalClock(DateTimeOffset now) =>
        now.ToOffset(TehranOffset).ToString("HH:mm");

    public static string TehranDay(DateTimeOffset now) =>
        now.ToOffset(TehranOffset).ToString("yyyy-MM-dd");
}
