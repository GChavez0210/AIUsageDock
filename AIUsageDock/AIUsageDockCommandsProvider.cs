using AIUsageDock.Bands;
using AIUsageDock.Commands;
using AIUsageDock.Models;
using AIUsageDock.Notifications;
using AIUsageDock.Pages;
using AIUsageDock.Providers;
using AIUsageDock.Status;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock;

public sealed partial class AIUsageDockCommandsProvider : CommandProvider, IDisposable
{
    private readonly ProviderSelectionSettings _selection;
    private readonly IUsageProvider[] _providers;
    private readonly UsageBand[] _bands;
    private readonly OverviewBand _overviewBand;
    private readonly OverviewPage _overviewPage;
    private readonly ICommandItem _overviewCommand;
    private readonly ICommandItem[] _usageCommands;
    private readonly ICommandItem[] _utilityCommands;
    private readonly StatusPoller? _status;
    private readonly QuotaNotifier? _notifier;
    private readonly Timer _refreshTimer;
    private readonly bool _timerStarted;
    private readonly object _refreshGate = new();
    private Task? _currentRefresh;
    private bool _currentForced;

    public AIUsageDockCommandsProvider() : this(
        new ProviderSelectionSettings(ProviderSelectionSettings.DefaultPath),
        startTimer: true,
        status: new StatusPoller(),
        toasts: new WindowsToastSink())
    {
    }

    /// <param name="status">Reads provider status pages; null (the default, for tests) makes no network calls.</param>
    /// <param name="toasts">Where quota warnings go; null sends none.</param>
    internal AIUsageDockCommandsProvider(
        ProviderSelectionSettings selection,
        bool startTimer,
        IUsageProvider[]? providers = null,
        StatusPoller? status = null,
        IToastSink? toasts = null)
    {
        Id = "AIUsageDock";
        DisplayName = "AI Usage Dock";
        Icon = IconHelpers.FromRelativePath("Assets/Square44x44Logo.png");
        _selection = selection;
        _status = status;
        _notifier = toasts is null ? null : new QuotaNotifier(toasts);
        Settings = selection.ToolkitSettings;
        _selection.Changed += OnSelectionChanged;
        _selection.ApiKeyChanged += OnApiKeyChanged;
        _selection.DockChoiceChanged += OnDisplayChanged;
        _selection.DisplayChanged += OnDisplayChanged;
        _selection.StatusChecksChanged += OnStatusChecksChanged;
        _selection.RefreshIntervalChanged += OnRefreshIntervalChanged;

        _providers = providers ??
        [
            new ClaudeUsageProvider(),
            new CodexUsageProvider(),
            new AntigravityUsageProvider(),
            new OllamaUsageProvider(() => selection.OllamaApiKey),
        ];

        var refreshCommand = new RefreshAllCommand(() => RefreshAsync(force: true));

        // Detail pages opened from the Dock get Refresh and a gear to the settings form,
        // so both are one step away instead of buried in Command Palette settings. When a
        // page's background read brings a new reading, its band redraws from the cache.
        var pages = _providers
            .Select((provider, index) => new UsagePage(
                provider,
                () => selection.Display,
                selection.ToolkitSettings.SettingsPage,
                () => _ = RedrawBandAsync(index),
                status: () => StatusFor(provider.Id)))
            .ToArray();
        _bands = _providers
            .Zip(pages, (provider, page) => new UsageBand(
                provider,
                page,
                () => selection.DockChoiceFor(provider.Id),
                windows => selection.ReportWindows(provider.Id, windows),
                () => selection.Display,
                () => StatusFor(provider.Id)))
            .ToArray();

        _overviewPage = new OverviewPage(
            OverviewEntries,
            RefreshAsync,
            () => selection.Display,
            selection.ToolkitSettings.SettingsPage);
        _overviewBand = new OverviewBand(_overviewPage, EnabledBands, () => selection.Display);
        _overviewCommand = new CommandItem(_overviewPage)
        {
            Title = "AI usage overview",
            Subtitle = "Every subscription on one page",
        };

        _usageCommands = pages
            .Select((page, index) => (ICommandItem)new CommandItem(page)
            {
                Title = $"{_providers[index].DisplayName} usage",
                Icon = ProviderIcons.Filled(_providers[index]),
            })
            .ToArray();

        _utilityCommands =
        [
            new CommandItem(refreshCommand)
            {
                Title = "Refresh AI usage",
            },
            new CommandItem(selection.ToolkitSettings.SettingsPage)
            {
                Title = "Select subscriptions",
                Subtitle = "Choose which usage bands to show",
                Icon = new IconInfo(""),
            },
        ];

        _timerStarted = startTimer;
        _refreshTimer = new Timer(
            async _ => await RefreshAsync(force: false),
            null,
            startTimer ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
            selection.RefreshInterval);
    }

    public override ICommandItem[] TopLevelCommands()
    {
        var usage = _usageCommands.Where((_, index) => IsEnabled(index)).ToArray();
        return usage.Length == 0 ? _utilityCommands : [_overviewCommand, .. usage, .. _utilityCommands];
    }

    public override ICommandItem[]? GetDockBands()
    {
        var bands = EnabledBands().Select(band => (ICommandItem)band.DockItem).ToArray();
        return bands.Length == 0 ? bands : [.. bands, _overviewBand.DockItem];
    }

    private bool IsEnabled(int index) => _selection.IsEnabled(_providers[index].Id);

    private UsageBand[] EnabledBands() => _bands.Where((_, index) => IsEnabled(index)).ToArray();

    private ProviderStatus? StatusFor(string providerId) =>
        _selection.StatusChecks ? _status?.For(providerId) : null;

    private IReadOnlyList<OverviewEntry> OverviewEntries() => EnabledBands()
        .Select(band => new OverviewEntry(band.Provider.DisplayName, band.Provider.Id, band.Last, StatusFor(band.Provider.Id)))
        .ToArray();

    private void OnSelectionChanged(object? sender, EventArgs args) => RaiseItemsChanged();

    // A newly saved API key should show up without waiting for the next tick.
    private void OnApiKeyChanged(object? sender, EventArgs args) => _ = RefreshAsync(force: true);

    // Cached readings are reused, so this only redraws the bands with the new choice.
    private void OnDisplayChanged(object? sender, EventArgs args) => _ = RefreshAsync(force: false);

    private void OnStatusChecksChanged(object? sender, EventArgs args)
    {
        if (!_selection.StatusChecks)
        {
            _status?.Clear();
        }

        _ = RefreshAsync(force: false);
    }

    // The next background read comes one new period after the change.
    private void OnRefreshIntervalChanged(object? sender, EventArgs args)
    {
        if (_timerStarted)
        {
            _refreshTimer.Change(_selection.RefreshInterval, _selection.RefreshInterval);
        }
    }

    private async Task RedrawBandAsync(int index)
    {
        await _bands[index].RefreshAsync(force: false).ConfigureAwait(false);
        _overviewBand.Refresh();
        Warn(_bands[index]);
    }

    /// <summary>Reads every enabled provider. A call made while a read is running joins it.</summary>
    internal Task RefreshAsync(bool force)
    {
        lock (_refreshGate)
        {
            if (_currentRefresh is { IsCompleted: false } running)
            {
                if (!force || _currentForced)
                {
                    return running;
                }

                // A forced read must not be answered by a cached one already under way.
                _currentForced = true;
                return _currentRefresh = running.ContinueWith(
                    _ => RefreshCoreAsync(force: true),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }

            _currentForced = force;
            return _currentRefresh = Task.Run(() => RefreshCoreAsync(force));
        }
    }

    private async Task RefreshCoreAsync(bool force)
    {
        var bands = EnabledBands();

        // Bands update their own Title/Subtitle, which notifies the Dock in place.
        // Do not call RaiseItemsChanged here: it makes Command Palette reload every
        // provider's commands and rebuild the Dock, resetting the other bands.
        var usage = Task.WhenAll(bands.Select(band => band.RefreshAsync(force)));
        var statusChanged = false;
        if (_status is not null && _selection.StatusChecks)
        {
            statusChanged = await _status.PollAsync(bands.Select(band => band.Provider.Id), force).ConfigureAwait(false);
        }

        await usage.ConfigureAwait(false);

        if (statusChanged)
        {
            // The incident flag is part of the band text; cached readings make this cheap.
            await Task.WhenAll(bands.Select(band => band.RefreshAsync(force: false))).ConfigureAwait(false);
        }

        _overviewBand.Refresh();
        foreach (var band in bands)
        {
            Warn(band);
        }
    }

    private void Warn(UsageBand band)
    {
        if (_notifier is not null && _selection.QuotaWarnings && band.Last?.Snapshot is { } snapshot)
        {
            _notifier.Observe(band.Provider.DisplayName, band.Provider.Id, snapshot, _selection.Display, DateTimeOffset.UtcNow);
        }
    }

    public override void Dispose()
    {
        _refreshTimer.Dispose();
        _selection.Changed -= OnSelectionChanged;
        _selection.ApiKeyChanged -= OnApiKeyChanged;
        _selection.DockChoiceChanged -= OnDisplayChanged;
        _selection.DisplayChanged -= OnDisplayChanged;
        _selection.StatusChecksChanged -= OnStatusChecksChanged;
        _selection.RefreshIntervalChanged -= OnRefreshIntervalChanged;
        _status?.Dispose();
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
        base.Dispose();
    }
}
