using System.Text.Json.Nodes;
using AIUsageDock.Models;
using AIUsageDock.Pages;
using AIUsageDock.Status;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class UsageCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 23, 16, 38, TimeSpan.Zero);

    [Fact]
    public void RendersHeaderBarsPaceFactsAndFooter()
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
            Details:
            [
                new UsageDetail("Plan", "Claude Pro"),
                new UsageDetail("Account", "Sam Example · sam@example.com"),
                new UsageDetail("Extra usage", "$0.00 of $50.00 monthly cap"),
            ]);

        var body = Body(UsageCard.Build(UsageResult.Success(snapshot), "Claude", "claude", Now));
        var texts = AllText(body);

        // The tray panel header: name and email, then the age of the reading and the plan.
        Assert.Equal(["Claude", "sam@example.com", "Updated 2 minutes ago · Claude usage cache", "Claude Pro"], texts.Take(4));

        Assert.Contains("Session · 5h", texts);
        Assert.Contains("100% left", texts);
        Assert.Contains("Resets in 3h 52m", texts);
        Assert.Contains("Resets in 2d 2h", texts);
        Assert.Contains("48% in reserve", texts);
        Assert.Contains("Lasts until reset", texts);
        Assert.DoesNotContain("normal", texts);

        // Session sorts before the week regardless of label order.
        Assert.True(texts.IndexOf("Session · 5h") < texts.IndexOf("Week (all models) · 7d"));

        // A full session is a single green column; the week splits 78 / 22.
        var bars = Bars(body);
        Assert.Equal(2, bars.Count);
        
        Assert.Equal("100% good", bars[0]);
        Assert.Equal("78% good", bars[1]);

        // Details are small label/value rows. The plan is already in the header; an
        // account row keeps the name without the address.
        Assert.DoesNotContain("Plan", texts);
        Assert.Equal("Sam Example", texts[texts.IndexOf("Account") + 1]);
        Assert.Equal("$0.00 of $50.00 monthly cap", texts[texts.IndexOf("Extra usage") + 1]);
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
        Assert.Contains("Running low · 8% in deficit", texts);
        Assert.Contains("Projected empty in 33m", texts);
        Assert.Contains("Locked: org_policy", texts);
        Assert.Contains("Resets in 1h", texts);
        Assert.Contains("Resets in 1d", texts);
        Assert.Contains("Codex app-server timed out", texts);

        var bars = Bars(body);
        Assert.Equal("12% warning", bars[0]);
        Assert.Equal("0% attention", bars[1]);

        var stale = body.OfType<JsonObject>().First(item => item["type"]!.GetValue<string>() == "Container");
        Assert.Equal("Warning", stale["style"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(60, "good", "Good", null)]
    [InlineData(74.4, "good", "Good", null)]
    [InlineData(75, "warning", "Warning", "Running low")]
    [InlineData(89.4, "warning", "Warning", "Running low")]
    [InlineData(90, "attention", "Attention", "Almost out")]
    public void ColorsBarsByUsageTier(double used, string style, string color, string? flag)
    {
        var body = Body(UsageCard.Build(UsageResult.Success(Session(used)), "Codex", "codex", Now));

        // Same math as UsageCard.AppendWindow: round the remaining share, the empty side is the rest.
        var filled = (int)Math.Round(100d - used);
        var remaining = filled.ToString();

        Assert.Equal($"{remaining}% {style}", Bars(body)[0]);

        var percent = AllBlocks(body).Single(block => block["text"]?.GetValue<string>() == $"{remaining}% left");
        Assert.Equal(color, percent["color"]!.GetValue<string>());

        var texts = AllText(body);
        if (flag is null)
        {
            Assert.DoesNotContain(texts, text => text.Contains("Running low", StringComparison.Ordinal) || text.Contains("Almost out", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(texts, text => text.StartsWith(flag, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ThresholdsFollowTheSettings()
    {
        var options = new DisplayOptions(HighUsedPercent: 50, CriticalUsedPercent: 80);

        Assert.Equal("40% warning", Bars(Body(UsageCard.Build(UsageResult.Success(Session(60)), "Codex", "codex", Now, options)))[0]);
        Assert.Equal("20% attention", Bars(Body(UsageCard.Build(UsageResult.Success(Session(80)), "Codex", "codex", Now, options)))[0]);
    }

    [Fact]
    public void ShowUsedFillsTheBarWithWhatIsUsed()
    {
        var body = Body(UsageCard.Build(UsageResult.Success(Session(72)), "Codex", "codex", Now, new DisplayOptions(ShowUsed: true)));

        Assert.Contains("72% used", AllText(body));
        Assert.Equal("72% good", Bars(body)[0]);
    }

    [Fact]
    public void ClockTimeResetsAndNoPaceWhenSwitchedOff()
    {
        var body = Body(UsageCard.Build(
            UsageResult.Success(Session(72)), "Codex", "codex", Now,
            new DisplayOptions(RelativeResets: false, ShowPace: false)));
        var texts = AllText(body);

        Assert.Contains(texts, text => text.StartsWith("Resets ", StringComparison.Ordinal) && !text.StartsWith("Resets in", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, text => text.Contains("reserve", StringComparison.Ordinal) || text.Contains("deficit", StringComparison.Ordinal) || text == "On pace");
    }

    [Fact]
    public void HidePersonalInformationDropsAccountAndEmails()
    {
        var snapshot = new UsageSnapshot(
            "Claude",
            "Claude Team",
            [new UsageWindow(UsageWindowKind.Session, "Session", 10, Now.AddHours(1), 300)],
            Now,
            "Claude usage cache",
            Details:
            [
                new UsageDetail("Plan", "Claude Team"),
                new UsageDetail("Account", "Sam Example · sam@example.com"),
                new UsageDetail("Organization", "Example Org (admin)"),
            ],
            Notes: ["Billed to billing@example.com"]);

        var all = string.Join("\n", AllText(Body(UsageCard.Build(
            UsageResult.Success(snapshot), "Claude", "claude", Now, new DisplayOptions(HidePersonalInfo: true)))));

        Assert.DoesNotContain("example.com", all);
        Assert.DoesNotContain("Sam Example", all);
        Assert.Contains("Billed to •••", all);
        Assert.DoesNotContain("Organization", all);
    }

    [Fact]
    public void IncidentIsNamedUnderTheHeader()
    {
        var status = new ProviderStatus(StatusIndicator.Major, "Elevated error rates", null);

        var body = Body(UsageCard.Build(UsageResult.Success(Session(10)), "Codex", "codex", Now, status: status));
        var calm = Body(UsageCard.Build(
            UsageResult.Success(Session(10)), "Codex", "codex", Now,
            status: new ProviderStatus(StatusIndicator.Operational, "All Systems Operational", null)));

        // Third row, directly under the two header lines.
        var note = Assert.IsType<JsonObject>(body[2]);
        Assert.Equal("Attention", note["color"]!.GetValue<string>());
        Assert.Contains("Codex status · Major incident: Elevated error rates", AllText(body));
        Assert.DoesNotContain(AllText(calm), text => text.Contains("status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OverviewStacksOneCardPerProvider()
    {
        var entries = new[]
        {
            new OverviewEntry("Claude", "claude", UsageResult.Success(Session(72, "Claude", "Pro"))),
            new OverviewEntry("Codex", "codex", UsageResult.Failure(UsageFailureReason.NotSignedIn, "auth required"),
                new ProviderStatus(StatusIndicator.Minor, "Degraded performance", null)),
            new OverviewEntry("Ollama", "ollama", null),
        };

        var body = Body(UsageCard.BuildOverview(entries, Now, notices: [("Refreshing…", "Default")]));
        var texts = AllText(body);

        Assert.Equal("AI usage", texts[0]);
        Assert.Equal("Refreshing…", texts[1]);
        Assert.Equal(3, body.OfType<JsonObject>().Count(item => item["separator"]?.GetValue<bool>() == true));

        Assert.Contains("Claude", texts);
        Assert.Contains("Pro", texts);
        Assert.Contains("28% left", texts);
        Assert.Contains("Codex is not signed in", texts);
        Assert.Contains("auth required", texts);
        Assert.Contains("Status · Minor incident: Degraded performance", texts);
        Assert.Contains("Not fetched yet", texts);

        // The overview is compact: no account rows and no source.
        Assert.DoesNotContain(texts, text => text.Contains("test source", StringComparison.Ordinal));
    }

    [Fact]
    public void OverviewWithNothingEnabledSaysSo()
    {
        var texts = AllText(Body(UsageCard.BuildOverview([], Now)));

        Assert.Contains(texts, text => text.StartsWith("No providers are turned on.", StringComparison.Ordinal));
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
    [InlineData(null, null)]
    [InlineData(300, "5h")]
    [InlineData(90, "1h 30m")]
    [InlineData(10080, "7d")]
    public void DescribesWindowDurations(int? minutes, string? expected)
    {
        Assert.Equal(expected, UsageCard.DescribeDuration(minutes));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(80)]
    [InlineData(95)]
    public void AccentModeUsesAccentForEveryBar(double used)
    {
        var body = Body(UsageCard.Build(
            UsageResult.Success(Session(used)), "Codex", "codex", Now, new DisplayOptions(BarColors: BarColorMode.Accent)));

        var filled = (int)Math.Round(100d - used);
        Assert.Equal($"{filled}% accent", Bars(body)[0]);
    }

    private static UsageSnapshot Session(double used, string provider = "Codex", string? plan = null) => new(
        provider,
        plan,
        [new UsageWindow(UsageWindowKind.Session, "Session", used, Now.AddHours(1), 300)],
        Now,
        "test source");

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

    /// <summary>Each bar as "fill% colour". Bars are images; the alt text names what is drawn.</summary>
    private static List<string> Bars(JsonArray body) => body
        .OfType<JsonObject>()
        .Where(item => item["type"]!.GetValue<string>() == "Image")
        .Select(image =>
        {
            Assert.StartsWith("data:image/png;base64,", image["url"]!.GetValue<string>());
            return image["altText"]!.GetValue<string>();
        })
        .ToList();

    [Fact]
    public void BarImageIsAPillWithFillTrackAndPaceTick()
    {
        const uint green = 0x30A46C;
        var pixels = BarImage.Pixels(50, green, pacePercent: 75);

        (byte R, byte G, byte B, byte A) At(int x, int y)
        {
            var at = (y * BarImage.Width + x) * 4;
            return (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]);
        }

        var middle = BarImage.Height / 2;
        Assert.Equal((0x30, 0xA4, 0x6C, 255), At(BarImage.Width / 4, middle));

        // Past the fill only the translucent track remains.
        var track = At(BarImage.Width * 9 / 10, middle);
        Assert.InRange(track.A, 1, 128);

        // The tick is opaque-ish and dark on the empty track.
        var tick = At(BarImage.Width * 3 / 4, middle);
        Assert.True(tick.A > track.A);
        Assert.Equal(96, tick.R);

        // Corners are cut away to round the ends.
        Assert.Equal(0, At(0, 0).A);

        // An empty bar has no fill colour anywhere.
        Assert.NotEqual(0x30, BarImage.Pixels(0, green, null)[(middle * BarImage.Width + 3) * 4]);

        // PNG signature, and the same bar is encoded once.
        var uri = BarImage.DataUri(50, green, 75);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], Convert.FromBase64String(uri["data:image/png;base64,".Length..]).Take(4).Select(value => (int)value));
        Assert.Same(uri, BarImage.DataUri(50, green, 75));
    }
}
