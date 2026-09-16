using System.Globalization;
using System.Text.Json;
using AIUsageDock.Models;

namespace AIUsageDock.Providers;

internal sealed class AntigravityUsageProvider : IUsageProvider
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly string _accountsPath;
    private UsageResult? _cached;

    public AntigravityUsageProvider()
    {
        // The CLI records only which Google account is active here; credentials are
        // kept in a separate file this provider never opens.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDirectory = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME") ?? Path.Combine(home, ".gemini");
        _accountsPath = Path.Combine(configDirectory, "google_accounts.json");
    }

    public string Id => "antigravity";
    public string DisplayName => "Antigravity";
    public string IconName => "antigravity";

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

            var executable = ExecutableResolver.ResolveAntigravity();
            if (executable is null)
            {
                return _cached = UsageResult.Failure(UsageFailureReason.CliUnavailable, "Antigravity CLI not found");
            }

            try
            {
                var result = await ProcessHelpers.RunAsync(
                    executable,
                    ["-p", "/usage", "--mode", "plan", "--output-format", "json", "--print-timeout", "15s"],
                    ProcessTimeout,
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    return KeepLastOrFail(
                        UsageFailureReason.ProcessFailed,
                        $"agy /usage exited with code {result.ExitCode}: {FirstLine(result.StandardError)}");
                }

                var snapshot = ParseUsageJson(result.StandardOutput, DateTimeOffset.UtcNow, ReadActiveAccount());
                return _cached = snapshot is null
                    ? KeepLastOrFail(UsageFailureReason.InvalidData, DescribeMissingQuota(result.StandardOutput))
                    : UsageResult.Success(snapshot);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return KeepLastOrFail(UsageFailureReason.TimedOut, "agy /usage timed out");
            }
            catch (JsonException exception)
            {
                return KeepLastOrFail(UsageFailureReason.InvalidData, $"agy /usage returned invalid JSON: {exception.Message}");
            }
            catch (Exception exception)
            {
                return KeepLastOrFail(UsageFailureReason.ProcessFailed, $"agy /usage failed: {exception.Message}");
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static UsageSnapshot? ParseUsageJson(string json, DateTimeOffset fetchedAt, string? activeAccount = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!string.Equals(TryGetString(root, "status"), "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object ||
            !string.Equals(TryGetString(command, "name"), "usage", StringComparison.OrdinalIgnoreCase) ||
            !command.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var windows = new List<UsageWindow>();
        foreach (var group in groups.EnumerateArray())
        {
            var groupName = TryGetString(group, "name") ?? "Models";
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!TryGetDouble(bucket, "remaining_fraction", out var remainingFraction))
                {
                    continue;
                }

                var windowName = TryGetString(bucket, "window") ?? "other";
                var kind = windowName.ToLowerInvariant() switch
                {
                    "5h" => UsageWindowKind.Session,
                    "weekly" => UsageWindowKind.Weekly,
                    "monthly" => UsageWindowKind.Monthly,
                    _ => UsageWindowKind.Other,
                };
                int? duration = kind switch
                {
                    UsageWindowKind.Session => 300,
                    UsageWindowKind.Weekly => 10080,
                    UsageWindowKind.Monthly => 43200,
                    _ => null,
                };
                var label = duration is null ? $"{groupName} ({windowName})" : groupName;
                var reset = DateTimeOffset.TryParse(
                    TryGetString(bucket, "reset_time"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsedReset)
                    ? parsedReset
                    : (DateTimeOffset?)null;
                var usedPercent = Math.Round(100d - Math.Clamp(remainingFraction, 0d, 1d) * 100d, 6);

                windows.Add(new UsageWindow(kind, label, usedPercent, reset, duration));
            }
        }

        if (windows.Count == 0)
        {
            return null;
        }

        var details = new List<UsageDetail>();
        var plan = ReadPlan(data);
        if (plan is not null)
        {
            details.Add(new UsageDetail("Plan", plan));
        }

        if (!string.IsNullOrWhiteSpace(activeAccount))
        {
            details.Add(new UsageDetail("Account", activeAccount));
        }

        return new UsageSnapshot("Antigravity", plan, windows, fetchedAt, "agy /usage", Details: details);
    }

    /// <summary>
    /// The quota payload has not carried a tier so far; look for the obvious keys so a
    /// future CLI that adds one shows up without a code change.
    /// </summary>
    private static string? ReadPlan(JsonElement data)
    {
        foreach (var key in new[] { "plan", "tier", "subscription", "plan_name", "subscription_tier" })
        {
            var value = TryGetString(data, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return Humanize(value);
            }
        }

        return null;
    }

    private string? ReadActiveAccount()
    {
        try
        {
            if (!File.Exists(_accountsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_accountsPath));
            return TryGetString(document.RootElement, "active");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Print mode sometimes answers /usage with an empty model turn instead of the
    /// quota block. Say so rather than reporting a generic parse failure.
    /// </summary>
    internal static string DescribeMissingQuota(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("command", out _) &&
                root.TryGetProperty("num_turns", out var turns) && turns.TryGetInt32(out var count) && count > 0)
            {
                return "agy ran a model turn instead of the /usage command; open the Antigravity CLI and run /usage to confirm it is signed in";
            }
        }
        catch (JsonException)
        {
        }

        return "Antigravity returned no quota buckets";
    }

    private static string Humanize(string value)
    {
        var words = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private UsageResult KeepLastOrFail(UsageFailureReason reason, string message)
    {
        if (_cached?.Snapshot is { } snapshot && snapshot.Windows.Any(window => window.ResetsAt is null || window.ResetsAt > DateTimeOffset.UtcNow))
        {
            return _cached = UsageResult.Success(snapshot with { IsStale = true }, message);
        }

        return _cached = UsageResult.Failure(reason, message);
    }

    private static bool TryGetDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var child) &&
               (child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out value) ||
                child.ValueKind == JsonValueKind.String && double.TryParse(child.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value));
    }

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;

    private static string FirstLine(string value) =>
        value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value.Trim();

    private static bool IsFresh(UsageSnapshot? snapshot) =>
        snapshot is not null && DateTimeOffset.UtcNow - snapshot.FetchedAt < CacheLifetime;

    public void Dispose() => _refreshLock.Dispose();
}
