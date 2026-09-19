using AIUsageDock.Providers;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class ProcessHelpersTests
{
    [Fact]
    public void EnvironmentVariablesAreAddedOnTopOfTheInheritedOnes()
    {
        var startInfo = ProcessHelpers.CreateStartInfo(
            @"C:\tools\agy.exe",
            ["-p", "/usage"],
            new Dictionary<string, string> { ["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true" });

        Assert.Equal("true", startInfo.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
        Assert.True(startInfo.Environment.ContainsKey("PATH"));
        Assert.Equal(["-p", "/usage"], startInfo.ArgumentList);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void ProvidersSwitchOffCliSelfUpdaters()
    {
        // Both updaters spawn a detached copy of the CLI that asks Windows for its own
        // console, which the terminal host then draws as a window on every refresh.
        Assert.Equal("true", AntigravityUsageProvider.CliEnvironment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
        Assert.Equal("1", ClaudeUsageProvider.CliEnvironment["DISABLE_AUTOUPDATER"]);
    }
}
