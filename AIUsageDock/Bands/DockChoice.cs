using AIUsageDock.Models;

namespace AIUsageDock.Bands;

/// <summary>
/// Which usage window a provider's Dock band shows. Stored per provider as a string
/// so the choice survives restarts even before the provider has reported windows.
/// </summary>
internal static class DockChoice
{
    /// <summary>Session when reported, otherwise the tightest limit (the original behaviour).</summary>
    public const string Auto = "auto";

    public const string Tightest = "tightest";

    private const string WindowPrefix = "window:";

    public static IReadOnlyList<(string Value, string Title)> Fixed { get; } =
    [
        (Auto, "Auto (session, else the tightest limit)"),
        (Tightest, "Tightest limit"),
    ];

    /// <summary>
    /// Kind is part of the value because one label can name several windows
    /// (Antigravity uses the model group name for both its 5h and weekly buckets).
    /// </summary>
    public static string ValueFor(UsageWindow window) => $"{WindowPrefix}{window.Kind}:{window.Label}";

    public static string TitleFor(UsageWindow window) =>
        DescribeKind(window.Kind) is { } period ? $"{window.Label} · {period}" : window.Label;

    /// <summary>A readable title for a saved window value whose window is not reported right now.</summary>
    public static string? TitleForValue(string value)
    {
        if (!TryParse(value, out var kind, out var label))
        {
            return null;
        }

        return DescribeKind(kind) is { } period ? $"{label} · {period}" : label;
    }

    public static UsageWindow? Pick(UsageSnapshot snapshot, string? choice)
    {
        if (string.Equals(choice, Tightest, StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.MostConstrained;
        }

        if (choice is not null && TryParse(choice, out var kind, out var label))
        {
            var match = snapshot.Windows.FirstOrDefault(window => window.Kind == kind && window.Label == label);
            if (match is not null)
            {
                return match;
            }
        }

        // Auto, or a chosen window the provider stopped reporting.
        return snapshot.Session ?? snapshot.MostConstrained;
    }

    private static bool TryParse(string value, out UsageWindowKind kind, out string label)
    {
        kind = default;
        label = string.Empty;
        if (!value.StartsWith(WindowPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = value[WindowPrefix.Length..];
        var separator = rest.IndexOf(':');
        if (separator <= 0 || !Enum.TryParse(rest[..separator], out kind))
        {
            return false;
        }

        label = rest[(separator + 1)..];
        return label.Length > 0;
    }

    private static string? DescribeKind(UsageWindowKind kind) => kind switch
    {
        UsageWindowKind.Session => "5h",
        UsageWindowKind.Weekly => "7d",
        UsageWindowKind.Monthly => "month",
        _ => null,
    };
}
