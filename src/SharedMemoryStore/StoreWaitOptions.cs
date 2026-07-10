using System.Diagnostics;
using System.Threading;

namespace SharedMemoryStore;

/// <summary>
/// Selects how long public store operations may wait for shared synchronization.
/// </summary>
/// <param name="Timeout">
/// Maximum time to wait for shared synchronization, <see cref="TimeSpan.Zero"/> for no wait,
/// or <see cref="Timeout.InfiniteTimeSpan"/> for an explicit unbounded wait.
/// </param>
/// <param name="CancellationToken">Optional token that cancels the wait before synchronization is acquired.</param>
public readonly record struct StoreWaitOptions(TimeSpan Timeout, CancellationToken CancellationToken = default)
{
    /// <summary>Gets the production default wait policy: one second and no cancellation token.</summary>
    public static StoreWaitOptions Default { get; } = new(TimeSpan.FromSeconds(1));

    /// <summary>Gets a policy that returns immediately when shared synchronization is busy.</summary>
    public static StoreWaitOptions NoWait { get; } = new(TimeSpan.Zero);

    /// <summary>Gets a policy that waits indefinitely for callers that intentionally want legacy blocking behavior.</summary>
    public static StoreWaitOptions Infinite { get; } = new(System.Threading.Timeout.InfiniteTimeSpan);

    /// <summary>Gets a value indicating whether this policy waits indefinitely.</summary>
    public bool IsInfinite => Timeout == System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>Gets a value indicating whether the timeout is finite and non-negative, or explicitly infinite.</summary>
    public bool IsValid => IsInfinite || Timeout >= TimeSpan.Zero;

    internal StoreWaitOptions RemainingSince(long startTimestamp)
    {
        if (IsInfinite)
        {
            return this;
        }

        var remaining = Timeout - Stopwatch.GetElapsedTime(startTimestamp);
        return new StoreWaitOptions(
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            CancellationToken);
    }
}
