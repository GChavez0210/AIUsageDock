using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AIUsageDock.Models;

namespace AIUsageDock.Providers;

internal sealed class CodexUsageProvider : IUsageProvider
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private UsageResult? _cached;

    public string Id => "codex";
    public string DisplayName => "Codex";
    public string IconName => "codex";

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

            var executable = ExecutableResolver.ResolveCodex();
            if (executable is null)
            {
                return _cached = UsageResult.Failure(UsageFailureReason.CliUnavailable, "Codex CLI not found");
            }

            try
            {
                var (accountJson, rateLimitsJson) = await ReadWithShimFallbackAsync(
                    executable,
                    ExecutableResolver.ResolveCodexShim(),
                    path => ReadAccountAndRateLimitsAsync(path, cancellationToken));
                var snapshot = ParseRateLimitsJson(rateLimitsJson, DateTimeOffset.UtcNow, accountJson);
                return _cached = snapshot is null
                    ? UsageResult.Failure(UsageFailureReason.InvalidData, "Codex returned no rate-limit windows")
                    : UsageResult.Success(snapshot);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return KeepLastOrFail(UsageFailureReason.TimedOut, "Codex app-server timed out");
            }
            catch (InvalidOperationException exception)
            {
                var reason = exception.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                    ? UsageFailureReason.NotSignedIn
                    : UsageFailureReason.ProcessFailed;
                return KeepLastOrFail(reason, exception.Message);
            }
            catch (Exception exception)
            {
                return KeepLastOrFail(UsageFailureReason.ProcessFailed, exception.Message);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static UsageSnapshot? ParseRateLimitsJson(string json, DateTimeOffset fetchedAt, string? accountJson = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement snapshot;

        if (root.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object &&
            byId.TryGetProperty("codex", out var codex) &&
            codex.ValueKind == JsonValueKind.Object)
        {
            snapshot = codex;
        }
        else if (root.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            snapshot = legacy;
        }
        else
        {
            return null;
        }

        var windows = new List<(string Name, double Used, int? Minutes, DateTimeOffset? Reset)>();
        AddWindow(snapshot, "primary", windows);
        AddWindow(snapshot, "secondary", windows);

        if (windows.Count == 0)
        {
            return null;
        }

        var mapped = new List<UsageWindow>();
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var kind = ClassifyWindow(window.Minutes, index, windows.Count);
            var label = kind switch
            {
                UsageWindowKind.Session => "Session",
                UsageWindowKind.Weekly => "Week",
                UsageWindowKind.Monthly => "Month",
                _ => window.Name,
            };

            mapped.Add(new UsageWindow(kind, label, window.Used, window.Reset, window.Minutes));
        }

        var details = new List<UsageDetail>();
        var notes = new List<string>();
        var planType = TryGetString(snapshot, "planType");
        var plan = ReadAccountDetails(accountJson, planType, details);
        ReadLimitMetadata(snapshot, details, notes);

        return new UsageSnapshot("Codex", plan, mapped, fetchedAt, "codex app-server", Details: details, Notes: notes);
    }

    /// <summary>
    /// account/read reports who is signed in and how. Falls back to the plan echoed
    /// by the rate-limit document when the account call is unavailable.
    /// </summary>
    internal static string? ReadAccountDetails(string? accountJson, string? fallbackPlanType, ICollection<UsageDetail> details)
    {
        string? planType = fallbackPlanType;
        string? accountType = null;
        string? email = null;

        if (!string.IsNullOrWhiteSpace(accountJson))
        {
            try
            {
                using var document = JsonDocument.Parse(accountJson);
                if (document.RootElement.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
                {
                    accountType = TryGetString(account, "type");
                    email = TryGetString(account, "email");
                    planType = TryGetString(account, "planType") ?? planType;
                }
            }
            catch (JsonException)
            {
                // Account metadata is optional; the rate-limit document stands on its own.
            }
        }

        var plan = DescribePlan(planType, accountType);
        if (plan is not null)
        {
            details.Add(new UsageDetail("Plan", plan));
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            details.Add(new UsageDetail("Account", email));
        }

        if (accountType is not null)
        {
            details.Add(new UsageDetail("Sign-in", accountType.ToLowerInvariant() switch
            {
                "chatgpt" => "ChatGPT account",
                "apikey" or "api_key" => "OpenAI API key",
                var other => Humanize(other),
            }));
        }

        return plan;
    }

    internal static string? DescribePlan(string? planType, string? accountType)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return null;
        }

        var name = planType.ToLowerInvariant() switch
        {
            "free" => "Free",
            "plus" => "Plus",
            "pro" => "Pro",
            "go" => "Go",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "edu" => "Edu",
            var other => Humanize(other),
        };

        return accountType?.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) == true
            ? $"ChatGPT {name}"
            : name;
    }

    private static void ReadLimitMetadata(JsonElement snapshot, ICollection<UsageDetail> details, ICollection<string> notes)
    {
        var limitName = TryGetString(snapshot, "limitName");
        if (!string.IsNullOrWhiteSpace(limitName))
        {
            details.Add(new UsageDetail("Limit", limitName));
        }

        if (snapshot.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            var unlimited = credits.TryGetProperty("unlimited", out var unlimitedElement) && unlimitedElement.ValueKind == JsonValueKind.True;
            var hasCredits = credits.TryGetProperty("hasCredits", out var hasElement) && hasElement.ValueKind == JsonValueKind.True;
            var balance = credits.TryGetProperty("balance", out var balanceElement)
                ? balanceElement.ValueKind == JsonValueKind.String ? balanceElement.GetString() : balanceElement.GetRawText()
                : null;

            details.Add(new UsageDetail(
                "Credits",
                unlimited ? "Unlimited" : hasCredits && !string.IsNullOrWhiteSpace(balance) ? $"{balance} available" : "None"));
        }

        var reached = TryGetString(snapshot, "rateLimitReachedType");
        if (!string.IsNullOrWhiteSpace(reached))
        {
            notes.Add($"Rate limit reached: {Humanize(reached)}.");
        }
    }

    private static string Humanize(string value)
    {
        var words = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static void AddWindow(
        JsonElement snapshot,
        string property,
        ICollection<(string Name, double Used, int? Minutes, DateTimeOffset? Reset)> windows)
    {
        if (!snapshot.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetDouble(out var used))
        {
            return;
        }

        int? minutes = null;
        if (element.TryGetProperty("windowDurationMins", out var minutesElement) && minutesElement.TryGetInt32(out var parsedMinutes))
        {
            minutes = parsedMinutes;
        }

        DateTimeOffset? reset = null;
        if (element.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var unixSeconds))
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        windows.Add((property, Math.Clamp(used, 0d, 100d), minutes, reset));
    }

    private static UsageWindowKind ClassifyWindow(int? minutes, int index, int windowCount)
    {
        if (minutes is <= 1440 and > 0)
        {
            return UsageWindowKind.Session;
        }

        if (minutes is >= 40000)
        {
            return UsageWindowKind.Monthly;
        }

        if (minutes is >= 6000)
        {
            return UsageWindowKind.Weekly;
        }

        if (windowCount > 1)
        {
            return index == 0 ? UsageWindowKind.Session : UsageWindowKind.Weekly;
        }

        return UsageWindowKind.Other;
    }

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static async Task<T> ReadWithShimFallbackAsync<T>(
        string executable,
        string? shim,
        Func<string, Task<T>> read)
    {
        if (shim is null || string.Equals(executable, shim, StringComparison.OrdinalIgnoreCase))
        {
            return await read(executable);
        }

        try
        {
            return await read(executable);
        }
        catch (Exception exception) when (
            (exception is InvalidOperationException or Win32Exception or IOException) &&
            !exception.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return await read(shim);
        }
    }

    private async Task<(string? AccountJson, string RateLimitsJson)> ReadAccountAndRateLimitsAsync(string executable, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(ProcessTimeout);

        using var process = new Process
        {
            StartInfo = ProcessHelpers.CreateStartInfo(executable, "app-server", "--stdio"),
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await WriteJsonLineAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "ai-usage-dock", title = "AI Usage Dock", version = "0.1.0" },
                    capabilities = new { },
                },
            });

            await WaitForResultAsync(process, 1, linked.Token);
            await WriteJsonLineAsync(process, new { method = "initialized", @params = new { } });
            await WriteJsonLineAsync(process, new { id = 2, method = "account/read", @params = new { } });
            await WriteJsonLineAsync(process, new { id = 3, method = "account/rateLimits/read", @params = (object?)null });

            // The account call is best-effort: an older app-server without it must not
            // take the rate limits down with it.
            string? accountJson;
            try
            {
                accountJson = await WaitForResultAsync(process, 2, linked.Token);
            }
            catch (InvalidOperationException)
            {
                accountJson = null;
            }

            return (accountJson, await WaitForResultAsync(process, 3, linked.Token));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result.Trim() : string.Empty;
            throw new InvalidOperationException(
                string.IsNullOrEmpty(stderr) ? exception.Message : $"{exception.Message}: {FirstLine(stderr)}",
                exception);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            ProcessHelpers.TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        }
    }

    private static async Task WriteJsonLineAsync(Process process, object value)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<string> WaitForResultAsync(Process process, int wantedId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException($"Codex app-server closed before response {wantedId}");
            }

            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var idValue) || idValue != wantedId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var messageText = TryGetString(error, "message") ?? "unknown error";
                throw new InvalidOperationException($"Codex app-server: {messageText}");
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
            {
                throw new InvalidOperationException("Codex app-server returned no result");
            }

            return result.GetRawText();
        }
    }

    private UsageResult KeepLastOrFail(UsageFailureReason reason, string message)
    {
        if (_cached?.Snapshot is { } snapshot && snapshot.Windows.Any(window => window.ResetsAt is null || window.ResetsAt > DateTimeOffset.UtcNow))
        {
            var stale = snapshot with { IsStale = true };
            return _cached = UsageResult.Success(stale, message);
        }

        return _cached = UsageResult.Failure(reason, message);
    }

    private static string FirstLine(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;

    private static bool IsFresh(UsageSnapshot? snapshot) =>
        snapshot is not null && DateTimeOffset.UtcNow - snapshot.FetchedAt < CacheLifetime;

    public void Dispose() => _refreshLock.Dispose();
}

