using Microsoft.CommandPalette.Extensions;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class DockBandContractTests
{
    [Fact]
    public void EveryDockBandHasAStableCommandId()
    {
        using var provider = new AIUsageDockCommandsProvider(
            new ProviderSelectionSettings(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            startTimer: false);

        var bands = provider.GetDockBands();

        Assert.NotNull(bands);
        Assert.Equal(5, bands.Length);
        Assert.All(bands, band =>
        {
            Assert.NotNull(band.Command);
            Assert.False(string.IsNullOrWhiteSpace(band.Command.Id));

            var page = Assert.IsAssignableFrom<IListPage>(band.Command);
            var items = page.GetItems();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.NotNull(item.Icon));

            // The detail page a band opens carries Refresh and a gear to the settings form.
            var detail = Assert.IsAssignableFrom<IContentPage>(items[0].Command);
            var refresh = Assert.IsAssignableFrom<ICommandContextItem>(detail.Commands[0]);
            Assert.Equal("Refresh", refresh.Title);
            Assert.IsAssignableFrom<IInvokableCommand>(refresh.Command);
            var settings = Assert.IsAssignableFrom<ICommandContextItem>(detail.Commands[1]);
            Assert.Equal("Settings", settings.Title);
            Assert.IsAssignableFrom<IContentPage>(settings.Command);
        });

    }
}
