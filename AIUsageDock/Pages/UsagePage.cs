using System.Text;
using AIUsageDock.Models;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Pages;

internal sealed class UsagePage : ContentPage
{
    private const int BarWidth = 20;
    private const int LowThresholdPercent = 20;

    private readonly IUsageProvider _provider;

    public UsagePage(IUsageProvider provider)
    {
        _provider = provider;
        Id = $"aiusagedock.page.{provider.Id}";
        Name = $"{provider.DisplayName} usage";
        Title = $"{provider.DisplayName} usage";
        Icon = ProviderIcons.Filled(provider);
    }

    public override IContent[] GetContent()
    {
        UsageResult result;
        try
        {
            result = _provider.GetAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            result = UsageResult.Failure(UsageFailureReason.ProcessFailed, exception.Message);
        }

        return [new MarkdownContent(BuildMarkdown(result, _provider.DisplayName, _provider.Id, DateTimeOffset.UtcNow))];
    }

    internal static string BuildMarkdown(UsageResult result, string providerName, string providerId, DateTimeOffset now)
    {
        if (result.Snapshot is not { } snapshot)
        {
            return BuildFailureMarkdown(result, providerName, providerId);
        }

        var builder = new StringBuilder();

        // Title: provider plus plan when known, so the plan is visible without scrolling.
        builder.AppendLine(string.IsNullOrWhiteSpace(snapshot.Plan)
            ? $"# {snapshot.Provider}"
            : $"# {snapshot.Provider} · {snapshot.Plan}");
        builder.AppendLine();

        if (snapshot.IsStale)
        {
            builder.AppendLine($"> ⚠ Showing the last good reading from {DescribeAge(now - snapshot.FetchedAt)}. A live refresh did not succeed.");
            builder.AppendLine();
        }

        var windows = snapshot.Windows
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var window in windows)
        {
            AppendWindow(builder, window, now);
        }

        if (snapshot.AccountDetails.Count > 0)
        {
            builder.AppendLine("### Account");
            builder.AppendLine();
            foreach (var detail in snapshot.AccountDetails)
            {
                builder.AppendLine($"- **{detail.Label}:** {detail.Value}");
            }

            builder.AppendLine();
        }

        var notes = snapshot.AccountNotes.ToList();
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            notes.Add(result.Message);
        }

        foreach (var note in notes)
        {
            builder.AppendLine($"> {note}");
            builder.AppendLine();
        }

        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine($"{snapshot.Source} · updated {DescribeAge(now - snapshot.FetchedAt)} ({snapshot.FetchedAt.ToLocalTime():HH:mm:ss})");

        return builder.ToString();
    }

    private static void AppendWindow(StringBuilder builder, UsageWindow window, DateTimeOffset now)
    {
        var remaining = (int)Math.Round(window.RemainingPercent);
        var duration = DescribeDuration(window.DurationMinutes);

        builder.AppendLine(duration is null
            ? $"### {window.Label}"
            : $"### {window.Label} · {duration} window");

        var line = new StringBuilder($"{BuildBar(remaining, BarWidth)} **{remaining}%** left");
        foreach (var flag in DescribeFlags(window, remaining))
        {
            line.Append(" · ").Append(flag);
        }

        builder.AppendLine(line.ToString());
        builder.AppendLine();

        if (window.ResetsAt is { } reset)
        {
            var until = reset - now;
            builder.AppendLine(until <= TimeSpan.Zero
                ? "Resetting now"
                : $"Resets in {DescribeSpan(until)} · {reset.ToLocalTime():ddd MMM d, HH:mm}");
        }
        else
        {
            builder.AppendLine("No reset time reported");
        }

        builder.AppendLine();
    }

    private static IEnumerable<string> DescribeFlags(UsageWindow window, int remaining)
    {
        if (window.IsLocked)
        {
            yield return $"🔒 locked: {window.LockedReason}";
        }
        else if (remaining <= 0)
        {
            yield return "⛔ exhausted";
        }
        else if (remaining < LowThresholdPercent)
        {
            yield return "⚠ running low";
        }

        if (!string.IsNullOrWhiteSpace(window.Severity) &&
            !window.Severity.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            yield return window.Severity.ToLowerInvariant();
        }
    }

    private static string BuildFailureMarkdown(UsageResult result, string providerName, string providerId)
    {
        var builder = new StringBuilder();
        var (heading, hint) = result.FailureReason switch
        {
            UsageFailureReason.CliUnavailable => (
                $"{providerName} CLI not found",
                $"Install the {providerName} CLI and make sure `{CommandName(providerId)}` is on your PATH, then use **Refresh AI usage**."),
            UsageFailureReason.NotSignedIn => (
                $"{providerName} is not signed in",
                $"Run `{SignInCommand(providerId)}` in a terminal and sign in, then use **Refresh AI usage**."),
            UsageFailureReason.TimedOut => (
                $"{providerName} did not respond",
                "The CLI took too long to answer. Try **Refresh AI usage**; if it keeps happening, run the CLI once by hand to let it finish any pending update."),
            UsageFailureReason.InvalidData => (
                $"{providerName} reported no usage data",
                $"The CLI answered but did not include any limits. Open `{CommandName(providerId)}` and run `/usage` to confirm the account has an active subscription."),
            _ => (
                $"{providerName} usage unavailable",
                "Try **Refresh AI usage**. If the problem persists, run the CLI once by hand to see its own error."),
        };

        builder.AppendLine($"# {heading}");
        builder.AppendLine();
        builder.AppendLine(hint);
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            builder.AppendLine();
            builder.AppendLine("### Details");
            builder.AppendLine();
            builder.AppendLine($"> {result.Message}");
        }

        return builder.ToString();
    }

    private static string CommandName(string providerId) => providerId switch
    {
        "codex" => "codex",
        "claude" => "claude",
        "antigravity" => "agy",
        _ => providerId,
    };

    private static string SignInCommand(string providerId) => providerId switch
    {
        "codex" => "codex login",
        "claude" => "claude /login",
        "antigravity" => "agy",
        _ => providerId,
    };

    private static string BuildBar(int remaining, int width)
    {
        var filled = Math.Clamp((int)Math.Round(remaining / 100d * width), 0, width);
        return new string('█', filled) + new string('░', width - filled);
    }

    internal static string? DescribeDuration(int? minutes) => minutes switch
    {
        null or <= 0 => null,
        < 60 => $"{minutes}m",
        < 1440 when minutes % 60 == 0 => $"{minutes / 60}h",
        < 1440 => $"{minutes / 60}h {minutes % 60}m",
        _ when minutes % 1440 == 0 => $"{minutes / 1440}d",
        _ => $"{minutes / 1440}d {minutes % 1440 / 60}h",
    };

    /// <summary>Compact countdown: "3h 52m", "2d 2h", "45m", "under a minute".</summary>
    internal static string DescribeSpan(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes}m";
        }

        if (span < TimeSpan.FromDays(1))
        {
            return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        var days = (int)span.TotalDays;
        return span.Hours == 0 ? $"{days}d" : $"{days}d {span.Hours}h";
    }

    /// <summary>How old a reading is: "just now", "2 min ago", "3 h ago", "2 d ago".</summary>
    internal static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        return $"{(int)age.TotalDays} d ago";
    }
}
