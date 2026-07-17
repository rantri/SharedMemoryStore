using System.Globalization;

namespace SharedMemoryStore.LockFreeAgent;

/// <summary>Creates one Active participant record and exits without Dispose.</summary>
internal static class ParticipantOrphanCommands
{
    internal static int Run(string[] arguments)
    {
        if (arguments.Length != 8
            || string.IsNullOrWhiteSpace(arguments[1])
            || !TryPositive(arguments[2], out int slotCount)
            || !TryPositive(arguments[3], out int maxValueBytes)
            || !TryNonNegative(arguments[4], out int maxDescriptorBytes)
            || !TryPositive(arguments[5], out int maxKeyBytes)
            || !TryPositive(arguments[6], out int leaseRecordCount)
            || !TryPositive(arguments[7], out int participantRecordCount))
        {
            return 64;
        }

        var options = SharedMemoryStoreOptions.Create(
            arguments[1],
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount,
            OpenMode.OpenExisting,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        if (status != StoreOpenStatus.Success || store is null)
        {
            Console.Error.WriteLine("Orphan open failed: " + status);
            return 66;
        }

        // Intentionally do not Dispose: process termination is the crash being
        // modeled, and the shared Active participant record must remain fenced
        // only by a later explicit recovery sweep.
        GC.KeepAlive(store);
        return 0;
    }

    private static bool TryPositive(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static bool TryNonNegative(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
}
