using AIUsageDock.Models;
using AIUsageDock.Pages;
using AIUsageDock.Status;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Bands;

internal sealed class UsageBand
{
    private readonly IUsageProvider _provider;
    private readonly Func<string?> _dockChoice;
    private readonly Action<IReadOnlyList<UsageWindow>>? _windowsReported;
    private readonly Func<DisplayOptions> _display;
    private readonly Func<ProviderStatus?> _status;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ListItem _bandItem;
    private string? _appliedIconPath;
    private UsageResult? _last;

    /// <param name="dockChoice">The <see cref="Bands.DockChoice"/> value saved for this provider.</param>
    /// <param name="windowsReported">Told which windows each successful refresh returned,
    /// so the settings dropdown can offer them.</param>
    /// <param name="status">The provider's status page reading, named on the band during an incident.</param>
    public UsageBand(
        IUsageProvider provider,
        UsagePage page,
        Func<string?>? dockChoice = null,
        Action<IReadOnlyList<UsageWindow>>? windowsReported = null,
        Func<DisplayOptions>? display = null,
        Func<ProviderStatus?>? status = null,
        Func<DateTimeOffset>? clock = null)
    {
        _provider = provider;
        _dockChoice = dockChoice ?? (() => Bands.DockChoice.Auto);
        _windowsReported = windowsReported;
        _display = display ?? (() => DisplayOptions.Default);
        _status = status ?? (() => null);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _bandItem = new ListItem(page)
        {
            Title = $"{provider.DisplayName} —",
            Subtitle = "loading usage",
        };

        DockItem = new WrappedDockItem(
            [_bandItem],
            $"aiusagedock.dock.{provider.Id}",
            $"{provider.DisplayName} usage");

        ApplyIcon();
    }

    public WrappedDockItem DockItem { get; }

    public IUsageProvider Provider => _provider;

    /// <summary>The result of this band's last read; null before the first one.</summary>
    public UsageResult? Last => Volatile.Read(ref _last) ?? _provider.LastResult;

    /// <summary>The window this band shows for a reading, by the provider's "Dock shows" choice.</summary>
    public UsageWindow? Pick(UsageSnapshot snapshot) => Bands.DockChoice.Pick(snapshot, _dockChoice());

    internal string Title => _bandItem.Title;

    internal string Subtitle => _bandItem.Subtitle;

    public async Task RefreshAsync(bool force = false)
    {
        try
        {
            var result = await _provider.GetAsync(force);
            Volatile.Write(ref _last, result);
            if (result.Snapshot is not { } snapshot)
            {
                _bandItem.Title = $"{_provider.DisplayName} —";
                _bandItem.Subtitle = FriendlyError(result);
                return;
            }

            _windowsReported?.Invoke(snapshot.Windows);

            var primary = Pick(snapshot);
            if (primary is null)
            {
                _bandItem.Title = $"{_provider.DisplayName} —";
                _bandItem.Subtitle = "no limits reported";
                return;
            }

            var options = _display();
            var now = _clock();
            _bandItem.Title = $"{_provider.DisplayName} {Shown(primary, options)}%";

            var subtitleParts = new List<string>();
            if (options.ShowUsed)
            {
                subtitleParts.Add("used");
            }

            // The weekly figure is extra context; skip it when the band already shows a weekly window.
            if (snapshot.Weekly is { } weekly && primary.Kind != UsageWindowKind.Weekly)
            {
                subtitleParts.Add($"wk {Shown(weekly, options)}%");
            }

            if (PanelText.BandReset(primary, now, options.RelativeResets) is { } reset)
            {
                subtitleParts.Add(reset);
            }

            if (_status() is { HasIssue: true })
            {
                subtitleParts.Add("incident");
            }

            if (snapshot.IsStale)
            {
                subtitleParts.Add("stale");
            }

            _bandItem.Subtitle = subtitleParts.Count == 0 ? snapshot.Source : string.Join(" · ", subtitleParts);
        }
        catch
        {
            _bandItem.Title = $"{_provider.DisplayName} —";
            _bandItem.Subtitle = "refresh failed";
        }
    }

    internal static int Shown(UsageWindow window, DisplayOptions options) =>
        (int)Math.Round(PanelText.ShownPercent(window, options.ShowUsed));

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

    private void ApplyIcon()
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
