using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Allocation-free, operation-wide deadline and cancellation probe for layout-v2
/// work. A single value is created at the public engine boundary and passed by
/// readonly reference through every variable-length scan and retry loop.
/// </summary>
internal readonly struct LockFreeOperationBudget
{
    // Clock/token reads on every mapped record are measurably expensive. Sixty-four
    // records is still a very small cancellation quantum and keeps large-table
    // scans comfortably inside the public limit-plus-250-ms qualification bound.
    private const int ProbeMask = 63;
    private static readonly StoreWaitOptions PostOwnershipCleanupOptions =
        new(TimeSpan.FromMilliseconds(250));

    private readonly StoreWaitOptions _options;
    private readonly long _started;
    private readonly bool _fullStructuralScan;

    private LockFreeOperationBudget(
        in StoreWaitOptions options,
        long started,
        bool fullStructuralScan = false)
    {
        _options = options;
        _started = started;
        _fullStructuralScan = fullStructuralScan;
    }

    internal static LockFreeOperationBudget Start(in StoreWaitOptions options) =>
        new(options, Stopwatch.GetTimestamp());

    internal static LockFreeOperationBudget Start(
        in StoreWaitOptions options,
        long started) => new(options, started);

    /// <summary>
    /// Once an exact CAS has removed caller ownership, public convenience
    /// operations may spend at most the documented completion allowance on
    /// physical unlink/recycle. Expiry leaves only universally helpable state.
    /// </summary>
    internal static LockFreeOperationBudget StartPostOwnershipCleanup() =>
        Start(PostOwnershipCleanupOptions);

    /// <summary>
    /// Internal protocol/test calls without a public wait policy remain bounded by
    /// their structural scan sizes but have no deadline or cancellation source.
    /// </summary>
    internal static LockFreeOperationBudget StructuralAttempt { get; } =
        new(StoreWaitOptions.NoWait, started: 0, fullStructuralScan: true);

    internal static LockFreeOperationBudget UnboundedScan { get; } =
        Start(StoreWaitOptions.Infinite, started: 0);

    internal bool IsInfinite => _options.IsInfinite;

    internal bool IsNoWait => _options.Timeout == TimeSpan.Zero;

    internal CancellationToken CancellationToken => _options.CancellationToken;

    internal StoreStatus TryGetRemainingWaitOptions(out StoreWaitOptions remaining)
    {
        remaining = default;
        if (_options.CancellationToken.IsCancellationRequested)
        {
            return StoreStatus.OperationCanceled;
        }

        if (_options.IsInfinite || _options.Timeout == TimeSpan.Zero)
        {
            remaining = _options;
            return StoreStatus.Success;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_started);
        if (elapsed >= _options.Timeout)
        {
            return StoreStatus.StoreBusy;
        }

        remaining = new StoreWaitOptions(
            _options.Timeout - elapsed,
            _options.CancellationToken);
        return StoreStatus.Success;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StoreStatus Check()
    {
        if (_options.CancellationToken.IsCancellationRequested)
        {
            return StoreStatus.OperationCanceled;
        }

        // NoWait permits one minimum safe structural attempt. It is bounded by
        // the caller's scan/retry path rather than by an already-expired clock.
        if (_options.IsInfinite || _options.Timeout == TimeSpan.Zero)
        {
            return StoreStatus.Success;
        }

        return Stopwatch.GetElapsedTime(_started) >= _options.Timeout
            ? StoreStatus.StoreBusy
            : StoreStatus.Success;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StoreStatus CheckPeriodic(int iteration)
    {
        if ((iteration & ProbeMask) != 0)
        {
            return StoreStatus.Success;
        }

        StoreStatus status = Check();
        if (status != StoreStatus.Success)
        {
            return status;
        }

        // NoWait gets one bounded probe chunk, not an arbitrarily large
        // layout-sized scan. Nested scans receive the same rule, bounding a
        // minimum safe attempt without requiring mutable/shared budget state.
        return IsNoWait && !_fullStructuralScan && iteration != 0
            ? StoreStatus.StoreBusy
            : StoreStatus.Success;
    }

    /// <summary>
    /// Decides whether a transient StoreBusy result should be retried. Infinite
    /// waits keep helping until a non-transient result while still observing an
    /// explicit cancellation token; NoWait never starts another attempt.
    /// </summary>
    internal bool TryContinueAfterContention(int attempt, out StoreStatus terminalStatus)
    {
        terminalStatus = Check();
        if (terminalStatus != StoreStatus.Success)
        {
            return false;
        }

        if (IsNoWait)
        {
            terminalStatus = StoreStatus.StoreBusy;
            return false;
        }

        Thread.SpinWait(4 << Math.Min(attempt, 10));
        if ((attempt & 63) == 63)
        {
            Thread.Yield();
        }

        return true;
    }
}
