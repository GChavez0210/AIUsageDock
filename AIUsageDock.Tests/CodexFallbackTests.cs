using AIUsageDock.Providers;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class CodexFallbackTests
{
    [Fact]
    public async Task FailedNativeQueryRetriesThroughShim()
    {
        var calls = new List<string>();
        var result = await CodexUsageProvider.ReadWithShimFallbackAsync("codex.exe", "codex.cmd", path =>
        {
            calls.Add(path);
            if (path == "codex.exe")
            {
                throw new InvalidOperationException("app-server closed before response");
            }

            return Task.FromResult("rate limits");
        });

        Assert.Equal("rate limits", result);
        Assert.Equal(["codex.exe", "codex.cmd"], calls);
    }

    [Theory]
    [InlineData("codex account authentication required")]
    [InlineData("authentication failed")]
    public async Task AuthenticationFailureDoesNotLaunchShim(string message)
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexUsageProvider.ReadWithShimFallbackAsync<string>("codex.exe", "codex.cmd", path =>
            {
                calls.Add(path);
                throw new InvalidOperationException(message);
            }));

        Assert.Equal(["codex.exe"], calls);
    }

    [Fact]
    public async Task CancellationDoesNotLaunchShim()
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CodexUsageProvider.ReadWithShimFallbackAsync<string>("codex.exe", "codex.cmd", path =>
            {
                calls.Add(path);
                throw new OperationCanceledException();
            }));

        Assert.Equal(["codex.exe"], calls);
    }
}
