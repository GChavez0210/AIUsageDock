using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;

namespace AIUsageDock;

[Guid("a8900b57-444a-4352-99ba-9bb6f5d4316c")]
public sealed partial class AIUsageDockExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposed;
    private readonly AIUsageDockCommandsProvider _provider = new();

    public AIUsageDockExtension(ManualResetEvent disposed)
    {
        _disposed = disposed;
    }

    public object? GetProvider(ProviderType providerType) =>
        providerType == ProviderType.Commands ? _provider : null;

    public void Dispose()
    {
        _provider.Dispose();
        _disposed.Set();
    }
}

