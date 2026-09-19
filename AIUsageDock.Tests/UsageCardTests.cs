using System.Text.Json.Nodes;
using AIUsageDock.Models;
using AIUsageDock.Pages;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class UsageCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 23, 16, 38, TimeSpan.Zero);

    [Fact]
    public void RendersTitleBarsFactsAndFooter()
    {
        var snapshot = new UsageSnapshot(
            "Claude",
            "Claude Pro",
            [
                new UsageWindow(UsageWindowKind.Weekly, "Week (all models)", 22, Now.AddDays(2).AddHours(2), 10080, Severity: "normal"),
                new UsageWindow(UsageWindowKind.Session, "Session", 0, Now.AddHours(3).AddMinutes(52), 300),
            ],
            Now.AddMinutes(-2),
            "Claude usage cache",
            Details: [new UsageDetail("Plan", "Claude Pro"), new UsageDetail("Extra usage", "$0.00 of $50.00 monthly cap")]);

        var body = Body(UsageCard.Build(UsageResult.Success(snapshot), "Claude", "claude", Now));
        var texts = AllText(body);

        Assert.Equal("Claude · Claude Pro", texts[0]);
        Assert.Contains("Session · 5h window", texts);
        Assert.Contains("100% left", texts);
        Assert.Contains(texts, text => text.StartsWith("Resets in 3h 52m · ", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.StartsWith("Resets in 2d 2h · ", StringComparison.Ordinal));
        Assert.Contains($"Claude usage cache · updated 2 min ago ({Now.AddMinutes(-2).ToLocalTime():HH:mm:ss})", texts);
        Assert.DoesNotContain("normal", texts);

        // Session sorts before the week regardless of label order.
        Assert.True(texts.IndexOf("Session · 5h window") < texts.IndexOf("Week (all models) · 7d window"));

        // A full session is a single green column; the week splits 78 / 22.
        var bars = Bars(body);
        Assert.Equal(2, bars.Count);
        Assert.Single(bars[0]);
        Assert.Equal(("100", "good"), bars[0][0]);
        Assert.Equal([("78", "good"), ("22", "emphasis")], bars[1]);

        var facts = body.OfType<JsonObject>().Single(item => item["type"]!.GetValue<string>() == "FactSet")["facts"]!.AsArray();
        Assert.Equal("Plan", facts[0]!["title"]!.GetValue<string>());
        Assert.Equal("$0.00 of $50.00 monthly cap", facts[1]!["value"]!.GetValue<string>());
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

        var body = Body(UsageCard.Build(UsageResult.Success(snapshot, "Codex app-server timed out"), "Codex", "codex", Now));
        var texts = AllText(body);

        Assert.Contains("Showing the last good reading from 3 h ago. A live refresh did not succeed.", texts);
        Assert.Contains(texts, text => text.StartsWith("Running low · Resets in 1h · ", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.StartsWith("Locked: org_policy · Resets in 1d · ", StringComparison.Ordinal));
        Assert.Contains("Codex app-server timed out", texts);

        var bars = Bars(body);
        Assert.Equal([("12", "warning"), ("88", "emphasis")], bars[0]);
        Assert.Equal([("100", "emphasis")], bars[1]);

        var stale = body.OfType<JsonObject>().First(item => item["type"]!.GetValue<string>() == "Container");
        Assert.Equal("Warning", stale["style"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(60, "good", "Good", null)]
    [InlineData(76, "warning", "Warning", "Running low")]
    [InlineData(91, "attention", "Attention", "Almost out")]
    public void ColorsBarsByRemainingTier(double used, string style, string color, string? flag)
    {
        var snapshot = new UsageSnapshot(
            "Codex",
            "ChatGPT Plus",
            [new UsageWindow(UsageWindowKind.Session, "Session", used, Now.AddHours(1), 300)],
            Now,
            "codex app-server");

        var body = Body(UsageCard.Build(UsageResult.Success(snapshot), "Codex", "codex", Now));
        var remaining = (100 - (int)used).ToString();

        Assert.Equal([(remaining, style), (((int)used).ToString(), "emphasis")], Bars(body)[0]);

        var percent = AllBlocks(body).Single(block => block["text"]?.GetValue<string>() == $"{remaining}% left");
        Assert.Equal(color, percent["color"]!.GetValue<string>());

        var texts = AllText(body);
        if (flag is null)
        {
            Assert.DoesNotContain(texts, text => text.Contains("low", StringComparison.OrdinalIgnoreCase) || text.Contains("out", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Contains(texts, text => text.StartsWith($"{flag} · Resets in 1h · ", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("CliUnavailable", "Codex CLI not found", "`codex` is on your PATH")]
    [InlineData("NotSignedIn", "Codex is not signed in", "`codex login`")]
    [InlineData("TimedOut", "Codex did not respond", "Refresh AI usage")]
    [InlineData("InvalidData", "Codex reported no usage data", "/usage")]
    public void FailureCardsExplainWhatToDo(string reasonName, string heading, string hint)
    {
        var reason = Enum.Parse<UsageFailureReason>(reasonName);
        var texts = AllText(Body(UsageCard.Build(UsageResult.Failure(reason, "raw error"), "Codex", "codex", Now)));

        Assert.Equal(heading, texts[0]);
        Assert.Contains(hint, texts[1], StringComparison.Ordinal);
        Assert.Contains("raw error", texts);
    }

    [Theory]
    [InlineData(30, "under a minute")]
    [InlineData(45 * 60, "45m")]
    [InlineData(3 * 3600 + 52 * 60, "3h 52m")]
    [InlineData(2 * 86400 + 2 * 3600, "2d 2h")]
    public void DescribesCountdownsCompactly(int seconds, string expected)
    {
        Assert.Equal(expected, UsageCard.DescribeSpan(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(300, "5h")]
    [InlineData(90, "1h 30m")]
    [InlineData(10080, "7d")]
    public void DescribesWindowDurations(int? minutes, string? expected)
    {
        Assert.Equal(expected, UsageCard.DescribeDuration(minutes));
    }

    private static JsonArray Body(string json) => JsonNode.Parse(json)!["body"]!.AsArray();

    /// <summary>Every TextBlock's text, in document order, including nested ones.</summary>
    private static List<string> AllText(JsonNode node) =>
        AllBlocks(node).Select(block => block["text"]!.GetValue<string>()).ToList();

    /// <summary>Every TextBlock, in document order, including nested ones.</summary>
    private static List<JsonObject> AllBlocks(JsonNode node)
    {
        var blocks = new List<JsonObject>();
        Walk(node);
        return blocks;

        void Walk(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    if (obj["type"]?.GetValue<string>() == "TextBlock")
                    {
                        blocks.Add(obj);
                    }

                    foreach (var property in obj)
                    {
                        Walk(property.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    /// <summary>Each bar as its (width, style) segments. Bars are the ColumnSets whose columns carry a style.</summary>
    private static List<List<(string Width, string Style)>> Bars(JsonArray body) => body
        .OfType<JsonObject>()
        .Where(item => item["type"]!.GetValue<string>() == "ColumnSet")
        .Select(set => set["columns"]!.AsArray().OfType<JsonObject>().ToList())
        .Where(columns => columns.All(column => column["style"] is not null))
        .Select(columns => columns.Select(column => (column["width"]!.GetValue<string>(), column["style"]!.GetValue<string>())).ToList())
        .ToList();
}
