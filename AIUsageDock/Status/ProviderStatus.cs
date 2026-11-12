using System.Collections.Concurrent;
using System.Text.Json;

namespace AIUsageDock.Status;

/// <summary>The statuspage.io indicator; <see cref="Operational"/> is its raw value "none".</summary>
internal enum StatusIndicator
{
    Operational,
    Minor,
    Major,
    Critical,
    Maintenance,
    Unknown,
}

internal sealed record ProviderStatus(StatusIndicator Indicator, string? Description, DateTimeOffset? UpdatedAt)
{
    public bool HasIssue => Indicator != StatusIndicator.Operational;

    /// <summary>"Minor incident: Elevated error rates", for a card or a band.</summary>
    public string Describe()
    {
        var kind = Indicator switch
        {
            StatusIndicator.Minor => "Minor incident",
            StatusIndicator.Major => "Major incident",
            StatusIndicator.Critical => "Critical incident",
            StatusIndicator.Maintenance => "Maintenance",
            StatusIndicator.Unknown => "Status unknown",
            _ => "Operational",
        };
        return string.IsNullOrWhiteSpace(Description) ? kind : $"{kind}: {Description.Trim()}";
    }

    /// <summary>Parses a statuspage.io <c>api/v2/status.json</c> body; null when it is not one.</summary>
    public static ProviderStatus? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.Object ||
                !status.TryGetProperty("indicator", out var indicator) ||
                indicator.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var description = status.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;

            DateTimeOffset? updatedAt = null;
            if (root.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object &&
                page.TryGetProperty("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(updated.GetString(), out var parsed))
            {
                updatedAt = parsed;
            }

            return new ProviderStatus(IndicatorFrom(indicator.GetString()), description, updatedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static StatusIndicator IndicatorFrom(string? raw) => raw switch
    {
        "none" => StatusIndicator.Operational,
        "minor" => StatusIndicator.Minor,
        "major" => StatusIndicator.Major,
        "critical" => StatusIndicator.Critical,
        "maintenance" => StatusIndicator.Maintenance,
        _ => StatusIndicator.Unknown,
    };
}

/// <summary>
/// Reads the public status pages of the providers that have one. No credentials or
/// account data are sent; the request is a plain GET of the vendor's status feed.
/// </summary>
internal sealed class StatusPoller : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "https://status.claude.com",
        ["codex"] = "https://status.openai.com",
    };

    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HttpClient? _http;
    private readonly ConcurrentDictionary<string, ProviderStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPolled = new(StringComparer.OrdinalIgnoreCase);

    public StatusPoller(Func<string, CancellationToken, Task<string>>? fetch = null, Func<DateTimeOffset>? clock = null)
    {
        if (fetch is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            fetch = (url, cancellation) => _http.GetStringAsync(url, cancellation);
        }

        _fetch = fetch;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static bool HasStatusPage(string providerId) => Pages.ContainsKey(providerId);

    /// <summary>The last status read for a provider; null before the first read or when it has no page.</summary>
    public ProviderStatus? For(string providerId) => _statuses.GetValueOrDefault(providerId);

    /// <summary>Forgets every reading, for when status checks are switched off.</summary>
    public void Clear()
    {
        _statuses.Clear();
        _lastPolled.Clear();
    }

    /// <summary>Polls the given providers, each at most once per five minutes unless forced.
    /// Returns true when any provider's status changed.</summary>
    public async Task<bool> PollAsync(IEnumerable<string> providerIds, bool force = false, CancellationToken cancellation = default)
    {
        var changes = await Task.WhenAll(providerIds
            .Where(HasStatusPage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => PollOneAsync(id, force, cancellation))).ConfigureAwait(false);
        return changes.Any(changed => changed);
    }

    private async Task<bool> PollOneAsync(string providerId, bool force, CancellationToken cancellation)
    {
        var now = _clock();
        if (!force && _lastPolled.TryGetValue(providerId, out var last) && now - last < MinInterval)
        {
            return false;
        }

        _lastPolled[providerId] = now;
        try
        {
            var body = await _fetch($"{Pages[providerId]}/api/v2/status.json", cancellation).ConfigureAwait(false);
            if (ProviderStatus.Parse(body) is not { } status)
            {
                return false;
            }

            var previous = _statuses.GetValueOrDefault(providerId);
            _statuses[providerId] = status;
            return previous?.Indicator != status.Indicator || previous?.Description != status.Description;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // An unreachable status page says nothing about the provider; keep the last reading.
            return false;
        }
    }

    public void Dispose() => _http?.Dispose();
}
