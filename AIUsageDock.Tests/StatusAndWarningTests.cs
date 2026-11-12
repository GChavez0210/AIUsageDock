using AIUsageDock.Models;
using AIUsageDock.Notifications;
using AIUsageDock.Pages;
using AIUsageDock.Status;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class StatusAndWarningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StatuspageFeedKeepsIndicatorDescriptionAndDate()
    {
        var status = ProviderStatus.Parse("""
            {
              "page": { "id": "x", "name": "Claude", "url": "https://status.claude.com", "updated_at": "2026-10-10T08:15:00Z" },
              "status": { "indicator": "major", "description": "Partial System Outage" }
            }
            """)!;

        Assert.Equal(StatusIndicator.Major, status.Indicator);
        Assert.True(status.HasIssue);
        Assert.Equal("Major incident: Partial System Outage", status.Describe());
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 8, 15, 0, TimeSpan.Zero), status.UpdatedAt);
    }

    [Theory]
    [InlineData("none", "Operational")]
    [InlineData("minor", "Minor")]
    [InlineData("major", "Major")]
    [InlineData("critical", "Critical")]
    [InlineData("maintenance", "Maintenance")]
    [InlineData("something-new", "Unknown")]
    public void RawIndicatorsMapLikeStatuspage(string raw, string expected)
    {
        Assert.Equal(Enum.Parse<StatusIndicator>(expected), ProviderStatus.IndicatorFrom(raw));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"status":{}}""")]
    public void BodiesThatAreNotAStatusFeedAreIgnored(string body)
    {
        Assert.Null(ProviderStatus.Parse(body));
    }

    [Fact]
    public async Task PollerReadsOnlyProvidersWithAPageAndThrottles()
    {
        var requested = new List<string>();
        var indicator = "none";
        var clock = Now;
        using var poller = new StatusPoller(
            (url, _) =>
            {
                requested.Add(url);
                return Task.FromResult("{\"status\":{\"indicator\":\"" + indicator + "\",\"description\":\"text\"}}");
            },
            () => clock);

        Assert.True(await poller.PollAsync(["claude", "codex", "ollama", "antigravity"]));
        Assert.Equal(
            ["https://status.claude.com/api/v2/status.json", "https://status.openai.com/api/v2/status.json"],
            requested.Order());
        Assert.False(poller.For("claude")!.HasIssue);
        Assert.Null(poller.For("ollama"));

        // Inside five minutes nothing is requested again, unless forced.
        indicator = "minor";
        clock = Now.AddMinutes(2);
        Assert.False(await poller.PollAsync(["claude"]));
        Assert.Equal(2, requested.Count);
        Assert.True(await poller.PollAsync(["claude"], force: true));
        Assert.True(poller.For("claude")!.HasIssue);

        // The same reading again is not a change.
        clock = Now.AddMinutes(10);
        Assert.False(await poller.PollAsync(["claude"]));
    }

    [Fact]
    public async Task UnreachableStatusPageKeepsTheLastReading()
    {
        var fail = false;
        var clock = Now;
        using var poller = new StatusPoller(
            (_, _) => fail
                ? Task.FromException<string>(new HttpRequestException("offline"))
                : Task.FromResult("""{"status":{"indicator":"minor"}}"""),
            () => clock);

        await poller.PollAsync(["codex"]);
        fail = true;
        clock = Now.AddMinutes(10);

        Assert.False(await poller.PollAsync(["codex"]));
        Assert.Equal(StatusIndicator.Minor, poller.For("codex")!.Indicator);
    }

    [Fact]
    public void WarnsOncePerLevelAndAgainAfterAReset()
    {
        var sink = new RecordingSink();
        var notifier = new QuotaNotifier(sink);
        var options = DisplayOptions.Default;

        void Observe(double used) => notifier.Observe("Claude", "claude", Session(used), options, Now);

        Observe(40);
        Assert.Empty(sink.Shown);

        Observe(80);
        Observe(82);
        Assert.Equal(("Claude · Session is running low", "20% left · Resets in 1h"), Assert.Single(sink.Shown));

        Observe(93);
        Assert.Equal("Claude · Session is almost out", sink.Shown[1].Title);

        Observe(100);
        Assert.Equal("Claude · Session is used up", sink.Shown[2].Title);

        // The window resets, then climbs again.
        Observe(5);
        Observe(78);
        Assert.Equal(4, sink.Shown.Count);
        Assert.Equal("Claude · Session is running low", sink.Shown[3].Title);
    }

    [Fact]
    public void StaleAndLockedWindowsDoNotWarn()
    {
        var sink = new RecordingSink();
        var notifier = new QuotaNotifier(sink);

        notifier.Observe("Claude", "claude", Session(95) with { IsStale = true }, DisplayOptions.Default, Now);
        notifier.Observe(
            "Codex",
            "codex",
            new UsageSnapshot("Codex", null, [new UsageWindow(UsageWindowKind.Weekly, "Week", 100, Now.AddDays(1), 10080, LockedReason: "policy")], Now, "test"),
            DisplayOptions.Default,
            Now);

        Assert.Empty(sink.Shown);
    }

    [Fact]
    public void WarningTextFollowsShowUsed()
    {
        var sink = new RecordingSink();

        new QuotaNotifier(sink).Observe("Claude", "claude", Session(80), new DisplayOptions(ShowUsed: true), Now);

        Assert.Equal("80% used · Resets in 1h", Assert.Single(sink.Shown).Body);
    }

    private static UsageSnapshot Session(double used) => new(
        "Claude",
        null,
        [new UsageWindow(UsageWindowKind.Session, "Session", used, Now.AddHours(1), 300)],
        Now,
        "test");

    private sealed class RecordingSink : IToastSink
    {
        public List<(string Title, string Body)> Shown { get; } = [];

        public void Show(string title, string body) => Shown.Add((title, body));
    }
}
