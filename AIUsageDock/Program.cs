using JPSoftworks.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock;

public static class Program
{
    [MTAThread]
    public static async Task Main(string[] args)
    {
        await ExtensionHostRunner.RunAsync(
            args,
            new ExtensionHostRunnerParameters
            {
                PublisherMoniker = "AIUsageDock",
                ProductMoniker = "AIUsageDock",
                ExtensionFactories = new()
                {
                    new DelegateExtensionFactory(disposed => new AIUsageDockExtension(disposed)),
                },
            });
    }
}

