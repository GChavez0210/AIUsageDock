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
        Assert.Equal(3, bands.Length);
        Assert.All(bands, band =>
        {
            Assert.NotNull(band.Command);
            Assert.False(string.IsNullOrWhiteSpace(band.Command.Id));

            var page = Assert.IsAssignableFrom<IListPage>(band.Command);
            var items = page.GetItems();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.NotNull(item.Icon));
        });

    }
}
