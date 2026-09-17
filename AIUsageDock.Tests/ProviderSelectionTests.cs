using AIUsageDock.Models;
using AIUsageDock.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class ProviderSelectionTests
{
    [Fact]
    public async Task SelectionPersistsAndSkipsDisabledProviders()
    {
        var path = Path.Combine(Path.GetTempPath(), "AIUsageDock.Tests", Guid.NewGuid() + ".json");
        try
        {
            var selection = new ProviderSelectionSettings(path);
            var providers = new[]
            {
                new FakeUsageProvider("claude"),
                new FakeUsageProvider("codex"),
                new FakeUsageProvider("antigravity"),
            };
            using (var commandProvider = new AIUsageDockCommandsProvider(selection, startTimer: false, providers))
            {
                Assert.NotNull(commandProvider.Settings);
                Assert.Equal(3, commandProvider.GetDockBands()!.Length);
                Assert.Equal(6, commandProvider.TopLevelCommands().Length);

                var form = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
                form.SubmitForm("""{"claude":"false","codex":"true","antigravity":"false"}""", "{}");

                Assert.Single(commandProvider.GetDockBands()!);
                Assert.Equal(4, commandProvider.TopLevelCommands().Length);
                Assert.Contains(commandProvider.TopLevelCommands(), item => item.Title == "Select subscriptions");
                Assert.Contains(commandProvider.TopLevelCommands(), item => item.Title == "About AI Usage Dock");

                await commandProvider.RefreshAsync(force: true);
                Assert.Equal(0, providers[0].Calls);
                Assert.Equal(1, providers[1].Calls);
                Assert.Equal(0, providers[2].Calls);

                var secondForm = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
                secondForm.SubmitForm("""{"claude":"false","codex":"false","antigravity":"false"}""", "{}");
                Assert.Empty(commandProvider.GetDockBands()!);
                Assert.Equal(3, commandProvider.TopLevelCommands().Length);
                await commandProvider.RefreshAsync(force: true);
                Assert.Equal(1, providers[1].Calls);
            }

            var reloaded = new ProviderSelectionSettings(path);
            Assert.False(reloaded.IsEnabled("claude"));
            Assert.False(reloaded.IsEnabled("codex"));
            Assert.False(reloaded.IsEnabled("antigravity"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AboutPageCreditsTheAuthor()
    {
        var content = Assert.Single(new AboutPage().GetContent());
        var markdown = Assert.IsType<MarkdownContent>(content);
        Assert.Contains(AboutPage.AboutText, markdown.Body);
    }

    private sealed class FakeUsageProvider(string id) : IUsageProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public string IconName => id;
        public int Calls { get; private set; }

        public Task<UsageResult> GetAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(UsageResult.Failure(UsageFailureReason.CliUnavailable, "Not installed"));
        }

        public void Dispose() { }
    }
}
