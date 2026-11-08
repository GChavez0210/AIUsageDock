using AIUsageDock.Bands;
using AIUsageDock.Models;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class DockChoiceTests
{
    private static readonly UsageWindow Session = new(UsageWindowKind.Session, "Session", 20, null, 300);
    private static readonly UsageWindow WeekAll = new(UsageWindowKind.Weekly, "Week (all models)", 71, null, 10080);
    private static readonly UsageWindow WeekFable = new(UsageWindowKind.Weekly, "Week (Fable only)", 83, null, 10080);

    private static UsageSnapshot Snapshot(params UsageWindow[] windows) =>
        new("Claude", null, windows, DateTimeOffset.UnixEpoch, "test");

    [Fact]
    public void AutoPrefersSessionThenTightest()
    {
        Assert.Same(Session, DockChoice.Pick(Snapshot(Session, WeekAll, WeekFable), DockChoice.Auto));
        Assert.Same(WeekFable, DockChoice.Pick(Snapshot(WeekAll, WeekFable), DockChoice.Auto));
    }

    [Fact]
    public void TightestIgnoresSession()
    {
        Assert.Same(WeekFable, DockChoice.Pick(Snapshot(Session, WeekAll, WeekFable), DockChoice.Tightest));
    }

    [Fact]
    public void ChosenWindowIsShownEvenWhenNotTightest()
    {
        Assert.Same(WeekAll, DockChoice.Pick(Snapshot(Session, WeekAll, WeekFable), DockChoice.ValueFor(WeekAll)));
    }

    [Fact]
    public void SameLabelDifferentPeriodIsDistinguished()
    {
        var fiveHour = new UsageWindow(UsageWindowKind.Session, "Gemini Pro", 10, null, 300);
        var weekly = new UsageWindow(UsageWindowKind.Weekly, "Gemini Pro", 50, null, 10080);

        Assert.NotEqual(DockChoice.ValueFor(fiveHour), DockChoice.ValueFor(weekly));
        Assert.Same(weekly, DockChoice.Pick(Snapshot(fiveHour, weekly), DockChoice.ValueFor(weekly)));
        Assert.Equal("Gemini Pro · 7d", DockChoice.TitleFor(weekly));
    }

    [Fact]
    public void MissingChosenWindowFallsBackToAuto()
    {
        var gone = DockChoice.ValueFor(new UsageWindow(UsageWindowKind.Weekly, "Week (Opus)", 0, null, 10080));

        Assert.Same(Session, DockChoice.Pick(Snapshot(Session, WeekAll), gone));
        Assert.Same(Session, DockChoice.Pick(Snapshot(Session, WeekAll), "garbage"));
        Assert.Equal("Week (Opus) · 7d", DockChoice.TitleForValue(gone));
    }
}
