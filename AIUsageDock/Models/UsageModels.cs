namespace AIUsageDock.Models;

internal enum UsageWindowKind
{
    Session,
    Weekly,
    Monthly,
    Other,
}

internal sealed record UsageWindow(
    UsageWindowKind Kind,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    int? DurationMinutes = null,
    string? Severity = null,
    string? LockedReason = null)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);

    public bool IsLocked => !string.IsNullOrWhiteSpace(LockedReason);
}

/// <summary>
/// One label/value pair a provider reports about the signed-in account
/// (plan, email, organization, billing, credits, ...). Rendered in order.
/// </summary>
internal sealed record UsageDetail(string Label, string Value);

internal sealed record UsageSnapshot(
    string Provider,
    string? Plan,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset FetchedAt,
    string Source,
    bool IsStale = false,
    IReadOnlyList<UsageDetail>? Details = null,
    IReadOnlyList<string>? Notes = null)
{
    public IReadOnlyList<UsageDetail> AccountDetails => Details ?? [];

    public IReadOnlyList<string> AccountNotes => Notes ?? [];

    public UsageWindow? Session => Windows
        .Where(window => window.Kind == UsageWindowKind.Session)
        .OrderBy(window => window.RemainingPercent)
        .FirstOrDefault();

    public UsageWindow? Weekly => Windows
        .Where(window => window.Kind == UsageWindowKind.Weekly)
        .OrderBy(window => window.RemainingPercent)
        .FirstOrDefault();

    public UsageWindow? MostConstrained => Windows
        .OrderBy(window => window.RemainingPercent)
        .FirstOrDefault();
}

internal enum UsageFailureReason
{
    None,
    CliUnavailable,
    NotSignedIn,
    TimedOut,
    InvalidData,
    ProcessFailed,
}

internal sealed record UsageResult(
    UsageFailureReason FailureReason,
    UsageSnapshot? Snapshot = null,
    string? Message = null)
{
    public bool IsSuccess => Snapshot is not null;

    public static UsageResult Success(UsageSnapshot snapshot, string? message = null) =>
        new(UsageFailureReason.None, snapshot, message);

    public static UsageResult Failure(UsageFailureReason reason, string message) =>
        new(reason, null, message);
}

internal interface IUsageProvider : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    string IconName { get; }
    Task<UsageResult> GetAsync(bool force = false, CancellationToken cancellationToken = default);
}
