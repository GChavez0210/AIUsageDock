using AIUsageDock.Models;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Pages;

internal sealed class UsagePage : ContentPage
{
    private readonly IUsageProvider _provider;

    public UsagePage(IUsageProvider provider)
    {
        _provider = provider;
        Id = $"aiusagedock.page.{provider.Id}";
        Name = $"{provider.DisplayName} usage";
        Title = $"{provider.DisplayName} usage";
        Icon = ProviderIcons.Filled(provider);
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
                TemplateJson = UsageCard.Build(result, _provider.DisplayName, _provider.Id, DateTimeOffset.UtcNow),
                DataJson = "{}",
            },
        ];
    }
}
