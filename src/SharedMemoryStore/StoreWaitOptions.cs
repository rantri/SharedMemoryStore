using System.Diagnostics;
using System.Threading;

namespace SharedMemoryStore;

/// <summary>
/// Bounds work performed by public store operations. Legacy stores apply the bound to shared
/// synchronization; lock-free stores apply it to local retry, revalidation, helping, and backoff.
/// </summary>
/// <param name="Timeout">
/// Maximum operation wait/work bound, <see cref="TimeSpan.Zero"/> for the minimum safe attempt,
/// or <see cref="Timeout.InfiniteTimeSpan"/> for an explicit unbounded local retry policy.
/// </param>
/// <param name="CancellationToken">
/// Optional token that cancels the operation when observed before its ordering point.
/// </param>
public readonly record struct StoreWaitOptions(TimeSpan Timeout, CancellationToken CancellationToken = default)
{
    /// <summary>Gets the production default wait policy: one second and no cancellation token.</summary>
    public static StoreWaitOptions Default { get; } = new(TimeSpan.FromSeconds(1));

    /// <summary>Gets a policy that performs only the minimum safe attempt without optional waiting.</summary>
    public static StoreWaitOptions NoWait { get; } = new(TimeSpan.Zero);

    /// <summary>
    /// Gets an unbounded policy for intentional legacy blocking or lock-free local retry.
    /// </summary>
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
