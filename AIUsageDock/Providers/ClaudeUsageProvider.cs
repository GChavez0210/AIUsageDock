using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageDock.Models;

namespace AIUsageDock.Providers;

internal sealed partial class ClaudeUsageProvider : IUsageProvider
{
    private static readonly TimeSpan FreshCacheAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CommandThrottle = TimeSpan.FromSeconds(30);
    // A usage read is not the moment to have Claude Code replace itself in the background.
    internal static readonly IReadOnlyDictionary<string, string> CliEnvironment =
        new Dictionary<string, string> { ["DISABLE_AUTOUPDATER"] = "1" };

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly string _configDirectory;
    private readonly string _cachePath;
    private UsageResult? _lastResult;
    private DateTimeOffset _lastCommandAttempt = DateTimeOffset.MinValue;

    public ClaudeUsageProvider()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        _cachePath = _configDirectory + ".json";
    }

    public string Id => "claude";
    public string DisplayName => "Claude";
    public string IconName => "claude";

    public async Task<UsageResult> GetAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cached = TryReadCache(now, out var age, out var resetExpired, out var cacheError);
            var fresh = cached is not null && !resetExpired && age <= FreshCacheAge;

            if (!force && fresh)
            {
                return _lastResult = UsageResult.Success(cached!);
            }

            string? refreshError = null;
            UsageSnapshot? commandSnapshot = null;
            var mayRun = now - _lastCommandAttempt >= CommandThrottle;

            if (mayRun)
            {
                _lastCommandAttempt = now;
                (commandSnapshot, refreshError) = await TryRefreshFromCliAsync(now, cancellationToken);
            }
            else
            {
                refreshError = "Claude refresh is throttled";
            }

            if (commandSnapshot is not null)
            {
                return _lastResult = UsageResult.Success(WithAccountDetails(commandSnapshot));
            }

            // Current Claude releases commonly refresh cachedUsageUtilization instead of
            // rendering percentages in print mode. Reread after the command exits.
            var refreshedCache = TryReadCache(now, out _, out var refreshedResetExpired, out _);
            if (refreshedCache is not null && !refreshedResetExpired)
            {
                return _lastResult = UsageResult.Success(refreshedCache, refreshError);
            }

            if (cached is not null && !resetExpired)
            {
                var stale = cached with { IsStale = true };
                return _lastResult = UsageResult.Success(stale, refreshError ?? cacheError);
            }

            if (_lastResult?.Snapshot is { } previous &&
                previous.Windows.Any(window => window.ResetsAt is null || window.ResetsAt > now))
            {
                return _lastResult = UsageResult.Success(previous with { IsStale = true }, refreshError ?? cacheError);
            }

            var message = refreshError ?? cacheError ?? "Claude reported no active usage limits";
            var reason = refreshError?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true
                ? UsageFailureReason.CliUnavailable
                : UsageFailureReason.InvalidData;
            return _lastResult = UsageResult.Failure(reason, message);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static UsageSnapshot? ParseCacheJson(string json, DateTimeOffset now, out DateTimeOffset? fetchedAt)
    {
        fetchedAt = null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("cachedUsageUtilization", out var cache) || cache.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (cache.TryGetProperty("fetchedAtMs", out var fetchedElement) && fetchedElement.TryGetInt64(out var fetchedMs) && fetchedMs > 0)
        {
            fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetchedMs);
        }

        if (!cache.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Shared buckets first. The "limits" array may repeat them with a severity, so
        // the two sources are merged by kind rather than appended.
        var windows = new List<UsageWindow>();
        AddCachedBucket(utilization, "five_hour", UsageWindowKind.Session, "Session", now, windows);
        AddCachedBucket(utilization, "seven_day", UsageWindowKind.Weekly, "Week (all models)", now, windows);

        if (utilization.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                var group = TryGetString(limit, "group");
                if (!TryGetDouble(limit, "percent", out var percent) || string.IsNullOrWhiteSpace(group))
                {
                    continue;
                }

                var kind = group.ToLowerInvariant() switch
                {
                    "session" => UsageWindowKind.Session,
                    "weekly" => UsageWindowKind.Weekly,
                    "monthly" => UsageWindowKind.Monthly,
                    _ => UsageWindowKind.Other,
                };

                var label = kind switch
                {
                    UsageWindowKind.Session => "Session",
                    UsageWindowKind.Weekly => "Week (all models)",
                    UsageWindowKind.Monthly => "Month",
                    _ => TryGetString(limit, "kind") ?? group,
                };

                var reset = ParseIsoDate(TryGetString(limit, "resets_at"));
                var severity = TryGetString(limit, "severity");
                AddIfActive(windows, new UsageWindow(kind, label, Math.Clamp(percent, 0d, 100d), reset, DurationFor(kind), Severity: severity), now);
            }
        }

        var shared = windows
            .GroupBy(window => window.Kind)
            .SelectMany(group => group.Key == UsageWindowKind.Other
                ? group.ToArray()
                : new[] { MergeDuplicates(group) })
            .ToList();

        // Per-model weekly caps are separate limits, not duplicates of the shared week.
        AddCachedBucket(utilization, "seven_day_opus", UsageWindowKind.Weekly, "Week (Opus)", now, shared);
        AddCachedBucket(utilization, "seven_day_sonnet", UsageWindowKind.Weekly, "Week (Sonnet)", now, shared);

        if (shared.Count == 0)
        {
            return null;
        }

        var details = new List<UsageDetail>();
        var notes = new List<string>();
        var plan = ReadAccountDetails(root, details);
        ReadExtraUsage(utilization, details, notes);

        return new UsageSnapshot(
            "Claude",
            plan,
            shared,
            fetchedAt ?? now,
            "Claude usage cache",
            Details: details,
            Notes: notes);
    }

    /// <summary>
    /// Two entries of the same kind describe the same limit; keep the higher
    /// utilization and carry over whichever metadata each side reported.
    /// </summary>
    private static UsageWindow MergeDuplicates(IEnumerable<UsageWindow> group)
    {
        var ordered = group.OrderByDescending(window => window.UsedPercent).ToArray();
        var primary = ordered[0];
        return primary with
        {
            Severity = primary.Severity ?? ordered.Select(window => window.Severity).FirstOrDefault(value => value is not null),
            LockedReason = primary.LockedReason ?? ordered.Select(window => window.LockedReason).FirstOrDefault(value => value is not null),
            ResetsAt = primary.ResetsAt ?? ordered.Select(window => window.ResetsAt).FirstOrDefault(value => value is not null),
        };
    }

    /// <summary>
    /// Reads the non-secret profile Claude Code keeps beside the cache. Returns the plan
    /// name. Tokens live elsewhere and are never touched.
    /// </summary>
    internal static string? ReadAccountDetails(JsonElement root, ICollection<UsageDetail> details)
    {
        if (!root.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var plan = DescribePlan(TryGetString(account, "organizationType"), TryGetString(account, "seatTier"));
        if (plan is not null)
        {
            details.Add(new UsageDetail("Plan", plan));
        }

        var email = TryGetString(account, "emailAddress");
        var name = TryGetString(account, "displayName") ?? TryGetString(account, "fullName");
        if (!string.IsNullOrWhiteSpace(email))
        {
            details.Add(new UsageDetail("Account", string.IsNullOrWhiteSpace(name) ? email : $"{name} · {email}"));
        }

        // Personal plans name the org after the email; that adds nothing, so skip it.
        var organization = TryGetString(account, "organizationName");
        var role = TryGetString(account, "organizationRole");
        var personalOrg = !string.IsNullOrWhiteSpace(email) &&
                          organization?.StartsWith(email, StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(organization) && !personalOrg)
        {
            details.Add(new UsageDetail("Organization", string.IsNullOrWhiteSpace(role) ? organization : $"{organization} ({role})"));
        }

        var billing = TryGetString(account, "billingType");
        if (!string.IsNullOrWhiteSpace(billing))
        {
            details.Add(new UsageDetail("Billing", Humanize(billing)));
        }

        var tier = TryGetString(account, "userRateLimitTier") ?? TryGetString(account, "organizationRateLimitTier");
        if (!string.IsNullOrWhiteSpace(tier) && !tier.StartsWith("default", StringComparison.OrdinalIgnoreCase))
        {
            details.Add(new UsageDetail("Rate limit tier", Humanize(tier)));
        }

        var subscribed = ParseIsoDate(TryGetString(account, "subscriptionCreatedAt"));
        if (subscribed is { } since)
        {
            details.Add(new UsageDetail("Subscribed since", since.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)));
        }

        var trialEnds = ParseIsoDate(TryGetString(account, "claudeCodeTrialEndsAt"));
        if (trialEnds is { } trial)
        {
            details.Add(new UsageDetail("Trial ends", trial.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)));
        }

        return plan;
    }

    internal static string? DescribePlan(string? organizationType, string? seatTier)
    {
        var plan = organizationType?.ToLowerInvariant() switch
        {
            null or "" => null,
            "claude_pro" => "Claude Pro",
            "claude_max" => "Claude Max",
            "claude_team" => "Claude Team",
            "claude_enterprise" => "Claude Enterprise",
            "claude_free" => "Claude Free",
            var other => Humanize(other),
        };

        if (plan is not null && !string.IsNullOrWhiteSpace(seatTier))
        {
            plan += $" ({Humanize(seatTier)})";
        }

        return plan;
    }

    private static void ReadExtraUsage(JsonElement utilization, ICollection<UsageDetail> details, ICollection<string> notes)
    {
        if (!utilization.TryGetProperty("extra_usage", out var extra) || extra.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var enabled = extra.TryGetProperty("is_enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
        var userDisabled = extra.TryGetProperty("user_disabled", out var disabledElement) && disabledElement.ValueKind == JsonValueKind.True;
        if (!enabled)
        {
            var reason = TryGetString(extra, "disabled_reason");
            details.Add(new UsageDetail("Extra usage", userDisabled ? "Off" : reason is null ? "Not enabled" : $"Off ({Humanize(reason)})"));
            return;
        }

        var currency = TryGetString(extra, "currency") ?? "USD";
        var decimals = extra.TryGetProperty("decimal_places", out var decimalsElement) && decimalsElement.TryGetInt32(out var parsedDecimals)
            ? parsedDecimals
            : 2;
        var scale = Math.Pow(10, decimals);
        TryGetDouble(extra, "used_credits", out var usedMinor);
        var hasLimit = TryGetDouble(extra, "monthly_limit", out var limitMinor) && limitMinor > 0;

        var used = FormatMoney(usedMinor / scale, currency);
        details.Add(new UsageDetail(
            "Extra usage",
            hasLimit ? $"{used} of {FormatMoney(limitMinor / scale, currency)} monthly cap" : $"{used} this month"));

        if (extra.TryGetProperty("spend_limit_reached", out var reached) && reached.ValueKind == JsonValueKind.True)
        {
            notes.Add("Extra usage spend limit reached; requests fall back to the subscription limits.");
        }
    }

    private static string FormatMoney(double amount, string currency) =>
        currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? amount.ToString("$#,##0.00", CultureInfo.InvariantCulture)
            : $"{amount.ToString("#,##0.00", CultureInfo.InvariantCulture)} {currency}";

    private static string Humanize(string value)
    {
        var words = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    internal static UsageSnapshot? ParseUsageCommandJson(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var windows = new List<UsageWindow>();
        foreach (var rawLine in (result.GetString() ?? string.Empty).Split('\n'))
        {
            var match = UsageLineRegex().Match(rawLine.Trim());
            if (!match.Success || !double.TryParse(match.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var used))
            {
                continue;
            }

            var label = match.Groups[1].Value.Trim();
            var lower = label.ToLowerInvariant();
            var kind = lower.Contains("session")
                ? UsageWindowKind.Session
                : lower.Contains("week")
                    ? UsageWindowKind.Weekly
                    : lower.Contains("month")
                        ? UsageWindowKind.Monthly
                        : UsageWindowKind.Other;

            var reset = ParseHumanReset(match.Groups[3].Value, now);
            windows.Add(new UsageWindow(kind, label, Math.Clamp(used, 0d, 100d), reset, DurationFor(kind)));
        }

        if (windows.Count == 0)
        {
            return null;
        }

        var binding = windows
            .GroupBy(window => window.Kind)
            .Select(group => group.OrderByDescending(window => window.UsedPercent).First())
            .ToArray();

        return new UsageSnapshot("Claude", null, binding, now, "claude /usage");
    }

    /// <summary>
    /// The /usage text has no profile block; borrow it from the config file so the
    /// detail page looks the same whichever path produced the numbers.
    /// </summary>
    private UsageSnapshot WithAccountDetails(UsageSnapshot snapshot)
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return snapshot;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var root = document.RootElement;
            var details = new List<UsageDetail>();
            var notes = new List<string>();
            var plan = ReadAccountDetails(root, details);
            if (root.TryGetProperty("cachedUsageUtilization", out var cache) && cache.ValueKind == JsonValueKind.Object &&
                cache.TryGetProperty("utilization", out var utilization) && utilization.ValueKind == JsonValueKind.Object)
            {
                ReadExtraUsage(utilization, details, notes);
            }

            return snapshot with { Plan = snapshot.Plan ?? plan, Details = details, Notes = notes };
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return snapshot;
        }
    }

    private UsageSnapshot? TryReadCache(
        DateTimeOffset now,
        out TimeSpan age,
        out bool resetExpired,
        out string? error)
    {
        age = TimeSpan.MaxValue;
        resetExpired = false;
        error = null;

        try
        {
            if (!File.Exists(_cachePath))
            {
                error = $"Claude usage cache not found at {_cachePath}";
                return null;
            }

            var snapshot = ParseCacheJson(File.ReadAllText(_cachePath), now, out var fetchedAt);
            if (snapshot is null)
            {
                error = "Claude usage cache has no active limits";
                return null;
            }

            age = fetchedAt.HasValue ? now - fetchedAt.Value : TimeSpan.MaxValue;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            resetExpired = snapshot.Windows.Any(window => window.ResetsAt.HasValue && window.ResetsAt <= now);
            return snapshot;
        }
        catch (JsonException exception)
        {
            error = $"Claude usage cache is invalid: {exception.Message}";
            return null;
        }
        catch (IOException exception)
        {
            error = $"Claude usage cache could not be read: {exception.Message}";
            return null;
        }
    }

    private async Task<(UsageSnapshot? Snapshot, string? Error)> TryRefreshFromCliAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            return (null, "ANTHROPIC_API_KEY is set; skipped Claude subscription refresh");
        }

        var executable = ExecutableResolver.ResolveClaude();
        if (executable is null)
        {
            return (null, "Claude CLI not found");
        }

        try
        {
            var result = await ProcessHelpers.RunAsync(
                executable,
                new[] { "-p", "/usage", "--output-format", "json", "--no-session-persistence" },
                ProcessTimeout,
                cancellationToken,
                CliEnvironment);

            if (result.ExitCode != 0)
            {
                return (null, $"Claude /usage exited with code {result.ExitCode}: {FirstLine(result.StandardError)}");
            }

            try
            {
                return (ParseUsageCommandJson(result.StandardOutput, now), null);
            }
            catch (JsonException exception)
            {
                return (null, $"Claude /usage returned invalid JSON: {exception.Message}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "Claude /usage timed out");
        }
        catch (Exception exception)
        {
            return (null, $"Claude /usage failed: {exception.Message}");
        }
    }

    private static void AddCachedBucket(
        JsonElement utilization,
        string property,
        UsageWindowKind kind,
        string label,
        DateTimeOffset now,
        ICollection<UsageWindow> windows)
    {
        if (!utilization.TryGetProperty(property, out var bucket) || bucket.ValueKind != JsonValueKind.Object ||
            !TryGetDouble(bucket, "utilization", out var used))
        {
            return;
        }

        var reset = ParseIsoDate(TryGetString(bucket, "resets_at"));
        var locked = TryGetString(bucket, "locked_reason");
        AddIfActive(windows, new UsageWindow(kind, label, Math.Clamp(used, 0d, 100d), reset, DurationFor(kind), LockedReason: locked), now);
    }

    private static int? DurationFor(UsageWindowKind kind) => kind switch
    {
        UsageWindowKind.Session => 300,
        UsageWindowKind.Weekly => 10080,
        _ => null,
    };

    private static void AddIfActive(ICollection<UsageWindow> windows, UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is null || window.ResetsAt > now)
        {
            windows.Add(window);
        }
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

    private static DateTimeOffset? ParseIsoDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ParseHumanReset(string value, DateTimeOffset now)
    {
        var match = ResetTimeRegex().Match(value.Trim());
        if (!match.Success ||
            !DateTime.TryParseExact(match.Groups["month"].Value[..3], "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthValue) ||
            !int.TryParse(match.Groups["day"].Value, out var day) ||
            !int.TryParse(match.Groups["hour"].Value, out var hour))
        {
            return null;
        }

        var minute = int.TryParse(match.Groups["minute"].Value, out var parsedMinute) ? parsedMinute : 0;
        var meridiem = match.Groups["ampm"].Value.ToLowerInvariant();
        if (meridiem == "pm" && hour < 12)
        {
            hour += 12;
        }
        else if (meridiem == "am" && hour == 12)
        {
            hour = 0;
        }

        var zone = TimeZoneInfo.Local;
        var zoneName = match.Groups["zone"].Value;
        if (!string.IsNullOrWhiteSpace(zoneName))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
            }
            catch (TimeZoneNotFoundException)
            {
                if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneName, out var windowsId))
                {
                    try { zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId); } catch { }
                }
            }
            catch (InvalidTimeZoneException) { }
        }

        var local = new DateTime(now.Year, monthValue.Month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var candidate = new DateTimeOffset(local, zone.GetUtcOffset(local));
        if (candidate < now.AddHours(-24))
        {
            local = local.AddYears(1);
            candidate = new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        return candidate;
    }

    private static string FirstLine(string value) =>
        value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value.Trim();

    [GeneratedRegex(@"^(.*?):\s*([0-9]+(?:\.[0-9]+)?)%\s+used(?:\s*[·・]\s*resets\s+(.*?))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UsageLineRegex();

    [GeneratedRegex(@"^(?<month>[A-Za-z]{3,})\s+(?<day>\d{1,2})(?:,|\s+at)\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>[AaPp][Mm])?\s*(?:\((?<zone>[^)]+)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResetTimeRegex();

    public void Dispose() => _refreshLock.Dispose();
}
