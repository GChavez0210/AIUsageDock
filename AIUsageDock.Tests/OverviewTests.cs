using System.Text.RegularExpressions;
using AIUsageDock.Models;
using AIUsageDock.Notifications;
using AIUsageDock.Pages;
using AIUsageDock.Status;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class OverviewTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "AIUsageDock.Tests", Guid.NewGuid() + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task OverviewBandNamesTheTightestProviderFirst()
    {
        var providers = Providers();
        using var commandProvider = new AIUsageDockCommandsProvider(new ProviderSelectionSettings(_path), startTimer: false, providers);

        await commandProvider.RefreshAsync(force: true);

        var bands = commandProvider.GetDockBands()!;
        Assert.Equal(["Claude 28%", "Codex 90%", "Ollama —"], bands.Take(3).Select(ItemOf).Select(item => item.Title));
        Assert.Equal("wk 55% · 1h", ItemOf(bands[0]).Subtitle);

        // The overview band comes last and only repeats what the others read.
        var overview = ItemOf(bands[3]);
        Assert.Equal("Claude 28%", overview.Title);
        Assert.Equal("Codex 90%", overview.Subtitle);
        Assert.All(providers, provider => Assert.Equal(1, provider.Calls));
    }

    [Fact]
    public async Task ShowUsedFlipsBandsWithoutReadingAgain()
    {
        var selection = new ProviderSelectionSettings(_path);
        var providers = Providers();
        using var commandProvider = new AIUsageDockCommandsProvider(selection, startTimer: false, providers);
        await commandProvider.RefreshAsync(force: true);

        var form = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
        form.SubmitForm("{\"" + ProviderSelectionSettings.UsageShownSetting + "\":\"used\"}", "{}");
        await commandProvider.RefreshAsync(force: false);

        var bands = commandProvider.GetDockBands()!;
        Assert.Equal("Claude 72%", ItemOf(bands[0]).Title);
        Assert.Equal("used · wk 45% · 1h", ItemOf(bands[0]).Subtitle);
        Assert.Equal("Claude 72%", ItemOf(bands[3]).Title);
        Assert.Equal("used · Codex 10%", ItemOf(bands[3]).Subtitle);
        Assert.True(selection.Display.ShowUsed);
    }

    [Fact]
    public async Task OverviewPageShowsEveryEnabledProvider()
    {
        using var commandProvider = new AIUsageDockCommandsProvider(new ProviderSelectionSettings(_path), startTimer: false, Providers());
        await commandProvider.RefreshAsync(force: true);

        var command = commandProvider.TopLevelCommands()[0];
        Assert.Equal("AI usage overview", command.Title);
        var page = Assert.IsType<OverviewPage>(command.Command);
        var content = Assert.IsAssignableFrom<IFormContent>(Assert.Single(page.GetContent()));
        if (page.PendingRefresh is { } pending)
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var text = Regex.Unescape(page.BuildCard());
        Assert.Contains("28% left", text);
        Assert.Contains("90% left", text);
        Assert.Contains("Ollama API key missing or rejected", text);
        Assert.DoesNotContain("Refreshing…", text);
        Assert.NotNull(content.TemplateJson);
    }

    [Fact]
    public async Task IncidentAndQuotaWarningReachTheBandAndTheToast()
    {
        var sink = new RecordingSink();
        using var poller = new StatusPoller((_, _) => Task.FromResult("""{"status":{"indicator":"major","description":"Outage"}}"""));
        using var commandProvider = new AIUsageDockCommandsProvider(
            new ProviderSelectionSettings(_path), startTimer: false, Providers(claudeUsed: 93), poller, sink);

        await commandProvider.RefreshAsync(force: true);

        var bands = commandProvider.GetDockBands()!;
        Assert.Equal("wk 55% · 1h · incident", ItemOf(bands[0]).Subtitle);
        Assert.Equal("Claude · Session is almost out", Assert.Single(sink.Shown).Title);
    }

    private static IListItem ItemOf(ICommandItem band) =>
        Assert.IsAssignableFrom<IListPage>(band.Command).GetItems()[0];

    private static FakeProvider[] Providers(double claudeUsed = 72)
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new FakeProvider("claude", "Claude", UsageResult.Success(new UsageSnapshot(
                "Claude",
                "Pro",
                [
                    new UsageWindow(UsageWindowKind.Session, "Session", claudeUsed, now.AddMinutes(59).AddSeconds(30), 300),
                    new UsageWindow(UsageWindowKind.Weekly, "Week", 45, now.AddDays(3), 10080),
                ],
                now,
                "test"))),
            new FakeProvider("codex", "Codex", UsageResult.Success(new UsageSnapshot(
                "Codex",
                null,
                [new UsageWindow(UsageWindowKind.Weekly, "Week", 10, null, 10080)],
                now,
                "test"))),
            new FakeProvider("ollama", "Ollama", UsageResult.Failure(UsageFailureReason.MissingApiKey, "API key not set")),
        ];
    }

    private sealed class FakeProvider(string id, string name, UsageResult result) : IUsageProvider
    {
        public string Id => id;
        public string DisplayName => name;
        public string IconName => id;
        public int Calls { get; private set; }
        public UsageResult? LastResult => Calls == 0 ? null : result;

        public Task<UsageResult> GetAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            // Like the real providers, a cached reading answers a non-forced read.
            if (force || Calls == 0)
            {
                Calls++;
            }

            return Task.FromResult(result);
        }

        public void Dispose() { }
    }

    private sealed class RecordingSink : IToastSink
    {
        public List<(string Title, string Body)> Shown { get; } = [];

        public void Show(string title, string body) => Shown.Add((title, body));
    }
}
