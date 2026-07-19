using System.Diagnostics;
using SharedMemoryStore;

internal static class WorkerStoreOpenPolicy
{
    private static readonly TimeSpan OpenBudget = TimeSpan.FromSeconds(10);

    internal static StoreOpenStatus TryOpen(
        in SharedMemoryStoreOptions options,
        out MemoryStore? store)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            TimeSpan remaining = OpenBudget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                store = null;
                return StoreOpenStatus.StoreBusy;
            }

            StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
                options,
                new StoreWaitOptions(remaining),
                out store);
            if (status != StoreOpenStatus.StoreBusy)
            {
                return status;
            }

            store?.Dispose();
            store = null;
            Thread.Sleep(1);
        }
    }
}
