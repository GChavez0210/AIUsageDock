using AIUsageDock.Models;
using AIUsageDock.Providers;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class ProviderParserTests
{
    [Fact]
    public void CodexParserPrefersNamedCodexBucketAndClassifiesWindows()
    {
        const string json = """
        {
          "rateLimits": {
            "planType": "wrong",
            "primary": { "usedPercent": 99, "windowDurationMins": 300 }
          },
          "rateLimitsByLimitId": {
            "codex": {
              "limitId": "codex",
              "planType": "plus",
              "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1893456000 },
              "secondary": { "usedPercent": 40, "windowDurationMins": 10080, "resetsAt": 1893974400 }
            }
          }
        }
        """;

        var snapshot = CodexUsageProvider.ParseRateLimitsJson(json, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot);
        Assert.Equal("Plus", snapshot.Plan);
        Assert.Equal(75, snapshot.Session!.RemainingPercent);
        Assert.Equal(60, snapshot.Weekly!.RemainingPercent);
    }

    [Fact]
    public void CodexParserDoesNotInventMissingSessionWindow()
    {
        const string json = """
        {
          "rateLimits": {
            "planType": "plus",
            "primary": { "usedPercent": 31, "windowDurationMins": 10080, "resetsAt": 1893974400 }
          }
        }
        """;

        var snapshot = CodexUsageProvider.ParseRateLimitsJson(json, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Session);
        Assert.Equal(69, snapshot.Weekly!.RemainingPercent);
    }

    [Fact]
    public void ClaudeCacheParserUsesServiceReportedUtilization()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionReset = now.AddHours(2).ToString("O");
        var weeklyReset = now.AddDays(3).ToString("O");
        var json = $$"""
        {
          "cachedUsageUtilization": {
            "fetchedAtMs": {{now.ToUnixTimeMilliseconds()}},
            "utilization": {
              "five_hour": { "utilization": 18, "resets_at": "{{sessionReset}}" },
              "seven_day": { "utilization": 42, "resets_at": "{{weeklyReset}}" }
            }
          }
        }
        """;

        var snapshot = ClaudeUsageProvider.ParseCacheJson(json, now, out var fetchedAt);

        Assert.NotNull(snapshot);
        Assert.NotNull(fetchedAt);
        Assert.Equal(82, snapshot.Session!.RemainingPercent);
        Assert.Equal(58, snapshot.Weekly!.RemainingPercent);
    }

    [Fact]
    public void ClaudeCacheParserDropsExpiredBuckets()
    {
        var now = DateTimeOffset.UtcNow;
        var json = $$"""
        {
          "cachedUsageUtilization": {
            "fetchedAtMs": {{now.AddHours(-1).ToUnixTimeMilliseconds()}},
            "utilization": {
              "five_hour": { "utilization": 90, "resets_at": "{{now.AddMinutes(-1):O}}" },
              "seven_day": { "utilization": 20, "resets_at": "{{now.AddDays(2):O}}" }
            }
          }
        }
        """;

        var snapshot = ClaudeUsageProvider.ParseCacheJson(json, now, out _);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Session);
        Assert.Equal(80, snapshot.Weekly!.RemainingPercent);
    }

    [Fact]
    public void ClaudeLegacyUsageParserKeepsBindingWeeklyLimit()
    {
        const string resultText = """
        You are currently using your subscription to power your Claude Code usage

        Current session: 10% used · resets Sep 16, 3:09am (America/Denver)
        Current week (all models): 6% used · resets Sep 18, 1:59am (America/Denver)
        Current week (Opus): 35% used · resets Sep 18, 1:59am (America/Denver)
        """;
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            is_error = false,
            result = resultText,
            subtype = "success",
        });

        var snapshot = ClaudeUsageProvider.ParseUsageCommandJson(json, DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot);
        Assert.Equal(90, snapshot.Session!.RemainingPercent);
        Assert.Equal(65, snapshot.Weekly!.RemainingPercent);
        Assert.NotNull(snapshot.Session.ResetsAt);
        Assert.NotNull(snapshot.Weekly.ResetsAt);
    }

    [Fact]
    public void AntigravityParserKeepsTheMostConstrainedSharedLimits()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "num_turns": 0,
          "usage": { "total_tokens": 0 },
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Gemini Models",
                  "buckets": [
                    { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.75, "reset_time": "2030-01-08T00:00:00Z" },
                    { "id": "gemini-5h", "window": "5h", "remaining_fraction": 0.60, "reset_time": "2030-01-01T05:00:00Z" }
                  ]
                },
                {
                  "name": "Claude and GPT models",
                  "buckets": [
                    { "id": "3p-weekly", "window": "weekly", "remaining_fraction": 0.55, "reset_time": "2030-01-08T00:00:00Z" },
                    { "id": "3p-5h", "window": "5h", "remaining_fraction": 0.80, "reset_time": "2030-01-01T05:00:00Z" }
                  ]
                }
              ]
            }
          }
        }
        """;

        var snapshot = AntigravityUsageProvider.ParseUsageJson(json, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot);
        Assert.Equal(60, snapshot.Session!.RemainingPercent);
        Assert.Equal(55, snapshot.Weekly!.RemainingPercent);
        Assert.Equal(4, snapshot.Windows.Count);
    }

    [Fact]
    public void CodexParserMergesAccountAndCreditMetadata()
    {
        const string rateLimits = """
        {
          "rateLimits": {
            "limitId": "codex",
            "limitName": null,
            "planType": "plus",
            "primary": { "usedPercent": 88, "windowDurationMins": 300, "resetsAt": 1893456000 },
            "secondary": { "usedPercent": 34, "windowDurationMins": 10080, "resetsAt": 1893974400 },
            "credits": { "hasCredits": false, "unlimited": false, "balance": "0" },
            "rateLimitReachedType": "primary"
          }
        }
        """;
        const string account = """
        { "account": { "type": "chatgpt", "email": "dev@example.com", "planType": "plus" }, "requiresOpenaiAuth": true }
        """;

        var snapshot = CodexUsageProvider.ParseRateLimitsJson(rateLimits, DateTimeOffset.UnixEpoch, account);

        Assert.NotNull(snapshot);
        Assert.Equal("ChatGPT Plus", snapshot.Plan);
        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Account", Value: "dev@example.com" });
        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Sign-in", Value: "ChatGPT account" });
        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Credits", Value: "None" });
        Assert.Contains(snapshot.AccountNotes, note => note.Contains("Rate limit reached", StringComparison.Ordinal));
        Assert.Equal(300, snapshot.Session!.DurationMinutes);
    }

    [Fact]
    public void CodexParserSurvivesMissingAccountDocument()
    {
        const string rateLimits = """
        { "rateLimits": { "planType": "pro", "primary": { "usedPercent": 10, "windowDurationMins": 300 } } }
        """;

        var snapshot = CodexUsageProvider.ParseRateLimitsJson(rateLimits, DateTimeOffset.UnixEpoch, accountJson: null);

        Assert.NotNull(snapshot);
        Assert.Equal("Pro", snapshot.Plan);
        Assert.DoesNotContain(snapshot.AccountDetails, detail => detail.Label == "Account");
    }

    [Fact]
    public void ClaudeCacheParserReadsProfileExtraUsageAndPerModelWeeks()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionReset = now.AddHours(2).ToString("O");
        var weeklyReset = now.AddDays(3).ToString("O");
        var json = $$"""
        {
          "oauthAccount": {
            "emailAddress": "dev@example.com",
            "displayName": "Dev",
            "organizationName": "dev@example.com's Organization",
            "organizationType": "claude_max",
            "organizationRole": "admin",
            "billingType": "stripe_subscription",
            "organizationRateLimitTier": "default_claude_ai",
            "subscriptionCreatedAt": "2026-03-18T07:21:52Z",
            "seatTier": null
          },
          "cachedUsageUtilization": {
            "fetchedAtMs": {{now.ToUnixTimeMilliseconds()}},
            "utilization": {
              "five_hour": { "utilization": 18, "resets_at": "{{sessionReset}}", "locked_reason": null },
              "seven_day": { "utilization": 42, "resets_at": "{{weeklyReset}}", "locked_reason": null },
              "seven_day_opus": { "utilization": 70, "resets_at": "{{weeklyReset}}", "locked_reason": "org_policy" },
              "extra_usage": {
                "is_enabled": true, "monthly_limit": 5000, "used_credits": 1234,
                "currency": "USD", "decimal_places": 2, "spend_limit_reached": false
              },
              "limits": [
                { "kind": "session", "group": "session", "percent": 18, "severity": "normal", "resets_at": "{{sessionReset}}" },
                { "kind": "weekly_all", "group": "weekly", "percent": 42, "severity": "warning", "resets_at": "{{weeklyReset}}" }
              ]
            }
          }
        }
        """;

        var snapshot = ClaudeUsageProvider.ParseCacheJson(json, now, out _);

        Assert.NotNull(snapshot);
        Assert.Equal("Claude Max", snapshot.Plan);
        Assert.Equal(3, snapshot.Windows.Count);

        var allModels = Assert.Single(snapshot.Windows, window => window.Label == "Week (all models)");
        Assert.Equal("warning", allModels.Severity);
        Assert.Equal(58, allModels.RemainingPercent);

        var opus = Assert.Single(snapshot.Windows, window => window.Label == "Week (Opus)");
        Assert.True(opus.IsLocked);
        Assert.Equal(30, snapshot.Weekly!.RemainingPercent);

        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Plan", Value: "Claude Max" });
        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Account", Value: "Dev · dev@example.com" });
        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Billing", Value: "Stripe subscription" });
        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Extra usage", Value: "$12.34 of $50.00 monthly cap" });
        Assert.DoesNotContain(snapshot.AccountDetails, detail => detail.Label == "Organization");
        Assert.DoesNotContain(snapshot.AccountDetails, detail => detail.Label == "Rate limit tier");
    }

    [Fact]
    public void ClaudePlanNamesAreHumanReadable()
    {
        Assert.Equal("Claude Pro", ClaudeUsageProvider.DescribePlan("claude_pro", null));
        Assert.Equal("Claude Team (Premium)", ClaudeUsageProvider.DescribePlan("claude_team", "premium"));
        Assert.Null(ClaudeUsageProvider.DescribePlan(null, null));
    }

    [Fact]
    public void AntigravityParserAttachesActiveAccount()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                { "name": "Gemini Models", "buckets": [ { "id": "g5h", "window": "5h", "remaining_fraction": 0.5, "reset_time": "2030-01-01T05:00:00Z" } ] }
              ]
            }
          }
        }
        """;

        var snapshot = AntigravityUsageProvider.ParseUsageJson(json, DateTimeOffset.UnixEpoch, "dev@example.com");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Plan);
        Assert.Contains(snapshot.AccountDetails, detail => detail is { Label: "Account", Value: "dev@example.com" });
    }

    [Fact]
    public void AntigravityExplainsAModelTurnInsteadOfQuota()
    {
        const string json = """
        { "status": "SUCCESS", "response": "", "num_turns": 1, "denied_actions": [] }
        """;

        Assert.Null(AntigravityUsageProvider.ParseUsageJson(json, DateTimeOffset.UnixEpoch));
        Assert.Contains("model turn", AntigravityUsageProvider.DescribeMissingQuota(json), StringComparison.Ordinal);
    }
}
