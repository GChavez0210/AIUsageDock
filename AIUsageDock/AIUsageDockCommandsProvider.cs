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
    private readonly IUsageProvider[] _providers;
    private readonly UsageBand[] _bands;
    private readonly ICommandItem[] _topLevelCommands;
    private readonly Timer _refreshTimer;
    private int _refreshing;

    public AIUsageDockCommandsProvider()
    {
        Id = "AIUsageDock";
        DisplayName = "AI Usage Dock";
        Icon = new IconInfo("\uE950");

        _providers =
        [
            new ClaudeUsageProvider(),
            new CodexUsageProvider(),
            new AntigravityUsageProvider(),
        ];

        var pages = _providers.Select(provider => new UsagePage(provider)).ToArray();
        _bands = _providers.Zip(pages, (provider, page) => new UsageBand(provider, page)).ToArray();

        var commands = pages
            .Select((page, index) => (ICommandItem)new CommandItem(page)
            {
                Title = $"{_providers[index].DisplayName} usage",
                Icon = ProviderIcons.Filled(_providers[index]),
            })
            .ToList();

        commands.Add(new CommandItem(new RefreshAllCommand(() => RefreshAsync(force: true)))
        {
            Title = "Refresh AI usage",
        });

        _topLevelCommands = commands.ToArray();
        _refreshTimer = new Timer(
            async _ => await RefreshAsync(force: false),
            null,
            TimeSpan.Zero,
            RefreshInterval);
    }

    public override ICommandItem[] TopLevelCommands() => _topLevelCommands;

    public override ICommandItem[]? GetDockBands() => _bands.Select(band => (ICommandItem)band.DockItem).ToArray();

    private async Task RefreshAsync(bool force)
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
            await Task.WhenAll(_bands.Select(band => band.RefreshAsync(force)));
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    public override void Dispose()
    {
        _refreshTimer.Dispose();
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
        base.Dispose();
    }
}
