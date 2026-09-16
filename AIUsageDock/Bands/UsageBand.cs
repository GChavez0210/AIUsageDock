using AIUsageDock.Models;
using AIUsageDock.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Bands;

internal sealed class UsageBand
{
    private readonly IUsageProvider _provider;
    private readonly ListItem _bandItem;
    private string? _appliedIconPath;

    public UsageBand(IUsageProvider provider, UsagePage page)
    {
        _provider = provider;
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

            var primary = snapshot.Session ?? snapshot.MostConstrained;
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
            if (snapshot.Weekly is { } weekly)
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
