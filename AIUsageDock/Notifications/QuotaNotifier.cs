using System.Security;
using AIUsageDock.Models;
using AIUsageDock.Pages;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AIUsageDock.Notifications;

internal interface IToastSink
{
    void Show(string title, string body);
}

/// <summary>Windows toast notifications, sent under the extension's package identity.</summary>
internal sealed class WindowsToastSink : IToastSink
{
    public void Show(string title, string body)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
        }
        catch
        {
            // Notifications can be off, or the process may run without package identity
            // (tests, an unpackaged debug run). A missed toast must not break a refresh.
        }
    }
}

/// <summary>
/// Sends a notification when a usage window crosses the high or critical threshold, or
/// runs out. Each window notifies once per level and again only after it has dropped
/// back (its reset) and climbed once more.
/// </summary>
internal sealed class QuotaNotifier(IToastSink sink)
{
    private readonly Dictionary<string, int> _levels = [];
    private readonly object _gate = new();

    public void Observe(string providerName, string providerId, UsageSnapshot snapshot, DisplayOptions options, DateTimeOffset now)
    {
        // A stale reading describes an earlier moment; warning about it again would mislead.
        if (snapshot.IsStale)
        {
            return;
        }

        foreach (var window in snapshot.Windows)
        {
            if (window.IsLocked)
            {
                continue;
            }

            var level = Level(window, options);
            var key = $"{providerId}|{window.Kind}|{window.Label}";
            bool rose;
            lock (_gate)
            {
                rose = level > _levels.GetValueOrDefault(key);
                _levels[key] = level;
            }

            if (rose)
            {
                var (title, body) = Describe(providerName, window, level, options, now);
                sink.Show(title, body);
            }
        }
    }

    /// <summary>0 healthy, 1 high, 2 critical, 3 exhausted.</summary>
    internal static int Level(UsageWindow window, DisplayOptions options) =>
        (int)Math.Round(window.RemainingPercent) <= 0 ? 3 : UsageCard.Tier(window, options);

    internal static (string Title, string Body) Describe(string providerName, UsageWindow window, int level, DisplayOptions options, DateTimeOffset now)
    {
        var state = level switch
        {
            3 => "is used up",
            2 => "is almost out",
            _ => "is running low",
        };
        var parts = new List<string> { PanelText.Percent(window, options.ShowUsed) };
        if (PanelText.Reset(window, now, options.RelativeResets) is { } reset)
        {
            parts.Add(reset);
        }

        return ($"{providerName} · {window.Label} {state}", string.Join(" · ", parts));
    }
}
