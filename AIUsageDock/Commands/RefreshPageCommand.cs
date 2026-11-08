using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Commands;

/// <summary>
/// Starts a forced read for one detail page and redraws it at once, so the page
/// shows "Refreshing…" and then the result instead of freezing until the read ends.
/// </summary>
internal sealed partial class RefreshPageCommand : InvokableCommand
{
    private readonly Action _start;
    private readonly Action _redraw;

    public RefreshPageCommand(Action start, Action redraw)
    {
        _start = start;
        _redraw = redraw;
        Name = "Refresh";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        _start();
        _redraw();
        return CommandResult.KeepOpen();
    }
}
