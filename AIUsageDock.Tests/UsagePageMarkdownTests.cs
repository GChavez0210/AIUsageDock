using AIUsageDock.Models;
using AIUsageDock.Pages;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class UsagePageMarkdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 23, 16, 38, TimeSpan.Zero);

    [Fact]
    public void RendersPlanWindowsAccountAndFooter()
    {
        var snapshot = new UsageSnapshot(
            "Claude",
            "Claude Pro",
            [
                new UsageWindow(UsageWindowKind.Weekly, "Week (all models)", 22, Now.AddDays(2).AddHours(2), Severity: "normal"),
                new UsageWindow(UsageWindowKind.Session, "Session", 0, Now.AddHours(3).AddMinutes(52)),
            ],
            Now.AddMinutes(-2),
            "Claude usage cache",
            Details: [new UsageDetail("Plan", "Claude Pro"), new UsageDetail("Extra usage", "$0.00 of $50.00 monthly cap")]);

        var markdown = UsagePage.BuildMarkdown(UsageResult.Success(snapshot), "Claude", "claude", Now);

        Assert.StartsWith("# Claude · Claude Pro", markdown, StringComparison.Ordinal);
        Assert.Contains("### Session", markdown, StringComparison.Ordinal);
        Assert.Contains("**100%** left", markdown, StringComparison.Ordinal);
        Assert.Contains("Resets in 3h 52m", markdown, StringComparison.Ordinal);
        Assert.Contains("Resets in 2d 2h", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Extra usage:** $0.00 of $50.00 monthly cap", markdown, StringComparison.Ordinal);
        Assert.Contains("updated 2 min ago", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("normal", markdown, StringComparison.Ordinal);

        // Session sorts before the week regardless of label order.
        Assert.True(markdown.IndexOf("### Session", StringComparison.Ordinal) < markdown.IndexOf("### Week", StringComparison.Ordinal));
    }

    [Fact]
    public void FlagsLowLockedAndStaleReadings()
    {
        var snapshot = new UsageSnapshot(
            "Codex",
            "ChatGPT Plus",
            [
                new UsageWindow(UsageWindowKind.Session, "Session", 88, Now.AddHours(1), 300),
                new UsageWindow(UsageWindowKind.Weekly, "Week", 100, Now.AddDays(1), 10080, LockedReason: "org_policy"),
            ],
            Now.AddHours(-3),
            "codex app-server",
            IsStale: true);

        var markdown = UsagePage.BuildMarkdown(UsageResult.Success(snapshot, "Codex app-server timed out"), "Codex", "codex", Now);

        Assert.Contains("last good reading from 3 h ago", markdown, StringComparison.Ordinal);
        Assert.Contains("### Session · 5h window", markdown, StringComparison.Ordinal);
        Assert.Contains("**12%** left · ⚠ running low", markdown, StringComparison.Ordinal);
        Assert.Contains("### Week · 7d window", markdown, StringComparison.Ordinal);
        Assert.Contains("🔒 locked: org_policy", markdown, StringComparison.Ordinal);
        Assert.Contains("> Codex app-server timed out", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CliUnavailable", "# Codex CLI not found", "`codex` is on your PATH")]
    [InlineData("NotSignedIn", "# Codex is not signed in", "`codex login`")]
    [InlineData("TimedOut", "# Codex did not respond", "Refresh AI usage")]
    [InlineData("InvalidData", "# Codex reported no usage data", "/usage")]
    public void FailureScreensExplainWhatToDo(string reasonName, string heading, string hint)
    {
        var reason = Enum.Parse<UsageFailureReason>(reasonName);
        var markdown = UsagePage.BuildMarkdown(UsageResult.Failure(reason, "raw error"), "Codex", "codex", Now);

        Assert.StartsWith(heading, markdown, StringComparison.Ordinal);
        Assert.Contains(hint, markdown, StringComparison.Ordinal);
        Assert.Contains("> raw error", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, "30s", "under a minute")]
    [InlineData(45 * 60, "45m", "45m")]
    [InlineData(3 * 3600 + 52 * 60, "3h52m", "3h 52m")]
    [InlineData(2 * 86400 + 2 * 3600, "2d2h", "2d 2h")]
    public void DescribesCountdownsCompactly(int seconds, string _, string expected)
    {
        Assert.Equal(expected, UsagePage.DescribeSpan(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(300, "5h")]
    [InlineData(90, "1h 30m")]
    [InlineData(10080, "7d")]
    public void DescribesWindowDurations(int? minutes, string? expected)
    {
        Assert.Equal(expected, UsagePage.DescribeDuration(minutes));
    }
}
