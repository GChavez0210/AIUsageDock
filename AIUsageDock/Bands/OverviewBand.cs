using AIUsageDock.Models;
using AIUsageDock.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Bands;

/// <summary>
/// One Dock band for every provider: the tightest one in the title, the rest in the
/// subtitle. Selecting it opens the overview page. It only reads what the provider
/// bands already fetched.
/// </summary>
internal sealed class OverviewBand
{
    private readonly Func<IReadOnlyList<UsageBand>> _bands;
    private readonly Func<DisplayOptions> _display;
    private readonly ListItem _bandItem;

    public OverviewBand(OverviewPage page, Func<IReadOnlyList<UsageBand>> bands, Func<DisplayOptions>? display = null)
    {
        _bands = bands;
        _display = display ?? (() => DisplayOptions.Default);
        _bandItem = new ListItem(page)
        {
            Title = "AI usage —",
            Subtitle = "loading usage",
            Icon = IconHelpers.FromRelativePath("Assets/Square44x44Logo.png"),
        };

        DockItem = new WrappedDockItem([_bandItem], "aiusagedock.dock.overview", "AI usage overview");
    }

    public WrappedDockItem DockItem { get; }

    internal string Title => _bandItem.Title;

    internal string Subtitle => _bandItem.Subtitle;

    public void Refresh()
    {
        var options = _display();
        var readings = _bands()
            .Select(band => (band.Provider.DisplayName, Window: band.Last?.Snapshot is { } snapshot ? band.Pick(snapshot) : null))
            .Where(reading => reading.Window is not null)
            .OrderBy(reading => reading.Window!.RemainingPercent)
            .Select(reading => $"{reading.DisplayName} {UsageBand.Shown(reading.Window!, options)}%")
            .ToList();

        if (readings.Count == 0)
        {
            _bandItem.Title = "AI usage —";
            _bandItem.Subtitle = "no usage read";
            return;
        }

        _bandItem.Title = readings[0];
        var rest = readings.Skip(1).ToList();
        if (options.ShowUsed)
        {
            rest.Insert(0, "used");
        }

        _bandItem.Subtitle = rest.Count == 0 ? "tightest limit" : string.Join(" · ", rest);
    }
}
