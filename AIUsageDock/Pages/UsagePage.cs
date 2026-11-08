using AIUsageDock.Models;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Pages;

/// <summary>
/// A provider's detail page. It draws the last reading immediately and refreshes in
/// the background; when the refresh brings something new (or fails) it redraws.
/// </summary>
internal sealed class UsagePage : ContentPage
{
    // A provider whose cache is still fresh answers almost at once; waiting this long
    // lets that case draw its final state without a "Refreshing" flash and redraw.
    private static readonly TimeSpan FastAnswer = TimeSpan.FromMilliseconds(150);

    // GetContent runs again after every redraw; do not start another read right after one ended.
    private static readonly TimeSpan RedrawGrace = TimeSpan.FromSeconds(5);

    private readonly IUsageProvider _provider;
    private readonly Func<BarColorMode> _barColors;
    private readonly Action? _onRefreshed;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private Task? _refresh;
    private string? _refreshError;
    private UsageSnapshot? _lastGood;
    private DateTimeOffset _lastRefreshEnded = DateTimeOffset.MinValue;

    // What the page last drew, so a finished refresh redraws only when it would change.
    private UsageResult? _drawnResult;
    private string? _drawnError;
    private bool _drewRefreshing;
    private bool _drawPending;

    /// <param name="settings">The extension settings form, reachable from the page's
    /// command bar so a page opened from the Dock is one step from its options.</param>
    /// <param name="onRefreshed">Called after a background refresh finishes, so the
    /// provider's Dock band can show the same reading.</param>
    public UsagePage(
        IUsageProvider provider,
        Func<BarColorMode>? barColors = null,
        ICommand? settings = null,
        Action? onRefreshed = null,
        Func<DateTimeOffset>? clock = null)
    {
        _provider = provider;
        _barColors = barColors ?? (() => BarColorMode.Status);
        _onRefreshed = onRefreshed;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Id = $"aiusagedock.page.{provider.Id}";
        Name = $"{provider.DisplayName} usage";
        Title = $"{provider.DisplayName} usage";
        Icon = ProviderIcons.Filled(provider);

        var commands = new List<IContextItem>
        {
            new CommandContextItem(new Commands.RefreshPageCommand(() => BeginRefresh(force: true), () => RaiseItemsChanged()))
            {
                Title = "Refresh",
                Subtitle = $"Read {provider.DisplayName} usage again now",
                Icon = new IconInfo(""),
            },
        };

        if (settings is not null)
        {
            commands.Add(new CommandContextItem(settings)
            {
                Title = "Settings",
                Subtitle = "Providers, Dock values, API key and bar colors",
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
                return _refresh is { IsCompleted: false } ? _refresh : null;
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
                // RefreshAsync records its own errors; a faulted task just means draw now.
            }

            template = BuildCard();
        }
        finally
        {
            bool missedFinish;
            lock (_gate)
            {
                _drawPending = false;

                // The read can finish after BuildCard drew "Refreshing…" but while this
                // draw was still pending, when RefreshAsync leaves the redraw to us.
                missedFinish = _drewRefreshing && _refresh is { IsCompleted: true };
            }

            if (missedFinish)
            {
                RaiseItemsChanged();
            }
        }

        return
        [
            new FormContent
            {
                TemplateJson = template,
                DataJson = "{}",
            },
        ];
    }

    internal string BuildCard()
    {
        bool refreshing;
        string? refreshError;
        var current = _provider.LastResult;
        lock (_gate)
        {
            refreshing = _refresh is { IsCompleted: false };
            refreshError = _refreshError;
            _drawnResult = current;
            _drawnError = refreshError;
            _drewRefreshing = refreshing;
        }

        var notices = new List<(string Text, string Style)>();
        if (refreshing)
        {
            notices.Add(("Refreshing…", "Default"));
        }
        else if (refreshError is not null)
        {
            notices.Add(($"Refresh failed: {refreshError}", "Attention"));
        }

        var result = current;
        if (result?.Snapshot is { } snapshot)
        {
            lock (_gate)
            {
                _lastGood = snapshot;
            }
        }
        else
        {
            // Keep showing the last good reading under the failure notice rather than
            // replacing it with an error page.
            UsageSnapshot? lastGood;
            lock (_gate)
            {
                lastGood = _lastGood;
            }

            if (lastGood is not null)
            {
                result = UsageResult.Success(lastGood with { IsStale = true });
            }
            else if (result is null)
            {
                return refreshError is null
                    ? UsageCard.BuildLoading(_provider.DisplayName)
                    : UsageCard.Build(UsageResult.Failure(UsageFailureReason.ProcessFailed, refreshError), _provider.DisplayName, _provider.Id, _clock(), _barColors());
            }
            else
            {
                // A failure page explains the error itself; no extra notice needed.
                notices.RemoveAll(notice => notice.Style == "Attention");
            }
        }

        return UsageCard.Build(result, _provider.DisplayName, _provider.Id, _clock(), _barColors(), notices);
    }

    /// <summary>Starts a read unless one is already running. A non-forced read only
    /// does I/O when the provider's own cache says the data is due.</summary>
    internal void BeginRefresh(bool force)
    {
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false })
            {
                return;
            }

            var before = _provider.LastResult;
            _refresh = Task.Run(() => RefreshAsync(before, force));
        }
    }

    private async Task RefreshAsync(UsageResult? before, bool force)
    {
        UsageResult? after = null;
        string? error;
        try
        {
            after = await _provider.GetAsync(force).ConfigureAwait(false);
            error = after.Snapshot is null ? after.Message ?? after.FailureReason.ToString() : null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        bool redraw;
        lock (_gate)
        {
            _refreshError = error;
            _lastRefreshEnded = _clock();

            // A cache hit returns the same result object, so nothing changes on screen.
            // A GetContent call still waiting on this read draws the outcome itself.
            var changed = !ReferenceEquals(_drawnResult, after) || _drawnError != error || _drewRefreshing;
            redraw = changed && !_drawPending;
        }

        if (!ReferenceEquals(before, after))
        {
            _onRefreshed?.Invoke();
        }

        if (redraw)
        {
            RaiseItemsChanged();
        }
    }
}
