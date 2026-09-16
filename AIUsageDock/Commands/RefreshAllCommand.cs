using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Commands;

internal sealed partial class RefreshAllCommand : InvokableCommand
{
    private readonly Func<Task> _refresh;

    public RefreshAllCommand(Func<Task> refresh)
    {
        _refresh = refresh;
        Id = "aiusagedock.command.refresh";
        Name = "Refresh AI usage";
        Icon = new IconInfo("\uE72C");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            _refresh().GetAwaiter().GetResult();
            return CommandResult.ShowToast("AI usage refreshed");
        }
        catch (Exception exception)
        {
            return CommandResult.ShowToast($"Usage refresh failed: {exception.Message}");
        }
    }
}

