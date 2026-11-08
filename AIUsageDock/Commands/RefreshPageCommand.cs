using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Commands;

/// <summary>
/// Refreshes every provider from a detail page and redraws that page in place,
/// so the bars update without leaving it.
/// </summary>
internal sealed partial class RefreshPageCommand : InvokableCommand
{
    private readonly Func<Task> _refresh;
    private readonly Action _redraw;

    public RefreshPageCommand(Func<Task> refresh, Action redraw)
    {
        _refresh = refresh;
        _redraw = redraw;
        Name = "Refresh";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            _refresh().GetAwaiter().GetResult();
        }
        catch
        {
            // The page shows the provider's own error or stale reading after the redraw.
        }

        _redraw();
        return CommandResult.KeepOpen();
    }
}
