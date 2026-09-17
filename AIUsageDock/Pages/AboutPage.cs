using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Pages;

internal sealed class AboutPage : ContentPage
{
    public const string AboutText = "AIUsageDock by Gabriel Chavez - Developed in Mexico with love";

    public AboutPage()
    {
        Id = "aiusagedock.page.about";
        Name = "About";
        Title = "About AI Usage Dock";
        Icon = new IconInfo("\uE946");
    }

    public override IContent[] GetContent() =>
    [
        new MarkdownContent { Body = $"# AI Usage Dock\n\n{AboutText}" },
    ];
}
