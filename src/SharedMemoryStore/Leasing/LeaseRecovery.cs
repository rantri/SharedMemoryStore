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
        var active = 0;
        var unsupported = 0;
        var failed = 0;

        if (!enabled)
        {
            report = new LeaseRecoveryReport(registry.RecordCount, 0, 0, registry.RecordCount, 0);
            return StoreStatus.UnsupportedPlatform;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            report = new LeaseRecoveryReport(registry.RecordCount, 0, 0, registry.RecordCount, 0);
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

            if ((uint)record.SlotIndex >= (uint)slots.SlotCount)
            {
                failed++;
                continue;
            }

            var owner = LeaseOwnerClassifier.Classify(record.OwnerProcessId);
            switch (owner.Kind)
            {
                case LeaseOwnerKind.Unsupported:
                    unsupported++;
                    continue;
                case LeaseOwnerKind.UnsafeRecord:
                    failed++;
                    continue;
            }

            if (!owner.IsRecoverable(options.RecoverCurrentProcessLeases))
            {
                active++;
                continue;
            }

            ref var slot = ref slots.GetSlot(record.SlotIndex);
            var lifecycleId = SlotLifecycleId.FromLease(record);
            if (!lifecycleId.IsValid
                || !lifecycleId.Matches(slot.Generation, slot.ReuseEpoch)
                || Volatile.Read(ref slot.UsageCount) <= 0)
            {
                failed++;
                continue;
            }

            Volatile.Write(ref record.State, LayoutConstants.LeaseAbandoned);
            var remaining = Interlocked.Decrement(ref slot.UsageCount);
            if (remaining == 0)
            {
                var reclaimStatus = reclaimer.ReclaimAfterFinalRelease(record.SlotIndex, lifecycleId);
                if (reclaimStatus != StoreStatus.Success)
                {
                    failed++;
                    continue;
                }
            }

            recovered++;
        }

        report = new LeaseRecoveryReport(scanned, recovered, active, unsupported, failed);
        return StoreStatus.Success;
    }
}
