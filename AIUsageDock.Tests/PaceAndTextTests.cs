using AIUsageDock.Models;
using AIUsageDock.Pages;
using Xunit;

namespace AIUsageDock.Tests;

/// <summary>
/// Pace numbers follow Win-CodexBar's own tests (commands/bridge/pace.rs and
/// core/usage_pace.rs), so both apps describe the same window the same way.
/// </summary>
public sealed class PaceAndTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow Week(double used, int minutesUntilReset, int? duration = 10080) =>
        new(UsageWindowKind.Weekly, "Week", used, Now.AddMinutes(minutesUntilReset), duration);

    [Fact]
    public void ReserveIsANegativeDeltaThatLastsToReset()
    {
        var pace = UsagePace.For(Week(30, 5040), Now)!;

        Assert.Equal(PaceStage.FarBehind, pace.Stage);
        Assert.Equal(-20, pace.DeltaPercent, 6);
        Assert.Equal(50, pace.ExpectedUsedPercent, 6);
        Assert.Null(pace.EtaSeconds);
        Assert.True(pace.WillLastToReset);
        Assert.Equal(("20% in reserve", "Lasts until reset"), PanelText.Pace(Week(30, 5040), Now));
    }

    [Fact]
    public void DeficitIsAPositiveDeltaWithAnEta()
    {
        var pace = UsagePace.For(Week(55, 5040), Now)!;

        Assert.Equal(PaceStage.SlightlyAhead, pace.Stage);
        Assert.Equal(5, pace.DeltaPercent, 6);
        Assert.False(pace.WillLastToReset);
        Assert.Equal(247_418.18, pace.EtaSeconds!.Value, 2);
        Assert.Equal(("5% in deficit", "Runs out in 2d 20h"), PanelText.Pace(Week(55, 5040), Now));
    }

    [Theory]
    [InlineData(0, "OnTrack")]
    [InlineData(1.5, "OnTrack")]
    [InlineData(-1.5, "OnTrack")]
    [InlineData(4, "SlightlyAhead")]
    [InlineData(-4, "SlightlyBehind")]
    [InlineData(10, "Ahead")]
    [InlineData(-10, "Behind")]
    [InlineData(20, "FarAhead")]
    [InlineData(-20, "FarBehind")]
    public void StagesFollowTheDelta(double delta, string stage)
    {
        Assert.Equal(Enum.Parse<PaceStage>(stage), UsagePace.StageFor(delta));
    }

    [Fact]
    public void WindowsThatCannotBePacedHaveNone()
    {
        Assert.Null(UsagePace.For(new UsageWindow(UsageWindowKind.Weekly, "Week", 30, null, 10080), Now));
        Assert.Null(UsagePace.For(Week(30, 5040, duration: null), Now));
        Assert.Null(UsagePace.For(Week(30, -5), Now));

        // A reset further away than the window is long does not describe this window.
        Assert.Null(UsagePace.For(Week(30, 20000), Now));
        Assert.Null(UsagePace.For(new UsageWindow(UsageWindowKind.Weekly, "Week", 100, Now.AddDays(1), 10080, LockedReason: "policy"), Now));
    }

    [Fact]
    public void SessionPaceProjectsWhenItRunsEmpty()
    {
        var session = new UsageWindow(UsageWindowKind.Session, "Session", 60, Now.AddMinutes(150), 300);

        Assert.Equal(PaceStage.Ahead, UsagePace.For(session, Now)!.Stage);
        Assert.Equal(("10% in deficit", "Projected empty in 1h 40m"), PanelText.Pace(session, Now));
    }

    [Fact]
    public void PaceLineIsHiddenEarlyInAWindowAndWhenUsedUp()
    {
        // Two minutes into a session nothing can be said yet.
        Assert.Null(PanelText.Pace(new UsageWindow(UsageWindowKind.Session, "Session", 1, Now.AddMinutes(298), 300), Now));
        Assert.Null(PanelText.Pace(Week(100, 5040), Now));
        Assert.Equal(("On pace", "Lasts until reset"), PanelText.Pace(Week(49, 5040), Now));
    }

    [Theory]
    [InlineData(0.5, null)]
    [InlineData(30, "1m")]
    [InlineData(45 * 60, "45m")]
    [InlineData(3600, "1h")]
    [InlineData(3 * 3600 + 52 * 60, "3h 52m")]
    [InlineData(2 * 86400 + 2 * 3600, "2d 2h")]
    [InlineData(2 * 86400 + 5 * 60, "2d 5m")]
    [InlineData(11 * 86400, "11d")]
    public void CountdownsAreCompact(double seconds, string? expected)
    {
        Assert.Equal(expected, PanelText.Countdown(seconds));
    }

    [Fact]
    public void PercentagesFollowUsedOrLeft()
    {
        var window = new UsageWindow(UsageWindowKind.Session, "Session", 72.4, null);
        var barelyUsed = new UsageWindow(UsageWindowKind.Session, "Session", 0.4, null);

        Assert.Equal("72% used", PanelText.Percent(window, showUsed: true));
        Assert.Equal("28% left", PanelText.Percent(window, showUsed: false));
        Assert.Equal("<1% used", PanelText.Percent(barelyUsed, showUsed: true));
        Assert.Equal("100% left", PanelText.Percent(barelyUsed, showUsed: false));
    }

    [Fact]
    public void ResetsReadAsCountdownOrClockTime()
    {
        var soon = new UsageWindow(UsageWindowKind.Session, "Session", 10, Now.AddMinutes(83), 300);
        var past = new UsageWindow(UsageWindowKind.Session, "Session", 10, Now.AddMinutes(-1), 300);

        Assert.Equal("Resets in 1h 23m", PanelText.Reset(soon, Now, relative: true));
        Assert.Equal("Resets now", PanelText.Reset(past, Now, relative: true));
        Assert.Equal("Resets now", PanelText.Reset(past, Now, relative: false));
        Assert.Null(PanelText.Reset(new UsageWindow(UsageWindowKind.Session, "Session", 10, null), Now, relative: true));

        Assert.Equal("1h 23m", PanelText.BandReset(soon, Now, relative: true));
        Assert.Equal($"resets {Now.AddMinutes(83).ToLocalTime():HH:mm}", PanelText.BandReset(soon, Now, relative: false));
    }

    [Theory]
    [InlineData(20, "Updated just now")]
    [InlineData(60, "Updated 1 minute ago")]
    [InlineData(5 * 60, "Updated 5 minutes ago")]
    [InlineData(3600, "Updated 1 hour ago")]
    [InlineData(3 * 86400, "Updated 3 days ago")]
    public void ReadingAgeIsSpelledOut(int secondsAgo, string expected)
    {
        Assert.Equal(expected, PanelText.Updated(Now.AddSeconds(-secondsAgo), Now));
    }
}
