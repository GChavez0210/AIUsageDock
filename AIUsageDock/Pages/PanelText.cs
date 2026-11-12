using System.Globalization;
using System.Text.RegularExpressions;
using AIUsageDock.Models;

namespace AIUsageDock.Pages;

/// <summary>How readings are worded and drawn, shared by the Dock bands and the pages.</summary>
internal sealed record DisplayOptions(
    BarColorMode BarColors = BarColorMode.Status,
    bool ShowUsed = false,
    bool RelativeResets = true,
    bool ShowPace = true,
    bool HidePersonalInfo = false,
    int HighUsedPercent = DisplayOptions.DefaultHighUsedPercent,
    int CriticalUsedPercent = DisplayOptions.DefaultCriticalUsedPercent)
{
    public const int DefaultHighUsedPercent = 75;
    public const int DefaultCriticalUsedPercent = 90;

    public static DisplayOptions Default { get; } = new();
}

/// <summary>
/// The wording of a usage row, following Win-CodexBar's tray panel
/// (<c>metricRowModel.ts</c>): "72% used", "Resets in 1h 23m", "33% in reserve · Lasts until reset".
/// </summary>
internal static partial class PanelText
{
    private const int SessionMinutes = 300;

    /// <summary>The percentage a row or band shows: used, or what is left.</summary>
    public static double ShownPercent(UsageWindow window, bool showUsed) =>
        showUsed ? Math.Clamp(window.UsedPercent, 0d, 100d) : window.RemainingPercent;

    public static string Percent(UsageWindow window, bool showUsed)
    {
        var shown = ShownPercent(window, showUsed);
        var suffix = showUsed ? "used" : "left";
        return shown is > 0 and < 1 ? $"<1% {suffix}" : $"{(int)Math.Round(shown)}% {suffix}";
    }

    /// <summary>"4d 12h", "1h 23m", "45m"; null when less than a second remains.</summary>
    public static string? Countdown(double seconds)
    {
        if (!(seconds >= 1))
        {
            return null;
        }

        var total = (long)Math.Max(1, Math.Ceiling(seconds / 60d));
        var days = total / 1440;
        var hours = total / 60 % 24;
        var minutes = total % 60;
        if (days > 0)
        {
            return hours > 0 ? $"{days}d {hours}h" : minutes > 0 ? $"{days}d {minutes}m" : $"{days}d";
        }

        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        return $"{minutes}m";
    }

    /// <summary>"Resets in 1h 23m", or "Resets 3:09 PM" / "Resets tomorrow, 3:09 PM" when absolute.</summary>
    public static string? Reset(UsageWindow window, DateTimeOffset now, bool relative)
    {
        if (window.ResetsAt is not { } reset)
        {
            return null;
        }

        if (relative)
        {
            return Countdown((reset - now).TotalSeconds) is { } countdown ? $"Resets in {countdown}" : "Resets now";
        }

        if (reset <= now)
        {
            return "Resets now";
        }

        var local = reset.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today)
        {
            return $"Resets {time}";
        }

        return local.Date == today.AddDays(1)
            ? $"Resets tomorrow, {time}"
            : $"Resets {local.ToString("MMM d", CultureInfo.CurrentCulture)}, {time}";
    }

    /// <summary>The short form for a Dock band: "1h 23m", or "resets 15:09" when absolute.</summary>
    public static string? BandReset(UsageWindow window, DateTimeOffset now, bool relative)
    {
        if (window.ResetsAt is not { } reset)
        {
            return null;
        }

        return relative
            ? Countdown((reset - now).TotalSeconds) ?? "resets now"
            : $"resets {reset.ToLocalTime():HH:mm}";
    }

    /// <summary>
    /// The pace line of a row as (left, right), or null when the window shows no pace:
    /// it is exhausted, has no pace, or too little of it has passed to say anything.
    /// </summary>
    public static (string Left, string? Right)? Pace(UsageWindow window, DateTimeOffset now)
    {
        if (!(window.UsedPercent < 100) || UsagePace.For(window, now) is not { } pace)
        {
            return null;
        }

        var session = window.DurationMinutes <= SessionMinutes;
        var expectsEnough = pace.ExpectedUsedPercent >= 3;
        if (session ? !expectsEnough : !expectsEnough && pace.EtaSeconds != 0)
        {
            return null;
        }

        var delta = (int)Math.Round(Math.Abs(pace.DeltaPercent));
        var left = delta == 0 || pace.Stage == PaceStage.OnTrack
            ? "On pace"
            : pace.IsDeficit ? $"{delta}% in deficit" : $"{delta}% in reserve";

        string? right = null;
        if (pace.WillLastToReset)
        {
            right = "Lasts until reset";
        }
        else if (pace.EtaSeconds is { } eta)
        {
            var duration = Countdown(eta);
            right = session
                ? duration is null ? "Projected empty now" : $"Projected empty in {duration}"
                : duration is null ? "Runs out now" : $"Runs out in {duration}";
        }

        return (left, right);
    }

    /// <summary>"Updated just now", "Updated 5 minutes ago", "Updated 1 hour ago".</summary>
    public static string Updated(DateTimeOffset fetchedAt, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (long)(now - fetchedAt).TotalSeconds);
        if (seconds < 60)
        {
            return "Updated just now";
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            return $"Updated {Count(minutes, "minute")} ago";
        }

        var hours = minutes / 60;
        return hours < 24 ? $"Updated {Count(hours, "hour")} ago" : $"Updated {Count(hours / 24, "day")} ago";
    }

    /// <summary>Replaces email addresses, for Hide personal information.</summary>
    public static string RedactEmails(string text) => EmailPattern().Replace(text, "•••");

    /// <summary>The email address inside an account detail such as "Name · name@example.com".</summary>
    public static string? EmailIn(string text) => EmailPattern().Match(text) is { Success: true } match ? match.Value : null;

    private static string Count(long count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();
}
