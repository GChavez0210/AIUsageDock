using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Pages;

/// <summary>
/// Every enabled provider on one page, like the "All" tab of Win-CodexBar's tray
/// panel. It draws the last readings at once and refreshes in the background.
/// </summary>
internal sealed class OverviewPage : ContentPage
{
    // Cached readings answer almost at once; waiting this long lets that case draw
    // its final state without a "Refreshing" flash and redraw.
    private static readonly TimeSpan FastAnswer = TimeSpan.FromMilliseconds(150);

    // GetContent runs again after every redraw; do not start another read right after one ended.
    private static readonly TimeSpan RedrawGrace = TimeSpan.FromSeconds(5);

    private readonly Func<IReadOnlyList<OverviewEntry>> _entries;
    private readonly Func<bool, Task> _refresh;
    private readonly Func<DisplayOptions> _display;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly FormContent _content = new() { DataJson = "{}" };
    private Task? _pending;
    private string? _refreshError;
    private DateTimeOffset _lastRefreshEnded = DateTimeOffset.MinValue;
    private bool _inFlight;
    private bool _drewRefreshing;
    private bool _drawPending;

    /// <param name="entries">The enabled providers with their last readings; does no I/O.</param>
    /// <param name="refresh">Reads every enabled provider; the argument forces a live read.</param>
    public OverviewPage(
        Func<IReadOnlyList<OverviewEntry>> entries,
        Func<bool, Task> refresh,
        Func<DisplayOptions>? display = null,
        ICommand? settings = null,
        Func<DateTimeOffset>? clock = null)
    {
        _entries = entries;
        _refresh = refresh;
        _display = display ?? (() => DisplayOptions.Default);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Id = "aiusagedock.page.overview";
        Name = "AI usage overview";
        Title = "AI usage overview";
        Icon = IconHelpers.FromRelativePath("Assets/Square44x44Logo.png");

        var commands = new List<IContextItem>
        {
            new CommandContextItem(new Commands.RefreshPageCommand(() => BeginRefresh(force: true), Redraw))
            {
                Title = "Refresh",
                Subtitle = "Read every provider's usage again now",
                Icon = new IconInfo(""),
            },
        };

        if (settings is not null)
        {
            commands.Add(new CommandContextItem(settings)
            {
                Title = "Settings",
                Subtitle = "Providers, Dock values, API key and display options",
                Icon = new IconInfo(""),
            });
        }

        Commands = [.. commands];
    }

    /// <summary>The background refresh in flight, if any; exposed for tests.</summary>
    internal Task? PendingRefresh
    {
        get
        {
            lock (_gate)
            {
                return _inFlight ? _pending : null;
            }
        }
    }

    public override IContent[] GetContent()
    {
        bool recentlyRefreshed;
        lock (_gate)
        {
            recentlyRefreshed = _clock() - _lastRefreshEnded < RedrawGrace;
            _drawPending = true;
        }

        string template;
        try
        {
            if (!recentlyRefreshed)
            {
                BeginRefresh(force: false);
            }

            try
            {
                PendingRefresh?.Wait(FastAnswer);
            }
            catch (AggregateException)
            {
                // The refresh records its own error; a faulted task just means draw now.
            }

            template = BuildCard();
        }
        finally
        {
            lock (_gate)
            {
                _drawPending = false;
            }
        }

        // The read can finish after BuildCard drew "Refreshing…" while this draw was
        // still pending, in which case the refresh left the redraw to us.
        bool missedFinish;
        lock (_gate)
        {
            missedFinish = _drewRefreshing && !_inFlight;
        }

        if (missedFinish)
        {
            template = BuildCard();
        }

        _content.TemplateJson = template;
        return [_content];
    }

    internal string BuildCard()
    {
        bool refreshing;
        string? error;
        lock (_gate)
        {
            refreshing = _inFlight;
            error = _refreshError;
            _drewRefreshing = refreshing;
        }

        var notices = new List<(string Text, string Style)>();
        if (refreshing)
        {
            notices.Add(("Refreshing…", "Default"));
        }
        else if (error is not null)
        {
            notices.Add(($"Refresh failed: {error}", "Attention"));
        }

        return UsageCard.BuildOverview(_entries(), _clock(), _display(), notices);
    }

    internal void BeginRefresh(bool force)
    {
        lock (_gate)
        {
            if (_inFlight)
            {
                return;
            }

            _inFlight = true;
            _pending = Task.Run(() => RefreshAsync(force));
        }
    }

    private async Task RefreshAsync(bool force)
    {
        string? error = null;
        try
        {
            await _refresh(force).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        bool redraw;
        lock (_gate)
        {
            _inFlight = false;
            _refreshError = error;
            _lastRefreshEnded = _clock();

            // A GetContent call still waiting on this read draws the outcome itself.
            redraw = !_drawPending;
        }

        if (redraw)
        {
            Redraw();
        }
    }

    private void Redraw()
    {
        _content.TemplateJson = BuildCard();
        RaiseItemsChanged();
    }
}
