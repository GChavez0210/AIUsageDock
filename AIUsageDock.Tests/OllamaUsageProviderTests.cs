using System.Net;
using AIUsageDock.Models;
using AIUsageDock.Providers;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class OllamaUsageProviderTests
{
    [Fact]
    public void LegacyPlanMapsSessionAndWeeklyRemainingPercent()
    {
        // Legacy-plan response shape from /api/balance.
        const string json = """
        {"included":{"session":{"remaining_percent":84.23,"resets_at":"2026-11-08T10:00:00Z"},"weekly":{"remaining_percent":22.03,"resets_at":"2026-11-09T00:00:00Z"}},"purchased":{"balance_usd":0}}
        """;

        var snapshot = OllamaUsageProvider.ParseBalanceJson(json, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot);
        Assert.Equal("Legacy", snapshot.Plan);
        Assert.Equal(84.23, snapshot.Session!.RemainingPercent, 6);
        Assert.Equal(300, snapshot.Session.DurationMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T10:00:00Z"), snapshot.Session.ResetsAt);
        Assert.Equal(22.03, snapshot.Weekly!.RemainingPercent, 6);
        Assert.Equal(10080, snapshot.Weekly.DurationMinutes);
        Assert.Contains(snapshot.AccountDetails, detail => detail == new UsageDetail("Purchased credits", "$0.00"));
    }

    [Fact]
    public void CurrentPlanMapsIncludedCreditsToMonthlyWindow()
    {
        const string json = """
        {"included":{"balance_usd":72.5,"allowance_usd":100,"period":{"from":"2026-09-15T09:30:00Z","until":"2026-10-15T09:30:00Z"}},"purchased":{"balance_usd":25}}
        """;

        var snapshot = OllamaUsageProvider.ParseBalanceJson(json, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Plan);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowKind.Monthly, window.Kind);
        Assert.Equal(72.5, window.RemainingPercent, 6);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T09:30:00Z"), window.ResetsAt);
        Assert.Contains(snapshot.AccountDetails, detail => detail == new UsageDetail("Included credits", "$72.50 of $100.00 left"));
        Assert.Contains(snapshot.AccountDetails, detail => detail == new UsageDetail("Purchased credits", "$25.00"));
    }

    [Fact]
    public void PurchasedCreditsAloneIsNotAPlanWindow()
    {
        Assert.Null(OllamaUsageProvider.ParseBalanceJson("""{"purchased":{"balance_usd":5}}""", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task MissingKeySkipsTheRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        using var provider = new OllamaUsageProvider(new HttpClient(handler), () => null);

        var result = await provider.GetAsync(force: true);

        Assert.Equal(UsageFailureReason.MissingApiKey, result.FailureReason);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendsBearerKeyAndReportsRejectedKey()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");
        using var provider = new OllamaUsageProvider(new HttpClient(handler), () => " test-key ");

        var result = await provider.GetAsync(force: true);

        Assert.Equal(UsageFailureReason.MissingApiKey, result.FailureReason);
        Assert.Equal("Bearer test-key", handler.LastAuthorization);
        Assert.Equal("https://ollama.com/api/balance", handler.LastUri);
    }

    [Fact]
    public async Task RateLimitKeepsLastSnapshotAndHonoursRetryAfter()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"included":{"session":{"remaining_percent":50,"resets_at":"2999-01-01T00:00:00Z"}}}""");
        using var provider = new OllamaUsageProvider(new HttpClient(handler), () => "key");

        Assert.True((await provider.GetAsync(force: true)).IsSuccess);

        handler.Status = HttpStatusCode.TooManyRequests;
        handler.RetryAfterSeconds = 60;
        var limited = await provider.GetAsync(force: true);
        Assert.True(limited.IsSuccess);
        Assert.True(limited.Snapshot!.IsStale);

        var again = await provider.GetAsync(force: true);
        Assert.Same(limited, again);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public int? RetryAfterSeconds { get; set; }
        public int Calls { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastUri = request.RequestUri?.ToString();
            var response = new HttpResponseMessage(Status) { Content = new StringContent(body) };
            if (RetryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return Task.FromResult(response);
        }
    }
}
