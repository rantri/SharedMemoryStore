using System.Threading;
using SharedMemoryStore.Layout;
using SharedMemoryStore.Leasing;
using SharedMemoryStore.Slots;

namespace SharedMemoryStore
{
    /// <summary>
    /// Options for explicit stale reservation recovery.
    /// </summary>
    /// <param name="RecoverCurrentProcessReservations">When true, current-process pending reservations may be recovered for tests and controlled shutdown.</param>
    public readonly record struct ReservationRecoveryOptions(bool RecoverCurrentProcessReservations);

    /// <summary>
    /// Summary returned by explicit stale reservation recovery.
    /// </summary>
    /// <param name="ScannedReservationCount">The number of pending reservation slots inspected.</param>
    /// <param name="RecoveredReservationCount">The number of stale reservations reclaimed.</param>
    /// <param name="ActiveReservationCount">The number of pending reservations still owned by live producers.</param>
    /// <param name="UnsupportedReservationCount">The number of reservations whose owner liveness could not be evaluated safely.</param>
    /// <param name="FailedRecoveryCount">The number of reservations whose slot or index state prevented safe recovery.</param>
    public readonly record struct ReservationRecoveryReport(
        int ScannedReservationCount,
        int RecoveredReservationCount,
        int ActiveReservationCount,
        int UnsupportedReservationCount,
        int FailedRecoveryCount);
}

namespace SharedMemoryStore.Ingest
{
    internal static class ReservationRecovery
    {
        public static StoreStatus Recover(
            StoreLayout layout,
            ReusableSlotTable slots,
            SharedKeyIndex index,
            in ReservationRecoveryOptions options,
            out ReservationRecoveryReport report)
        {
            var scanned = 0;
            var recovered = 0;
            var active = 0;
            var unsupported = 0;
            var failed = 0;

            for (var i = 0; i < layout.SlotCount; i++)
            {
                ref var slot = ref slots.GetSlot(i);
                if (Volatile.Read(ref slot.State) != LayoutConstants.SlotPublishing)
                {
                    continue;
                }

                scanned++;
                var owner = LeaseOwnerClassifier.Classify(slot.PublisherProcessId);
                switch (owner.Kind)
                {
                    case LeaseOwnerKind.Unsupported:
                        unsupported++;
                        continue;
                    case LeaseOwnerKind.UnsafeRecord:
                        failed++;
                        continue;
                }

                if (!owner.IsRecoverable(options.RecoverCurrentProcessReservations))
                {
                    active++;
                    continue;
                }

                var lifecycleId = SlotLifecycleId.FromSlot(slot);
                if (!index.TryRemoveSlot(i, lifecycleId, slot.KeyHash))
                {
                    failed++;
                    continue;
                }

                if (slots.Reclaim(i) != StoreStatus.Success)
                {
                    failed++;
                    continue;
                }

                recovered++;
            }

            report = new ReservationRecoveryReport(scanned, recovered, active, unsupported, failed);
            return StoreStatus.Success;
        }
    }
}
