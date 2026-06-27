using System.Diagnostics;
using System.Threading;
using SharedMemoryStore.Layout;
using SharedMemoryStore.Slots;

namespace SharedMemoryStore.Leasing;

internal static class LeaseRecovery
{
    public static StoreStatus Recover(
        LeaseRegistry registry,
        ReusableSlotTable slots,
        SlotReclaimer reclaimer,
        bool enabled,
        in LeaseRecoveryOptions options,
        out LeaseRecoveryReport report)
    {
        var scanned = 0;
        var recovered = 0;
        var unsupported = 0;

        if (!enabled || !OperatingSystem.IsWindows())
        {
            report = new LeaseRecoveryReport(registry.RecordCount, 0, registry.RecordCount);
            return StoreStatus.UnsupportedPlatform;
        }

        for (var i = 0; i < registry.RecordCount; i++)
        {
            scanned++;
            ref var record = ref registry.GetRecord(i);
            if (Volatile.Read(ref record.State) != LayoutConstants.LeaseActive)
            {
                continue;
            }

            if (!options.RecoverCurrentProcessLeases && IsProcessAlive(record.OwnerProcessId))
            {
                continue;
            }

            ref var slot = ref slots.GetSlot(record.SlotIndex);
            if (slot.Generation != record.SlotGeneration)
            {
                unsupported++;
                continue;
            }

            Volatile.Write(ref record.State, LayoutConstants.LeaseAbandoned);
            var remaining = Interlocked.Decrement(ref slot.UsageCount);
            if (remaining == 0)
            {
                reclaimer.ReclaimAfterFinalRelease(record.SlotIndex, record.SlotGeneration);
            }

            recovered++;
        }

        report = new LeaseRecoveryReport(scanned, recovered, unsupported);
        return StoreStatus.Success;
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
