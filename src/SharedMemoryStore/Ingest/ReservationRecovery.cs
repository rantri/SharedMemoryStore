using System.Diagnostics;
using System.Threading;
using SharedMemoryStore.Layout;
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
                var owner = slot.PublisherProcessId;
                var canRecover = CanRecoverOwner(owner, options.RecoverCurrentProcessReservations, out var isUnsupported);
                if (isUnsupported)
                {
                    unsupported++;
                    continue;
                }

                if (!canRecover)
                {
                    active++;
                    continue;
                }

                if (!index.TryRemoveSlot(i, slot.Generation))
                {
                    failed++;
                    continue;
                }

                slots.Reclaim(i);
                recovered++;
            }

            report = new ReservationRecoveryReport(scanned, recovered, active, unsupported, failed);
            return StoreStatus.Success;
        }

        private static bool CanRecoverOwner(int ownerProcessId, bool recoverCurrentProcessReservations, out bool unsupported)
        {
            unsupported = false;
            if (ownerProcessId <= 0)
            {
                return true;
            }

            if (ownerProcessId == Environment.ProcessId)
            {
                return recoverCurrentProcessReservations;
            }

            try
            {
                using var process = Process.GetProcessById(ownerProcessId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (PlatformNotSupportedException)
            {
                unsupported = true;
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch
            {
                unsupported = true;
                return false;
            }
        }
    }
}
