namespace AIUsageDock.Models;

/// <summary>
/// How far usage runs ahead of or behind an even spend of the window. "Ahead" means
/// more is used than time has passed (a deficit); "behind" means a reserve.
/// </summary>
internal enum PaceStage
{
    OnTrack,
    SlightlyAhead,
    Ahead,
    FarAhead,
    SlightlyBehind,
    Behind,
    FarBehind,
}

/// <summary>
/// Compares how much of a window is used with how much of its time has passed.
/// Ported from Win-CodexBar's <c>UsagePace::weekly</c> (linear pace, no workday weighting).
/// </summary>
internal sealed record UsagePace(
    PaceStage Stage,
    double DeltaPercent,
    double ExpectedUsedPercent,
    double ActualUsedPercent,
    double? EtaSeconds,
    bool WillLastToReset)
{
    public bool IsDeficit => Stage is PaceStage.SlightlyAhead or PaceStage.Ahead or PaceStage.FarAhead;

    public bool IsReserve => Stage is PaceStage.SlightlyBehind or PaceStage.Behind or PaceStage.FarBehind;

    /// <summary>
    /// Null when the window has no reset time or length, has already reset, or reports
    /// a reset further away than its own length (the reading does not describe this window).
    /// </summary>
    public static UsagePace? For(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } resetsAt || window.DurationMinutes is not { } minutes || minutes <= 0)
        {
            return null;
        }

        if (window.IsLocked || !double.IsFinite(window.UsedPercent))
        {
            return null;
        }

        var duration = minutes * 60d;
        var untilReset = (resetsAt - now).TotalSeconds;
        if (untilReset <= 0 || untilReset > duration)
        {
            return null;
        }

        var elapsed = Math.Clamp(duration - untilReset, 0, duration);
        var expected = Math.Clamp(elapsed / duration * 100d, 0, 100);
        var actual = Math.Clamp(window.UsedPercent, 0, 100);

        // Usage with no time elapsed cannot be paced.
        if (elapsed == 0 && actual > 0)
        {
            return null;
        }

        var delta = actual - expected;
        double? eta = null;
        var lasts = false;

        if (actual >= 100)
        {
            eta = 0;
        }
        else if (elapsed > 0 && actual > 0)
        {
            var secondsToEmpty = (100 - actual) / (actual / elapsed);
            if (secondsToEmpty >= untilReset)
            {
                lasts = true;
            }
            else
            {
                eta = secondsToEmpty;
            }
        }
        else if (elapsed > 0)
        {
            lasts = true;
        }

        return new UsagePace(StageFor(delta), delta, expected, actual, eta, lasts);
    }

    internal static PaceStage StageFor(double delta)
    {
        var size = Math.Abs(delta);
        if (size <= 2)
        {
            return PaceStage.OnTrack;
        }

        if (size <= 6)
        {
            return delta >= 0 ? PaceStage.SlightlyAhead : PaceStage.SlightlyBehind;
        }

        if (size <= 12)
        {
            return delta >= 0 ? PaceStage.Ahead : PaceStage.Behind;
        }

        return delta >= 0 ? PaceStage.FarAhead : PaceStage.FarBehind;
    }
}
