using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageDock.Models;

namespace AIUsageDock.Providers;

/// <summary>
/// Reads Ollama Cloud capacity from the documented balance endpoint
/// (https://docs.ollama.com/api/balance). Legacy Pro/Max plans report session and
/// weekly windows as remaining percentages; current plans report an included credit
/// allowance for the billing period. /api/usage is not used: since 2026-11-04 it only
/// returns request counts and token totals, not limits.
/// </summary>
internal sealed class OllamaUsageProvider : IUsageProvider
{
    internal const string ApiKeyVariable = "OLLAMA_API_KEY";
    private const string BalanceUrl = "https://ollama.com/api/balance";

    // The endpoint allows 10 requests per minute per user, shared across keys and
    // devices, and recommends polling once a minute.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly Func<string?> _readApiKey;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private UsageResult? _cached;

    /// <param name="savedApiKey">The key saved from the settings page; when it returns
    /// null the <see cref="ApiKeyVariable"/> environment variable is used.</param>
    public OllamaUsageProvider(Func<string?>? savedApiKey = null)
        : this(new HttpClient { Timeout = RequestTimeout }, () => savedApiKey?.Invoke() ?? ReadApiKeyFromEnvironment())
    {
    }

    internal OllamaUsageProvider(HttpClient http, Func<string?> readApiKey)
    {
        _http = http;
        _readApiKey = readApiKey;
    }

    public string Id => "ollama";
    public string DisplayName => "Ollama Cloud";
    public string IconName => "ollama";

    public async Task<UsageResult> GetAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && IsFresh(_cached?.Snapshot))
        {
            return _cached!;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && IsFresh(_cached?.Snapshot))
            {
                return _cached!;
            }

            // A forced refresh still honours a 429's Retry-After, so mashing
            // "Refresh AI usage" cannot lock the account out of other devices.
            if (DateTimeOffset.UtcNow < _retryAfter && _cached is not null)
            {
                return _cached;
            }

            var apiKey = _readApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return _cached = UsageResult.Failure(UsageFailureReason.MissingApiKey, $"set {ApiKeyVariable}");
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Headers.UserAgent.ParseAdd("AIUsageDock");
                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        return _cached = UsageResult.Failure(UsageFailureReason.MissingApiKey, "API key rejected");
                    case HttpStatusCode.Forbidden:
                        return _cached = UsageResult.Failure(UsageFailureReason.ProcessFailed, "account suspended");
                    case HttpStatusCode.TooManyRequests:
                        _retryAfter = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                        return KeepLastOrFail(UsageFailureReason.ProcessFailed, "rate limited");
                    case HttpStatusCode.ServiceUnavailable:
                        return KeepLastOrFail(UsageFailureReason.ProcessFailed, "balance temporarily unavailable");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return KeepLastOrFail(UsageFailureReason.ProcessFailed, $"HTTP {(int)response.StatusCode}");
                }

                var snapshot = ParseBalanceJson(body, DateTimeOffset.UtcNow);
                return _cached = snapshot is null
                    ? KeepLastOrFail(UsageFailureReason.InvalidData, "Ollama reported no included plan usage")
                    : UsageResult.Success(snapshot);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return KeepLastOrFail(UsageFailureReason.TimedOut, "ollama.com timed out");
            }
            catch (HttpRequestException exception)
            {
                return KeepLastOrFail(UsageFailureReason.ProcessFailed, $"network error: {exception.Message}");
            }
            catch (JsonException exception)
            {
                return KeepLastOrFail(UsageFailureReason.InvalidData, $"invalid JSON: {exception.Message}");
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static UsageSnapshot? ParseBalanceJson(string json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var windows = new List<UsageWindow>();
        var details = new List<UsageDetail>();
        string? plan = null;

        if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Object)
        {
            AddPercentWindow(included, "session", UsageWindowKind.Session, "Session", 300, windows);
            AddPercentWindow(included, "weekly", UsageWindowKind.Weekly, "Week", 10080, windows);

            if (windows.Count > 0)
            {
                plan = "Legacy";
            }
            else if (TryGetDouble(included, "balance_usd", out var balance) &&
                     TryGetDouble(included, "allowance_usd", out var allowance) && allowance > 0)
            {
                DateTimeOffset? from = null;
                DateTimeOffset? until = null;
                if (included.TryGetProperty("period", out var period) && period.ValueKind == JsonValueKind.Object)
                {
                    from = TryGetDate(period, "from");
                    until = TryGetDate(period, "until");
                }

                int? minutes = from is { } start && until is { } end && end > start
                    ? (int)Math.Round((end - start).TotalMinutes)
                    : null;
                // Billing periods run a calendar month, 28 to 31 days.
                var kind = minutes is >= 28 * 1440 and <= 31 * 1440 ? UsageWindowKind.Monthly : UsageWindowKind.Other;
                var used = Math.Round(Math.Clamp((allowance - balance) / allowance, 0d, 1d) * 100d, 6);

                windows.Add(new UsageWindow(kind, kind == UsageWindowKind.Monthly ? "Month" : "Included credits", used, until, minutes));
                details.Add(new UsageDetail("Included credits", $"{FormatUsd(balance)} of {FormatUsd(allowance)} left"));
            }
        }

        if (root.TryGetProperty("purchased", out var purchased) && purchased.ValueKind == JsonValueKind.Object &&
            TryGetDouble(purchased, "balance_usd", out var purchasedBalance))
        {
            details.Add(new UsageDetail("Purchased credits", FormatUsd(purchasedBalance)));
        }

        if (windows.Count == 0)
        {
            return null;
        }

        if (plan is not null)
        {
            details.Insert(0, new UsageDetail("Plan", plan));
        }

        return new UsageSnapshot("Ollama Cloud", plan, windows, fetchedAt, "ollama.com/api/balance", Details: details);
    }

    private static void AddPercentWindow(JsonElement included, string property, UsageWindowKind kind, string label, int minutes, List<UsageWindow> windows)
    {
        if (included.TryGetProperty(property, out var window) && window.ValueKind == JsonValueKind.Object &&
            TryGetDouble(window, "remaining_percent", out var remaining))
        {
            var used = Math.Round(100d - Math.Clamp(remaining, 0d, 100d), 6);
            windows.Add(new UsageWindow(kind, label, used, TryGetDate(window, "resets_at"), minutes));
        }
    }

    /// <summary>
    /// The process environment first, then the user's persisted variables, so a key
    /// set after Command Palette started is picked up without a restart.
    /// </summary>
    internal static string? ReadApiKeyFromEnvironment() =>
        NullIfBlank(Environment.GetEnvironmentVariable(ApiKeyVariable)) ??
        NullIfBlank(Environment.GetEnvironmentVariable(ApiKeyVariable, EnvironmentVariableTarget.User));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FormatUsd(double value) => "$" + value.ToString("0.00", CultureInfo.InvariantCulture);

    private UsageResult KeepLastOrFail(UsageFailureReason reason, string message)
    {
        if (_cached?.Snapshot is { } snapshot && snapshot.Windows.Any(window => window.ResetsAt is null || window.ResetsAt > DateTimeOffset.UtcNow))
        {
            return _cached = UsageResult.Success(snapshot with { IsStale = true }, message);
        }

        return _cached = UsageResult.Failure(reason, message);
    }

    private static DateTimeOffset? TryGetDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(child.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    private static bool TryGetDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var child) &&
               child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out value);
    }

    private static bool IsFresh(UsageSnapshot? snapshot) =>
        snapshot is not null && DateTimeOffset.UtcNow - snapshot.FetchedAt < CacheLifetime;

    public void Dispose()
    {
        _refreshLock.Dispose();
        _http.Dispose();
    }
}
