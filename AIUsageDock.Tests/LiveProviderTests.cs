using AIUsageDock.Providers;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class LiveProviderTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task CodexProviderCanReadInstalledCli()
    {
        if (Environment.GetEnvironmentVariable("AI_USAGE_DOCK_LIVE_TESTS") != "1")
        {
            return;
        }

        using var provider = new CodexUsageProvider();
        var result = await provider.GetAsync(force: true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotEmpty(result.Snapshot!.Windows);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task ClaudeProviderCanReadInstalledCli()
    {
        if (Environment.GetEnvironmentVariable("AI_USAGE_DOCK_LIVE_TESTS") != "1")
        {
            return;
        }

        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")));

        using var provider = new ClaudeUsageProvider();
        var result = await provider.GetAsync(force: true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotEmpty(result.Snapshot!.Windows);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task AntigravityProviderCanReadInstalledCliWithoutModelTurns()
    {
        if (Environment.GetEnvironmentVariable("AI_USAGE_DOCK_LIVE_TESTS") != "1")
        {
            return;
        }

        using var provider = new AntigravityUsageProvider();
        var result = await provider.GetAsync(force: true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotEmpty(result.Snapshot!.Windows);
    }
}
