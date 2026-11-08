using AIUsageDock.Models;
using AIUsageDock.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Bands;

internal sealed class UsageBand
{
    private readonly IUsageProvider _provider;
    private readonly Func<string?> _dockChoice;
    private readonly Action<IReadOnlyList<UsageWindow>>? _windowsReported;
    private readonly ListItem _bandItem;
    private string? _appliedIconPath;

    /// <param name="dockChoice">The <see cref="Bands.DockChoice"/> value saved for this provider.</param>
    /// <param name="windowsReported">Told which windows each successful refresh returned,
    /// so the settings dropdown can offer them.</param>
    public UsageBand(
        IUsageProvider provider,
        UsagePage page,
        Func<string?>? dockChoice = null,
        Action<IReadOnlyList<UsageWindow>>? windowsReported = null)
    {
        _provider = provider;
        _dockChoice = dockChoice ?? (() => Bands.DockChoice.Auto);
        _windowsReported = windowsReported;
        _bandItem = new ListItem(page)
        {
            Title = $"{provider.DisplayName} —",
            Subtitle = "loading usage",
        };

        DockItem = new WrappedDockItem(
            [_bandItem],
            $"aiusagedock.dock.{provider.Id}",
            $"{provider.DisplayName} usage");

        ApplyIcon(low: false);
    }

    public WrappedDockItem DockItem { get; }

    public async Task RefreshAsync(bool force = false)
    {
        try
        {
            var result = await _provider.GetAsync(force);
            if (result.Snapshot is not { } snapshot)
            {
                _bandItem.Title = $"{_provider.DisplayName} —";
                _bandItem.Subtitle = FriendlyError(result);
                ApplyIcon(low: false);
                return;
            }

            _windowsReported?.Invoke(snapshot.Windows);

            var primary = Bands.DockChoice.Pick(snapshot, _dockChoice());
            if (primary is null)
            {
                _bandItem.Title = $"{_provider.DisplayName} —";
                _bandItem.Subtitle = "no limits reported";
                ApplyIcon(low: false);
                return;
            }

            var primaryLeft = (int)Math.Round(primary.RemainingPercent);
            _bandItem.Title = $"{_provider.DisplayName} {primaryLeft}%";

            var subtitleParts = new List<string>();
            // The weekly figure is extra context; skip it when the band already shows a weekly window.
            if (snapshot.Weekly is { } weekly && primary.Kind != UsageWindowKind.Weekly)
            {
                subtitleParts.Add($"wk {(int)Math.Round(weekly.RemainingPercent)}%");
            }

            if (primary.ResetsAt is { } reset)
            {
                subtitleParts.Add($"resets {reset.ToLocalTime():HH:mm}");
            }

            if (snapshot.IsStale)
            {
                subtitleParts.Add("stale");
            }

            _bandItem.Subtitle = subtitleParts.Count == 0 ? snapshot.Source : string.Join(" · ", subtitleParts);
            ApplyIcon(primaryLeft < 20);
        }
        catch
        {
            _bandItem.Title = $"{_provider.DisplayName} —";
            _bandItem.Subtitle = "refresh failed";
            ApplyIcon(low: false);
        }
    }

    private static string FriendlyError(UsageResult result) => result.FailureReason switch
    {
        UsageFailureReason.CliUnavailable => result.Message ?? "CLI not found",
        UsageFailureReason.NotSignedIn => "sign in with the CLI",
        UsageFailureReason.MissingApiKey => result.Message ?? "API key not set",
        UsageFailureReason.TimedOut => "refresh timed out",
        UsageFailureReason.InvalidData => result.Message ?? "no usage data",
        UsageFailureReason.ProcessFailed => result.Message ?? "CLI refresh failed",
        _ => result.Message ?? "usage unavailable",
    };

    private void ApplyIcon(bool low)
    {
        // Fixed-color SVGs render consistently in the Dock. SVG currentColor is not
        // resolved by every Command Palette image path and can appear transparent.
        var path = ProviderIcons.FilledPath(_provider);
        if (path == _appliedIconPath)
        {
            return;
        }

        try
        {
            _bandItem.Icon = IconHelpers.FromRelativePath(path);
            _appliedIconPath = path;
        }
        catch
        {
            // Preserve the previous icon if an asset is unavailable.
        }
    }
}
