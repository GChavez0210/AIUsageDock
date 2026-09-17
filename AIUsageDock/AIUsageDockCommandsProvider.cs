using AIUsageDock.Bands;
using AIUsageDock.Commands;
using AIUsageDock.Models;
using AIUsageDock.Pages;
using AIUsageDock.Providers;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock;

public sealed partial class AIUsageDockCommandsProvider : CommandProvider, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly ProviderSelectionSettings _selection;
    private readonly IUsageProvider[] _providers;
    private readonly UsageBand[] _bands;
    private readonly ICommandItem[] _usageCommands;
    private readonly ICommandItem[] _utilityCommands;
    private readonly Timer _refreshTimer;
    private int _refreshing;

    public AIUsageDockCommandsProvider() : this(new ProviderSelectionSettings(ProviderSelectionSettings.DefaultPath), startTimer: true)
    {
    }

    internal AIUsageDockCommandsProvider(ProviderSelectionSettings selection, bool startTimer, IUsageProvider[]? providers = null)
    {
        Id = "AIUsageDock";
        DisplayName = "AI Usage Dock";
        Icon = IconHelpers.FromRelativePath("Assets/Square44x44Logo.png");
        _selection = selection;
        Settings = selection.ToolkitSettings;
        _selection.Changed += OnSelectionChanged;

        _providers = providers ??
        [
            new ClaudeUsageProvider(),
            new CodexUsageProvider(),
            new AntigravityUsageProvider(),
        ];

        var pages = _providers.Select(provider => new UsagePage(provider)).ToArray();
        _bands = _providers.Zip(pages, (provider, page) => new UsageBand(provider, page)).ToArray();

        _usageCommands = pages
            .Select((page, index) => (ICommandItem)new CommandItem(page)
            {
                Title = $"{_providers[index].DisplayName} usage",
                Icon = ProviderIcons.Filled(_providers[index]),
            })
            .ToArray();

        _utilityCommands =
        [
            new CommandItem(new RefreshAllCommand(() => RefreshAsync(force: true)))
            {
                Title = "Refresh AI usage",
            },
            new CommandItem(selection.ToolkitSettings.SettingsPage)
            {
                Title = "Select subscriptions",
                Subtitle = "Choose which usage bands to show",
                Icon = new IconInfo("\uE713"),
            },
        ];

        _refreshTimer = new Timer(
            async _ => await RefreshAsync(force: false),
            null,
            startTimer ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
            RefreshInterval);
    }

    public override ICommandItem[] TopLevelCommands() =>
        _usageCommands.Where((_, index) => IsEnabled(index)).Concat(_utilityCommands).ToArray();

    public override ICommandItem[]? GetDockBands() =>
        _bands.Where((_, index) => IsEnabled(index)).Select(band => (ICommandItem)band.DockItem).ToArray();

    private bool IsEnabled(int index) => _selection.IsEnabled(_providers[index].Id);

    private void OnSelectionChanged(object? sender, EventArgs args) => RaiseItemsChanged();

    internal async Task RefreshAsync(bool force)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            // Bands update their own Title/Subtitle, which notifies the Dock in place.
            // Do not call RaiseItemsChanged here: it makes Command Palette reload every
            // provider's commands and rebuild the Dock, resetting the other bands.
            await Task.WhenAll(_bands.Where((_, index) => IsEnabled(index)).Select(band => band.RefreshAsync(force)));
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    public override void Dispose()
    {
        _refreshTimer.Dispose();
        _selection.Changed -= OnSelectionChanged;
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
        base.Dispose();
    }
}
