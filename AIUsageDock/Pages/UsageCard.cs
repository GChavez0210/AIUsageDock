using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageDock.Models;

namespace AIUsageDock.Pages;

/// <summary>
/// Renders a usage result as an Adaptive Card. The bars are two weighted columns
/// with container styles, which the Command Palette renderer paints as filled blocks.
/// </summary>
internal static class UsageCard
{
    private const int LowThresholdPercent = 20;

    public static string Build(UsageResult result, string providerName, string providerId, DateTimeOffset now)
    {
        var body = new JsonArray();

        if (result.Snapshot is not { } snapshot)
        {
            AppendFailure(body, result, providerName, providerId);
        }
        else
        {
            AppendSnapshot(body, result, snapshot, now);
        }

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = body,
        };

        return card.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void AppendSnapshot(JsonArray body, UsageResult result, UsageSnapshot snapshot, DateTimeOffset now)
    {
        body.Add(Text(
            string.IsNullOrWhiteSpace(snapshot.Plan) ? snapshot.Provider : $"{snapshot.Provider} · {snapshot.Plan}",
            size: "Large",
            weight: "Bolder"));

        if (snapshot.IsStale)
        {
            body.Add(Note($"Showing the last good reading from {DescribeAge(now - snapshot.FetchedAt)}. A live refresh did not succeed.", "Warning"));
        }

        var windows = snapshot.Windows
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            AppendWindow(body, window, now);
        }

        if (snapshot.AccountDetails.Count > 0)
        {
            body.Add(Text("Account", weight: "Bolder", spacing: "Large"));
            var facts = new JsonArray();
            foreach (var detail in snapshot.AccountDetails)
            {
                facts.Add(new JsonObject { ["title"] = detail.Label, ["value"] = detail.Value });
            }

            body.Add(new JsonObject { ["type"] = "FactSet", ["facts"] = facts, ["spacing"] = "Small" });
        }

        foreach (var note in snapshot.AccountNotes)
        {
            body.Add(Note(note, "Default"));
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            body.Add(Note(result.Message, "Default"));
        }

        body.Add(Text(
            $"{snapshot.Source} · updated {DescribeAge(now - snapshot.FetchedAt)} ({snapshot.FetchedAt.ToLocalTime():HH:mm:ss})",
            size: "Small",
            subtle: true,
            spacing: "Large",
            separator: true));
    }

    private static void AppendWindow(JsonArray body, UsageWindow window, DateTimeOffset now)
    {
        var remaining = (int)Math.Round(window.RemainingPercent);
        var duration = DescribeDuration(window.DurationMinutes);
        var heading = duration is null ? window.Label : $"{window.Label} · {duration} window";

        // Label on the left, percentage on the right, on one line.
        body.Add(new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Large",
            ["columns"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "stretch",
                    ["items"] = new JsonArray { Text(heading, weight: "Bolder") },
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["items"] = new JsonArray { Text($"{remaining}% left", weight: "Bolder", color: BarColorName(window, remaining), horizontalAlignment: "Right") },
                },
            },
        });

        body.Add(Bar(remaining, BarStyle(window, remaining)));

        var flags = DescribeFlags(window, remaining).ToList();
        var resetText = window.ResetsAt is { } reset
            ? (reset - now) <= TimeSpan.Zero
                ? "Resetting now"
                : $"Resets in {DescribeSpan(reset - now)} · {reset.ToLocalTime():ddd MMM d, HH:mm}"
            : "No reset time reported";

        var detail = flags.Count == 0 ? resetText : $"{string.Join(" · ", flags)} · {resetText}";
        body.Add(Text(detail, size: "Small", subtle: flags.Count == 0, spacing: "Small",
            color: flags.Count == 0 ? null : BarColorName(window, remaining)));
    }

    /// <summary>
    /// A filled bar: one column per side, widths weighted by percentage. A 0-width
    /// side is omitted because the renderer collapses zero-weight columns unevenly.
    /// </summary>
    private static JsonObject Bar(int remaining, string filledStyle)
    {
        var columns = new JsonArray();
        var filled = Math.Clamp(remaining, 0, 100);
        var empty = 100 - filled;

        if (filled > 0)
        {
            columns.Add(BarSegment(filled, filledStyle));
        }

        if (empty > 0)
        {
            columns.Add(BarSegment(empty, "emphasis"));
        }

        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = columns,
        };
    }

    private static JsonObject BarSegment(int weight, string style) => new()
    {
        ["type"] = "Column",
        ["width"] = weight.ToString(),
        ["style"] = style,
        ["minHeight"] = "10px",
        ["items"] = new JsonArray(),
    };

    private static string BarStyle(UsageWindow window, int remaining) =>
        window.IsLocked || remaining <= 0 ? "attention"
        : remaining < LowThresholdPercent ? "warning"
        : "accent";

    private static string? BarColorName(UsageWindow window, int remaining) =>
        window.IsLocked || remaining <= 0 ? "Attention"
        : remaining < LowThresholdPercent ? "Warning"
        : null;

    private static IEnumerable<string> DescribeFlags(UsageWindow window, int remaining)
    {
        if (window.IsLocked)
        {
            yield return $"Locked: {window.LockedReason}";
        }
        else if (remaining <= 0)
        {
            yield return "Exhausted";
        }
        else if (remaining < LowThresholdPercent)
        {
            yield return "Running low";
        }

        if (!string.IsNullOrWhiteSpace(window.Severity) &&
            !window.Severity.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            yield return window.Severity.ToLowerInvariant();
        }
    }

    private static void AppendFailure(JsonArray body, UsageResult result, string providerName, string providerId)
    {
        var (heading, hint) = DescribeFailure(result.FailureReason, providerName, providerId);
        body.Add(Text(heading, size: "Large", weight: "Bolder"));
        body.Add(Text(hint, wrap: true));
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            body.Add(Note(result.Message, "Default"));
        }
    }

    private static JsonObject Note(string text, string style) => new()
    {
        ["type"] = "Container",
        ["style"] = style,
        ["spacing"] = "Medium",
        ["items"] = new JsonArray { Text(text, size: "Small", wrap: true) },
    };

    private static JsonObject Text(
        string text,
        string? size = null,
        string? weight = null,
        string? color = null,
        bool subtle = false,
        bool wrap = true,
        string? spacing = null,
        bool separator = false,
        string? horizontalAlignment = null)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = wrap };
        if (size is not null) block["size"] = size;
        if (weight is not null) block["weight"] = weight;
        if (color is not null) block["color"] = color;
        if (subtle) block["isSubtle"] = true;
        if (spacing is not null) block["spacing"] = spacing;
        if (separator) block["separator"] = true;
        if (horizontalAlignment is not null) block["horizontalAlignment"] = horizontalAlignment;
        return block;
    }

    internal static (string Heading, string Hint) DescribeFailure(UsageFailureReason reason, string providerName, string providerId) =>
        reason switch
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
