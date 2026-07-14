using System.Diagnostics;
using System.Globalization;

namespace SharedMemoryStore.LockFreeAgent;

/// <summary>
/// Emits no console output between the external go and done file markers so a
/// syscall tracer can isolate only the warmed layout-v2 steady-state interval.
/// </summary>
internal static class SteadyNoLockCommands
{
    private const int InvalidArgumentsExitCode = 64;
    private const int OperationFailureExitCode = 66;
    private const int TimeoutExitCode = 68;
    private const int WarmupIterations = 64;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    internal static int Run(string[] arguments)
    {
        if (!Arguments.TryParse(arguments, out Arguments parsed))
        {
            return InvalidArgumentsExitCode;
        }

        StoreOpenStatus open = MemoryStore.TryCreateOrOpen(parsed.Options, out MemoryStore? store);
        if (open != StoreOpenStatus.Success || store is null)
        {
            return OperationFailureExitCode;
        }

        using (store)
        {
            byte[] key = CreateKey();
            for (var iteration = 0; iteration < WarmupIterations; iteration++)
            {
                if (!RunCycle(store, key, iteration))
                {
                    return OperationFailureExitCode;
                }
            }

            File.WriteAllText(parsed.ReadyPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (!WaitForFile(parsed.GoPath))
            {
                return TimeoutExitCode;
            }

            var succeeded = true;
            for (var iteration = 0; iteration < parsed.Iterations; iteration++)
            {
                if (!RunCycle(store, key, checked(WarmupIterations + iteration)))
                {
                    succeeded = false;
                    break;
                }
            }

            // This write is the end marker. Do not add console/logging calls
            // above it: the parent traces the warmed go-to-done interval.
            File.WriteAllText(parsed.DonePath, succeeded ? "ok" : "failed");
            if (!succeeded)
            {
                Console.Error.WriteLine("A steady-no-lock operation returned an unexpected status.");
                return OperationFailureExitCode;
            }

            return 0;
        }
    }

    private static bool RunCycle(MemoryStore store, byte[] key, int iteration)
    {
        Span<byte> value = stackalloc byte[16];
        BitConverter.TryWriteBytes(value, iteration);
        BitConverter.TryWriteBytes(value[8..], ~((long)iteration));
        Span<byte> descriptor = stackalloc byte[8];
        BitConverter.TryWriteBytes(descriptor, unchecked(iteration * 31 + 7));

        if (!TryPublishEventually(store, key, value, descriptor)
            || store.TryAcquire(key, out ValueLease lease) != StoreStatus.Success)
        {
            return false;
        }

        bool contentMatches = lease.ValueSpan.SequenceEqual(value)
            && lease.DescriptorSpan.SequenceEqual(descriptor);
        if (lease.Release() != StoreStatus.Success || !contentMatches)
        {
            return false;
        }

        StoreStatus remove = store.TryRemove(key);
        if (remove is not (StoreStatus.Success or StoreStatus.RemovePending))
        {
            return false;
        }

        if (store.TryRecoverLeases(
                new LeaseRecoveryOptions(true),
                out LeaseRecoveryReport leaseReport) != StoreStatus.Success
            || leaseReport.RecoveredLeaseCount != 0
            || store.TryRecoverReservations(
                new ReservationRecoveryOptions(true),
                out ReservationRecoveryReport reservationReport) != StoreStatus.Success
            || reservationReport.RecoveredReservationCount != 0
            || store.TryGetDiagnostics(out DiagnosticsSnapshot diagnostics) != StoreStatus.Success
            || diagnostics.Profile != StoreProfile.LockFree)
        {
            return false;
        }

        return WaitUntilAbsent(store, key);
    }

    private static bool TryPublishEventually(
        MemoryStore store,
        byte[] key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(WaitTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (true)
        {
            StoreStatus publish = store.TryPublish(key, value, descriptor);
            if (publish == StoreStatus.Success)
            {
                return true;
            }

            if (publish is not (StoreStatus.DuplicateKey or StoreStatus.StoreFull or StoreStatus.StoreBusy))
            {
                return false;
            }

            _ = store.TryRemove(key);
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            spin.SpinOnce();
        }
    }

    private static bool WaitUntilAbsent(MemoryStore store, byte[] key)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(WaitTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (true)
        {
            StoreStatus acquire = store.TryAcquire(key, out ValueLease lease);
            if (acquire == StoreStatus.NotFound)
            {
                return true;
            }

            if (acquire == StoreStatus.Success)
            {
                _ = lease.Release();
            }
            else if (acquire is not (StoreStatus.StoreBusy or StoreStatus.LeaseTableFull))
            {
                return false;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            spin.SpinOnce();
        }
    }

    private static byte[] CreateKey() => [0x53, 0x4d, 0x53, 0x32, 0x4e, 0x4f, 0x4c, 0x4b];

    private static bool WaitForFile(string path)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(WaitTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (!File.Exists(path))
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            spin.SpinOnce();
        }

        return true;
    }

    private readonly record struct Arguments(
        SharedMemoryStoreOptions Options,
        int Iterations,
        string ReadyPath,
        string GoPath,
        string DonePath)
    {
        internal static bool TryParse(string[] arguments, out Arguments parsed)
        {
            parsed = default;
            if (arguments.Length != 12
                || string.IsNullOrWhiteSpace(arguments[1])
                || !TryPositive(arguments[2], out int slotCount)
                || !TryPositive(arguments[3], out int maxValueBytes)
                || !TryNonNegative(arguments[4], out int maxDescriptorBytes)
                || !TryPositive(arguments[5], out int maxKeyBytes)
                || !TryPositive(arguments[6], out int leaseRecordCount)
                || !TryPositive(arguments[7], out int participantRecordCount)
                || !TryPositive(arguments[8], out int iterations)
                || maxValueBytes < 16
                || maxDescriptorBytes < 8
                || maxKeyBytes < 8
                || iterations > 1_000_000
                || string.IsNullOrWhiteSpace(arguments[9])
                || string.IsNullOrWhiteSpace(arguments[10])
                || string.IsNullOrWhiteSpace(arguments[11]))
            {
                return false;
            }

            parsed = new Arguments(
                SharedMemoryStoreOptions.CreateLockFree(
                    arguments[1],
                    slotCount,
                    maxValueBytes,
                    maxDescriptorBytes,
                    maxKeyBytes,
                    leaseRecordCount,
                    participantRecordCount,
                    OpenMode.OpenExisting,
                    enableLeaseRecovery: true),
                iterations,
                arguments[9],
                arguments[10],
                arguments[11]);
            return true;
        }

        private static bool TryPositive(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

        private static bool TryNonNegative(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }
}
