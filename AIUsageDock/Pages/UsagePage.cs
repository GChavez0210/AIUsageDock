using AIUsageDock.Models;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Pages;

internal sealed class UsagePage : ContentPage
{
    private readonly IUsageProvider _provider;
    private readonly Func<BarColorMode> _barColors;

    /// <param name="settings">The extension settings form, reachable from the page's
    /// command bar so a page opened from the Dock is one step from its options.</param>
    /// <param name="refreshAll">Refreshes every provider; the page redraws afterwards.</param>
    public UsagePage(
        IUsageProvider provider,
        Func<BarColorMode>? barColors = null,
        ICommand? settings = null,
        Func<Task>? refreshAll = null)
    {
        _provider = provider;
        _barColors = barColors ?? (() => BarColorMode.Status);
        Id = $"aiusagedock.page.{provider.Id}";
        Name = $"{provider.DisplayName} usage";
        Title = $"{provider.DisplayName} usage";
        Icon = ProviderIcons.Filled(provider);

        var commands = new List<IContextItem>();
        if (refreshAll is not null)
        {
            commands.Add(new CommandContextItem(new Commands.RefreshPageCommand(refreshAll, () => RaiseItemsChanged()))
            {
                Title = "Refresh",
                Subtitle = "Read usage again now",
                Icon = new IconInfo(""),
            });
        }

        if (settings is not null)
        {
            commands.Add(new CommandContextItem(settings)
            {
                Title = "Settings",
                Subtitle = "Providers, Dock values, API key and bar colors",
                Icon = new IconInfo(""),
            });
        }

        if (commands.Count > 0)
        {
            Commands = [.. commands];
        }
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

        return
        [
            new FormContent
            {
                TemplateJson = UsageCard.Build(result, _provider.DisplayName, _provider.Id, DateTimeOffset.UtcNow, _barColors()),
                DataJson = "{}",
            },
        ];
    }
}
