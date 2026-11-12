using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageDock.Models;
using AIUsageDock.Status;

namespace AIUsageDock.Pages;

/// <summary>
/// Renders a usage result as an Adaptive Card. The bars are two weighted columns
/// with container styles, which the Command Palette renderer paints as filled blocks.
/// </summary>
internal enum BarColorMode
{
    /// <summary>Green, yellow or red by how much is used.</summary>
    Status,

    /// <summary>The theme accent for every bar. The status container styles render
    /// as pale tints in the light theme, which some people find hard to see.</summary>
    Accent,
}

/// <summary>One provider's place on the overview page.</summary>
internal sealed record OverviewEntry(string ProviderName, string ProviderId, UsageResult? Result, ProviderStatus? Status = null);

/// <summary>
/// The card layout follows Win-CodexBar's tray panel: a header with the provider name,
/// when it was read and the plan, then one row per usage window with its bar, the
/// percentage, the reset and a pace line.
/// </summary>
internal static class UsageCard
{
    /// <param name="notices">Status lines shown under the heading, such as a background
    /// refresh in progress or one that failed, as (text, container style) pairs.</param>
    /// <param name="status">The provider's status page reading; shown only during an incident.</param>
    public static string Build(
        UsageResult result,
        string providerName,
        string providerId,
        DateTimeOffset now,
        DisplayOptions? options = null,
        IReadOnlyList<(string Text, string Style)>? notices = null,
        ProviderStatus? status = null)
    {
        options ??= DisplayOptions.Default;
        var body = new JsonArray();
        var headerRows = 1;

        if (result.Snapshot is { } snapshot)
        {
            headerRows = AppendHeader(body, snapshot, now, options, large: true);
        }
        else
        {
            AppendFailure(body, result, providerName, providerId);
        }

        var at = Math.Min(headerRows, body.Count);
        if (status is { HasIssue: true })
        {
            var mild = status.Indicator is StatusIndicator.Minor or StatusIndicator.Maintenance;
            body.Insert(at++, Line($"{providerName} status · {status.Describe()}", mild ? "Warning" : "Attention"));
        }

        foreach (var (text, style) in notices ?? [])
        {
            body.Insert(at++, Line(text, style));
        }

        if (result.Snapshot is { } shown)
        {
            AppendSnapshot(body, result, shown, now, options);
        }

        return Wrap(body);
    }

    /// <summary>
    /// Every enabled provider on one page, one compact card each, like the
    /// "All" tab of Win-CodexBar's tray panel.
    /// </summary>
    public static string BuildOverview(
        IReadOnlyList<OverviewEntry> entries,
        DateTimeOffset now,
        DisplayOptions? options = null,
        IReadOnlyList<(string Text, string Style)>? notices = null)
    {
        options ??= DisplayOptions.Default;
        var body = new JsonArray { Text("AI usage", size: "Large", weight: "Bolder") };
        foreach (var (text, style) in notices ?? [])
        {
            body.Add(Note(text, style));
        }

        if (entries.Count == 0)
        {
            body.Add(Text("No providers are turned on. Open **Settings** to choose which subscriptions to show.", subtle: true, spacing: "Medium"));
        }

        foreach (var entry in entries)
        {
            var section = new JsonArray();
            if (entry.Result?.Snapshot is { } snapshot)
            {
                AppendHeader(section, snapshot, now, options, large: false);
                AppendOverviewStatus(section, entry);
                foreach (var window in Ordered(snapshot))
                {
                    AppendWindow(section, window, now, options, spacing: "Medium");
                }
            }
            else
            {
                section.Add(Text(entry.ProviderName, size: "Medium", weight: "Bolder"));
                AppendOverviewStatus(section, entry);
                if (entry.Result is null)
                {
                    section.Add(Text("Not fetched yet", size: "Small", subtle: true, spacing: "None"));
                }
                else
                {
                    var (heading, _) = DescribeFailure(entry.Result.FailureReason, entry.ProviderName, entry.ProviderId);
                    section.Add(Text(heading, size: "Small", color: "Attention", spacing: "None"));
                    if (!string.IsNullOrWhiteSpace(entry.Result.Message))
                    {
                        section.Add(Text(Shown(entry.Result.Message, options), size: "Small", subtle: true, spacing: "None"));
                    }
                }
            }

            body.Add(new JsonObject
            {
                ["type"] = "Container",
                ["separator"] = true,
                ["spacing"] = "Large",
                ["items"] = section,
            });
        }

        return Wrap(body);
    }

    /// <summary>Shown when a page opens before the provider has ever been read.</summary>
    public static string BuildLoading(string providerName)
    {
        var body = new JsonArray
        {
            Text($"{providerName} usage", size: "Large", weight: "Bolder"),
            Text("Reading usage…", subtle: true, spacing: "Medium"),
        };
        return Wrap(body);
    }

    private static string Wrap(JsonArray body)
    {
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = body,
        };

        return card.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void AppendOverviewStatus(JsonArray section, OverviewEntry entry)
    {
        if (entry.Status is { HasIssue: true } status)
        {
            section.Add(Text($"Status · {status.Describe()}", size: "Small", color: "Warning", spacing: "None"));
        }
    }

    /// <summary>
    /// The tray panel's card header: the provider name with the account email opposite,
    /// then when it was read with the plan opposite. Returns how many rows it added.
    /// </summary>
    private static int AppendHeader(JsonArray body, UsageSnapshot snapshot, DateTimeOffset now, DisplayOptions options, bool large)
    {
        var email = options.HidePersonalInfo
            ? null
            : snapshot.AccountDetails
                .Where(detail => detail.Label == "Account")
                .Select(detail => PanelText.EmailIn(detail.Value))
                .FirstOrDefault(value => value is not null);

        body.Add(Split(
            Text(snapshot.Provider, size: "Medium", weight: "Bolder"),
            email is null ? null : Text(email, size: "Small", subtle: true, wrap: false, horizontalAlignment: "Right")));

        body.Add(Split(
            Text(large ? $"{PanelText.Updated(snapshot.FetchedAt, now)} · {snapshot.Source}" : PanelText.Updated(snapshot.FetchedAt, now), size: "Small", subtle: true),
            string.IsNullOrWhiteSpace(snapshot.Plan) ? null : Text(snapshot.Plan, size: "Small", subtle: true, wrap: false, horizontalAlignment: "Right"),
            spacing: "None"));
        return 2;
    }

    private static void AppendSnapshot(JsonArray body, UsageResult result, UsageSnapshot snapshot, DateTimeOffset now, DisplayOptions options)
    {
        if (snapshot.IsStale)
        {
            body.Add(Note($"Showing the last good reading from {DescribeAge(now - snapshot.FetchedAt)}. A live refresh did not succeed.", "Warning"));
        }

        foreach (var window in Ordered(snapshot))
        {
            AppendWindow(body, window, now, options, spacing: "Medium");
        }

        // The header already carries the plan and the account email; the rest goes in
        // small label/value rows so a card fits without scrolling.
        var details = snapshot.AccountDetails
            .Where(detail => !options.HidePersonalInfo || detail.Label is not ("Account" or "Organization"))
            .Where(detail => !(detail.Label == "Plan" && detail.Value == snapshot.Plan))
            .Select(detail => detail.Label == "Account" ? detail with { Value = WithoutEmail(detail.Value) } : detail)
            .Where(detail => detail.Value.Length > 0)
            .ToList();
        for (var index = 0; index < details.Count; index++)
        {
            var row = Split(
                Text(details[index].Label, size: "Small", subtle: true),
                Text(Shown(details[index].Value, options), size: "Small", horizontalAlignment: "Right"),
                spacing: index == 0 ? "Medium" : "None");
            if (index == 0)
            {
                row["separator"] = true;
            }

            body.Add(row);
        }

        foreach (var note in snapshot.AccountNotes)
        {
            body.Add(Note(Shown(note, options), "Default"));
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            body.Add(Note(Shown(result.Message, options), "Default"));
        }
    }

    /// <summary>"Name · name@example.com" without the address, which the header already shows.</summary>
    private static string WithoutEmail(string account) => string.Join(
        " · ",
        account.Split(" · ").Where(part => PanelText.EmailIn(part) != part.Trim()));

    private static IEnumerable<UsageWindow> Ordered(UsageSnapshot snapshot) => snapshot.Windows
        .OrderBy(item => item.Kind)
        .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase);

    private static string Shown(string text, DisplayOptions options) =>
        options.HidePersonalInfo ? PanelText.RedactEmails(text) : text;

    /// <summary>
    /// One usage window as the tray panel draws it: the title, the bar, the percentage
    /// with the reset opposite, then the pace line.
    /// </summary>
    private static void AppendWindow(JsonArray body, UsageWindow window, DateTimeOffset now, DisplayOptions options, string spacing)
    {
        var duration = DescribeDuration(window.DurationMinutes);
        var heading = duration is null ? window.Label : $"{window.Label} · {duration}";
        body.Add(Text(heading, weight: "Bolder", spacing: spacing));

        var flags = DescribeFlags(window, options).ToList();
        var pace = options.ShowPace ? PanelText.Pace(window, now) : null;
        if (pace is { } line)
        {
            flags.Add(line.Left);
        }

        // The tick marks where an even spend would stand, in the bar's own direction.
        int? paceTick = null;
        if (pace is not null && UsagePace.For(window, now) is { Stage: not PaceStage.OnTrack } measured)
        {
            paceTick = (int)Math.Round(options.ShowUsed ? measured.ExpectedUsedPercent : 100 - measured.ExpectedUsedPercent);
        }

        var shown = (int)Math.Round(PanelText.ShownPercent(window, options.ShowUsed));
        body.Add(Bar(shown, BarStyle(window, options), paceTick));

        var reset = PanelText.Reset(window, now, options.RelativeResets) ?? "No reset time reported";
        body.Add(Split(
            Text(PanelText.Percent(window, options.ShowUsed), size: "Small", weight: "Bolder", color: BarColorName(window, options)),
            Text(reset, size: "Small", subtle: true, horizontalAlignment: "Right"),
            spacing: "Small"));

        if (flags.Count > 0)
        {
            body.Add(Split(
                Text(string.Join(" · ", flags), size: "Small", subtle: true),
                pace?.Right is { } right ? Text(right, size: "Small", subtle: true, horizontalAlignment: "Right") : null,
                spacing: "None"));
        }
    }

    /// <summary>Two texts on one line, the second right-aligned; the second may be absent.</summary>
    private static JsonObject Split(JsonObject left, JsonObject? right, string? spacing = null)
    {
        var columns = new JsonArray
        {
            new JsonObject { ["type"] = "Column", ["width"] = "stretch", ["items"] = new JsonArray { left } },
        };
        if (right is not null)
        {
            columns.Add(new JsonObject { ["type"] = "Column", ["width"] = "auto", ["items"] = new JsonArray { right } });
        }

        var set = new JsonObject { ["type"] = "ColumnSet", ["columns"] = columns };
        if (spacing is not null)
        {
            set["spacing"] = spacing;
        }

        return set;
    }

    /// <summary>
    /// 0 healthy, 1 at or past the high-usage threshold, 2 at or past the critical one
    /// or locked. Uses the rounded percentage, so the colour always matches the number shown.
    /// </summary>
    internal static int Tier(UsageWindow window, DisplayOptions options)
    {
        var used = 100 - (int)Math.Round(window.RemainingPercent);
        return window.IsLocked || used >= options.CriticalUsedPercent ? 2
            : used >= options.HighUsedPercent ? 1
            : 0;
    }


    /// <summary>
    /// A thin rounded bar drawn as an image (see <see cref="BarImage"/>). The alt text
    /// carries the percentage and colour for screen readers.
    /// </summary>
    private static JsonObject Bar(int shown, string style, int? paceTick) => new()
    {
        ["type"] = "Image",
        ["url"] = BarImage.DataUri(shown, BarColor(style), paceTick),
        ["size"] = "Stretch",
        ["spacing"] = "Small",
        ["altText"] = $"{Math.Clamp(shown, 0, 100)}% {style}",
    };

    // Green while healthy, yellow when running low, red when almost gone; or one accent colour.
    internal static string BarStyle(UsageWindow window, DisplayOptions options) =>
        options.BarColors == BarColorMode.Accent
            ? "accent"
            : Tier(window, options) switch { 2 => "attention", 1 => "warning", _ => "good" };

    internal static uint BarColor(string style) => style switch
    {
        "attention" => 0xE5484D,
        "warning" => 0xE2A200,
        "good" => 0x30A46C,
        _ => 0x0A84FF,
    };

    private static string BarColorName(UsageWindow window, DisplayOptions options) =>
        Tier(window, options) switch { 2 => "Attention", 1 => "Warning", _ => "Good" };

    private static IEnumerable<string> DescribeFlags(UsageWindow window, DisplayOptions options)
    {
        if (window.IsLocked)
        {
            yield return $"Locked: {window.LockedReason}";
        }
        else if ((int)Math.Round(window.RemainingPercent) <= 0)
        {
            yield return "Exhausted";
        }
        else if (Tier(window, options) == 2)
        {
            yield return "Almost out";
        }
        else if (Tier(window, options) == 1)
        {
            yield return "Running low";
        }

        // "Almost out · critical" says the same thing twice; the provider's own severity
        // is only worth showing when the thresholds here have not flagged the window.
        if (Tier(window, options) == 0 &&
            !string.IsNullOrWhiteSpace(window.Severity) &&
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

    /// <summary>A one-line notice under the header. A padded box would push a window off the page.</summary>
    private static JsonObject Line(string text, string style) => Text(
        text,
        size: "Small",
        color: style is "Attention" or "Warning" ? style : null,
        subtle: style == "Default",
        spacing: "Small");

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
            UsageFailureReason.MissingApiKey => (
                $"{providerName} API key missing or rejected",
                $"Create an API key at ollama.com/settings/keys and paste it into **Ollama API key** in this extension's settings (or set the `{Providers.OllamaUsageProvider.ApiKeyVariable}` environment variable), then use **Refresh AI usage**."),
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
