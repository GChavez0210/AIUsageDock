using System.Text.RegularExpressions;
using AIUsageDock.Models;
using AIUsageDock.Pages;
using Microsoft.CommandPalette.Extensions;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class UsagePageTests
{
    private static UsageResult Reading(double used) => UsageResult.Success(new UsageSnapshot(
        "Codex",
        null,
        [new UsageWindow(UsageWindowKind.Session, "Session", used, DateTimeOffset.UtcNow.AddHours(1), 300)],
        DateTimeOffset.UtcNow,
        "test"));

    [Fact]
    public async Task DrawsLastReadingAtOnceThenRedrawsWhenSlowRefreshLands()
    {
        var provider = new GatedProvider(Reading(40));
        var page = new UsagePage(provider);
        var redraws = 0;
        page.ItemsChanged += (_, _) => Interlocked.Increment(ref redraws);

        var content = Assert.IsAssignableFrom<IFormContent>(Assert.Single(page.GetContent()));
        var first = Regex.Unescape(content.TemplateJson);
        Assert.Contains("60% left", first);
        Assert.Contains("Refreshing…", first);

        provider.Complete(Reading(70));
        await page.PendingRefresh!.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitFor(() => redraws == 1);

        // The content already on screen is updated in place, without another fetch.
        var inPlace = Regex.Unescape(content.TemplateJson);
        Assert.Contains("30% left", inPlace);
        Assert.DoesNotContain("Refreshing…", inPlace);

        var second = Text(page);
        Assert.Contains("30% left", second);
        Assert.DoesNotContain("Refreshing…", second);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task FreshCacheNeedsNoRefreshNoticeOrRedraw()
    {
        var cached = Reading(10);
        var provider = new GatedProvider(cached);
        provider.Complete(cached);
        var page = new UsagePage(provider);
        var redraws = 0;
        page.ItemsChanged += (_, _) => Interlocked.Increment(ref redraws);

        var text = Text(page);

        Assert.Contains("90% left", text);
        Assert.DoesNotContain("Refreshing…", text);
        await Task.Delay(100);
        Assert.Equal(0, redraws);
    }

    [Fact]
    public async Task FailedRefreshKeepsLastGoodReadingAndSaysSo()
    {
        var provider = new GatedProvider(Reading(40));
        var page = new UsagePage(provider);
        var redraws = 0;
        page.ItemsChanged += (_, _) => Interlocked.Increment(ref redraws);
        Text(page);

        provider.Complete(UsageResult.Failure(UsageFailureReason.MissingApiKey, "API key rejected"));
        await page.PendingRefresh!.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitFor(() => redraws == 1);

        var text = Text(page);
        Assert.Contains("Refresh failed: API key rejected", text);
        Assert.Contains("60% left", text);
    }

    [Fact]
    public async Task ReadFinishingAroundTheFirstDrawNeverLeavesRefreshingOnScreen()
    {
        // Answers in roughly the page's 150 ms wait, so the read lands while the first
        // draw is in progress. Whatever the interleaving, once the read is done the
        // content on screen must not say "Refreshing…".
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var provider = new DelayedProvider(Reading(40), Reading(70), TimeSpan.FromMilliseconds(140 + attempt % 20));
            var page = new UsagePage(provider);
            var content = Assert.IsAssignableFrom<IFormContent>(Assert.Single(page.GetContent()));

            await WaitFor(() => page.PendingRefresh is null);
            await WaitFor(() => !Regex.Unescape(content.TemplateJson).Contains("Refreshing…"));
            Assert.Contains("30% left", Regex.Unescape(content.TemplateJson));
        }
    }

    [Fact]
    public void FirstOpenWithoutAnyReadingShowsLoading()
    {
        var page = new UsagePage(new GatedProvider(null));

        var text = Text(page);

        Assert.Contains("Reading usage…", text);
    }

    private static string Text(UsagePage page)
    {
        var content = Assert.IsAssignableFrom<IFormContent>(Assert.Single(page.GetContent()));
        return Regex.Unescape(content.TemplateJson);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class DelayedProvider(UsageResult last, UsageResult next, TimeSpan delay) : IUsageProvider
    {
        private UsageResult _last = last;

        public string Id => "codex";
        public string DisplayName => "Codex";
        public string IconName => "codex";
        public UsageResult? LastResult => Volatile.Read(ref _last);

        public async Task<UsageResult> GetAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            Volatile.Write(ref _last, next);
            return next;
        }

        public void Dispose() { }
    }

    /// <summary>Answers GetAsync only when the test calls Complete.</summary>
    private sealed class GatedProvider(UsageResult? last) : IUsageProvider
    {
        private readonly TaskCompletionSource<UsageResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private UsageResult? _last = last;

        public string Id => "codex";
        public string DisplayName => "Codex";
        public string IconName => "codex";
        public int Calls { get; private set; }
        public UsageResult? LastResult => Volatile.Read(ref _last);

        public void Complete(UsageResult result) => _answer.TrySetResult(result);

        public async Task<UsageResult> GetAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = await _answer.Task;
            Volatile.Write(ref _last, result);
            return result;
        }

        public void Dispose() { }
    }
}
