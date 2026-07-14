using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharedMemoryStore;
using SharedMemoryStore.LockFreeAgent;

const int invalidArgumentsExitCode = 64;
const int unsupportedPlatformExitCode = 65;
const int unhandledFailureExitCode = 69;

if (args.Length == 1 && string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("READY "
        + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
        + " "
        + (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported"));
    return 0;
}

if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
    || RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    return unsupportedPlatformExitCode;
}

try
{
    return args.Length == 0
        ? invalidArgumentsExitCode
        : args[0] switch
        {
            "atomic-publication-producer" => AtomicCommands.RunPublicationProducer(args),
            "atomic-publication-consumer" => AtomicCommands.RunPublicationConsumer(args),
            "atomic-cas-worker" => AtomicCommands.RunCasWorker(args),
            "atomic-dekker-worker" => AtomicCommands.RunDekkerWorker(args),
            "atomic-dekker-coordinator" => AtomicCommands.RunDekkerCoordinator(args),
            "lease-read" => LeaseCommands.RunRead(args),
            "lease-hold" => LeaseCommands.RunHold(args),
            "churn-worker" => ChurnCommands.Run(args),
            "checkpoint-catalog" => CheckpointCatalogCommands.Run(args),
            "checkpoint-pause" => CheckpointCrashCommands.Run(args),
            "checkpoint-crash" => CheckpointCrashCommands.Run(args),
            "participant-orphan" => ParticipantOrphanCommands.Run(args),
            "raw-visibility-publisher" => RawVisibilityCommands.RunPublisher(args),
            "raw-visibility-reader" => RawVisibilityCommands.RunReader(args),
            "raw-visibility-remover" => RawVisibilityCommands.RunRemover(args),
            "steady-no-lock" => SteadyNoLockCommands.Run(args),
            "linux-file-lock-probe" => LinuxFileLockCommands.Run(args),
            _ => invalidArgumentsExitCode
        };
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
    return unhandledFailureExitCode;
}

internal static class ChurnCommands
{
    private const int OperationFailureExitCode = 66;
    private const int ContentMismatchExitCode = 67;
    private const int TimeoutExitCode = 68;
    private const int WarmupIterations = 128;
    private const int SampleWindow = 256;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public static int Run(string[] arguments)
    {
        if (arguments.Length != 13
            || !TryCreateOptions(arguments, out var options)
            || !int.TryParse(arguments[8], NumberStyles.None, CultureInfo.InvariantCulture, out var workerId)
            || workerId < 0
            || !int.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < SampleWindow * 2
            || iterations > 100_000_000
            || !TryParseKeys(arguments[10], options.MaxKeyBytes, out var keys))
        {
            return 64;
        }

        StoreOpenStatus openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
        if (openStatus != StoreOpenStatus.Success || store is null)
        {
            Console.Error.WriteLine("Open failed: " + openStatus);
            return OperationFailureExitCode;
        }

        using (store)
        {
            File.WriteAllText(arguments[11], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (!WaitForFile(arguments[12]))
            {
                return TimeoutExitCode;
            }

            var earlyPublish = new long[SampleWindow];
            var earlyMissing = new long[SampleWindow];
            var latePublish = new long[SampleWindow];
            var lateMissing = new long[SampleWindow];
            var value = new byte[8];
            var removePendingCount = 0;
            for (var operation = -WarmupIterations; operation < iterations; operation++)
            {
                byte[] key = keys[(operation + WarmupIterations) % keys.Length];
                BitConverter.TryWriteBytes(value, ((long)workerId << 32) | (uint)(operation + WarmupIterations));

                long started = Stopwatch.GetTimestamp();
                StoreStatus publish = store.TryPublish(key, value);
                long publishTicks = Stopwatch.GetTimestamp() - started;
                if (publish != StoreStatus.Success)
                {
                    Console.Error.WriteLine(
                        "Publish failed: " + publish
                        + " worker=" + workerId.ToString(CultureInfo.InvariantCulture)
                        + " operation=" + operation.ToString(CultureInfo.InvariantCulture)
                        + " key=" + Convert.ToHexString(key));
                    return OperationFailureExitCode;
                }

                StoreStatus acquire = store.TryAcquire(key, out var lease);
                if (acquire != StoreStatus.Success || !lease.ValueSpan.SequenceEqual(value))
                {
                    _ = lease.Release();
                    return ContentMismatchExitCode;
                }

                if (lease.Release() != StoreStatus.Success)
                {
                    return OperationFailureExitCode;
                }

                StoreStatus remove = store.TryRemove(key);
                if (remove is not (StoreStatus.Success or StoreStatus.RemovePending))
                {
                    Console.Error.WriteLine(
                        "Remove failed: " + remove
                        + " worker=" + workerId.ToString(CultureInfo.InvariantCulture)
                        + " operation=" + operation.ToString(CultureInfo.InvariantCulture)
                        + " key=" + Convert.ToHexString(key));
                    return OperationFailureExitCode;
                }

                // The default one-second operation policy permits
                // RemovePending after the logical removal ordering point when
                // bounded classification/reclaim work is incomplete. The
                // following NotFound check proves logical absence, and the
                // controller's final full-capacity fill proves eventual exact
                // reclamation. Preserve the count as qualification evidence.
                if (remove == StoreStatus.RemovePending && operation >= 0)
                {
                    removePendingCount++;
                }

                started = Stopwatch.GetTimestamp();
                StoreStatus missing = store.TryAcquire(key, out _);
                long missingTicks = Stopwatch.GetTimestamp() - started;
                if (missing != StoreStatus.NotFound)
                {
                    Console.Error.WriteLine("Missing lookup failed: " + missing);
                    return OperationFailureExitCode;
                }

                if (operation is >= 0 and < SampleWindow)
                {
                    earlyPublish[operation] = publishTicks;
                    earlyMissing[operation] = missingTicks;
                }
                else if (operation >= iterations - SampleWindow)
                {
                    int sample = operation - (iterations - SampleWindow);
                    latePublish[sample] = publishTicks;
                    lateMissing[sample] = missingTicks;
                }
            }

            var result = new ChurnResult(
                workerId,
                iterations,
                keys.Length,
                removePendingCount,
                Percentile99(earlyPublish),
                Percentile99(latePublish),
                Percentile99(earlyMissing),
                Percentile99(lateMissing));
            Console.WriteLine("RESULT " + JsonSerializer.Serialize(result));
            return 0;
        }
    }

    private static bool TryCreateOptions(string[] arguments, out SharedMemoryStoreOptions options)
    {
        options = null!;
        if (string.IsNullOrWhiteSpace(arguments[1])
            || !TryPositive(arguments[2], out var slotCount)
            || !TryPositive(arguments[3], out var maxValueBytes)
            || !TryNonNegative(arguments[4], out var maxDescriptorBytes)
            || !TryPositive(arguments[5], out var maxKeyBytes)
            || !TryPositive(arguments[6], out var leaseRecordCount)
            || !TryPositive(arguments[7], out var participantRecordCount))
        {
            return false;
        }

        options = SharedMemoryStoreOptions.CreateLockFree(
            arguments[1],
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount,
            OpenMode.OpenExisting,
            enableLeaseRecovery: true);
        return true;
    }

    private static bool TryParseKeys(string text, int maxKeyBytes, out byte[][] keys)
    {
        keys = [];
        try
        {
            keys = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(Convert.FromHexString)
                .ToArray();
            return keys.Length > 0 && keys.All(key => key.Length is > 0 && key.Length <= maxKeyBytes);
        }
        catch (FormatException)
        {
            keys = [];
            return false;
        }
    }

    private static long Percentile99(long[] samples)
    {
        Array.Sort(samples);
        int index = Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.99d) - 1);
        return samples[index];
    }

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

    private static bool TryPositive(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static bool TryNonNegative(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;

    private readonly record struct ChurnResult(
        int WorkerId,
        int Iterations,
        int CollisionKeyCount,
        int RemovePendingCount,
        long EarlyPublishP99Ticks,
        long LatePublishP99Ticks,
        long EarlyMissingP99Ticks,
        long LateMissingP99Ticks);
}

internal static class LeaseCommands
{
    private const int OperationFailureExitCode = 66;
    private const int ContentMismatchExitCode = 67;
    private const int TimeoutExitCode = 68;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public static int RunRead(string[] arguments)
    {
        if (!StoreArguments.TryParse(arguments, expectedLength: 14, out var parsed)
            || !TryDecode(arguments[8], out var key)
            || key.Length == 0
            || !TryDecode(arguments[9], out var expectedValue)
            || !TryDecode(arguments[10], out var expectedDescriptor)
            || !int.TryParse(arguments[11], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < 1 or > 1_000_000)
        {
            return 64;
        }

        StoreOpenStatus openStatus = MemoryStore.TryCreateOrOpen(parsed.Options, out var store);
        if (openStatus != StoreOpenStatus.Success || store is null)
        {
            Console.Error.WriteLine("Open failed: " + openStatus);
            return OperationFailureExitCode;
        }

        using (store)
        {
            File.WriteAllText(arguments[13], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (!WaitForFile(arguments[12]))
            {
                return TimeoutExitCode;
            }

            ulong checksum = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                StoreStatus acquireStatus = store.TryAcquire(key, out var lease);
                if (acquireStatus != StoreStatus.Success)
                {
                    Console.Error.WriteLine("Acquire failed: " + acquireStatus);
                    return OperationFailureExitCode;
                }

                if (!lease.ValueSpan.SequenceEqual(expectedValue)
                    || !lease.DescriptorSpan.SequenceEqual(expectedDescriptor)
                    || lease.ValueLength != expectedValue.Length
                    || lease.DescriptorLength != expectedDescriptor.Length)
                {
                    _ = lease.Release();
                    return ContentMismatchExitCode;
                }

                checksum = AddChecksum(checksum, lease.ValueSpan);
                checksum = AddChecksum(checksum, lease.DescriptorSpan);
                StoreStatus releaseStatus = lease.Release();
                if (releaseStatus != StoreStatus.Success)
                {
                    Console.Error.WriteLine("Release failed: " + releaseStatus);
                    return OperationFailureExitCode;
                }
            }

            Console.WriteLine(
                "OK lease-read iterations="
                + iterations.ToString(CultureInfo.InvariantCulture)
                + " checksum="
                + checksum.ToString(CultureInfo.InvariantCulture));
            return 0;
        }
    }

    public static int RunHold(string[] arguments)
    {
        if (!StoreArguments.TryParse(arguments, expectedLength: 13, out var parsed)
            || !TryDecode(arguments[8], out var key)
            || key.Length == 0
            || !TryDecode(arguments[9], out var expectedValue)
            || !TryDecode(arguments[10], out var expectedDescriptor))
        {
            return 64;
        }

        StoreOpenStatus openStatus = MemoryStore.TryCreateOrOpen(parsed.Options, out var store);
        if (openStatus != StoreOpenStatus.Success || store is null)
        {
            Console.Error.WriteLine("Open failed: " + openStatus);
            return OperationFailureExitCode;
        }

        using (store)
        {
            StoreStatus acquireStatus = store.TryAcquire(key, out var lease);
            if (acquireStatus != StoreStatus.Success)
            {
                Console.Error.WriteLine("Acquire failed: " + acquireStatus);
                return OperationFailureExitCode;
            }

            if (!lease.ValueSpan.SequenceEqual(expectedValue)
                || !lease.DescriptorSpan.SequenceEqual(expectedDescriptor))
            {
                _ = lease.Release();
                return ContentMismatchExitCode;
            }

            File.WriteAllText(arguments[11], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (!WaitForFile(arguments[12]))
            {
                _ = lease.Release();
                return TimeoutExitCode;
            }

            // The observer keeps using the borrowed spans after an arbitrary
            // pause; another reader must not own its progress or lifetime.
            if (!lease.ValueSpan.SequenceEqual(expectedValue)
                || !lease.DescriptorSpan.SequenceEqual(expectedDescriptor))
            {
                _ = lease.Release();
                return ContentMismatchExitCode;
            }

            StoreStatus releaseStatus = lease.Release();
            if (releaseStatus != StoreStatus.Success)
            {
                Console.Error.WriteLine("Release failed: " + releaseStatus);
                return OperationFailureExitCode;
            }

            Console.WriteLine("OK lease-hold released=1");
            return 0;
        }
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value == "-")
        {
            return true;
        }

        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

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

    private static ulong AddChecksum(ulong checksum, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            checksum = unchecked((checksum ^ value) * 1_099_511_628_211UL);
        }

        return checksum;
    }

    private readonly record struct StoreArguments(SharedMemoryStoreOptions Options)
    {
        public static bool TryParse(string[] arguments, int expectedLength, out StoreArguments parsed)
        {
            parsed = default;
            if (arguments.Length != expectedLength
                || string.IsNullOrWhiteSpace(arguments[1])
                || !TryPositive(arguments[2], out var slotCount)
                || !TryPositive(arguments[3], out var maxValueBytes)
                || !TryNonNegative(arguments[4], out var maxDescriptorBytes)
                || !TryPositive(arguments[5], out var maxKeyBytes)
                || !TryPositive(arguments[6], out var leaseRecordCount)
                || !TryPositive(arguments[7], out var participantRecordCount))
            {
                return false;
            }

            parsed = new StoreArguments(SharedMemoryStoreOptions.CreateLockFree(
                arguments[1],
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount,
                participantRecordCount,
                OpenMode.OpenExisting,
                enableLeaseRecovery: true));
            return true;
        }

        private static bool TryPositive(string value, out int result) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

        private static bool TryNonNegative(string value, out int result) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
    }
}

internal static class AtomicCommands
{
    private const int AtomicFailureExitCode = 66;
    private const int VisibilityFailureExitCode = 67;
    private const int TimeoutExitCode = 68;

    private const int SequenceOffset = 0;
    private const int ComplementOffset = 8;
    private const int PublicationOffset = 16;
    private const int AcknowledgementOffset = 24;
    private const int CasCounterOffset = 32;

    private const int DekkerReady0Offset = 0;
    private const int DekkerReady1Offset = 8;
    private const int DekkerPhaseOffset = 16;
    private const int DekkerDone0Offset = 24;
    private const int DekkerDone1Offset = 32;
    private const int DekkerWord0Offset = 40;
    private const int DekkerWord1Offset = 48;
    private const int DekkerSeen0Offset = 56;
    private const int DekkerSeen1Offset = 64;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public static int RunPublicationProducer(string[] arguments)
    {
        if (!TryParseArguments(arguments, expectedLength: 3, out var path, out var iterations))
        {
            return 64;
        }

        using var words = MappedAtomicWords.Open(path);
        if (!words.AreAligned(SequenceOffset, ComplementOffset, PublicationOffset, AcknowledgementOffset))
        {
            return AtomicFailureExitCode;
        }

        ref var sequence = ref words[SequenceOffset];
        ref var complement = ref words[ComplementOffset];
        ref var publication = ref words[PublicationOffset];
        ref var acknowledgement = ref words[AcknowledgementOffset];
        for (long iteration = 1; iteration <= iterations; iteration++)
        {
            if (!WaitForExact(ref acknowledgement, iteration - 1))
            {
                return TimeoutExitCode;
            }

            sequence = iteration;
            complement = ~iteration;
            Volatile.Write(ref publication, iteration);
        }

        Console.WriteLine("OK publication-producer " + iterations.ToString(CultureInfo.InvariantCulture) + " aligned=1");
        return 0;
    }

    public static int RunPublicationConsumer(string[] arguments)
    {
        if (!TryParseArguments(arguments, expectedLength: 3, out var path, out var iterations))
        {
            return 64;
        }

        using var words = MappedAtomicWords.Open(path);
        if (!words.AreAligned(SequenceOffset, ComplementOffset, PublicationOffset, AcknowledgementOffset))
        {
            return AtomicFailureExitCode;
        }

        ref var sequence = ref words[SequenceOffset];
        ref var complement = ref words[ComplementOffset];
        ref var publication = ref words[PublicationOffset];
        ref var acknowledgement = ref words[AcknowledgementOffset];
        for (long iteration = 1; iteration <= iterations; iteration++)
        {
            if (!WaitForExact(ref publication, iteration))
            {
                return TimeoutExitCode;
            }

            var observedSequence = sequence;
            var observedComplement = complement;
            if (observedSequence != iteration || observedComplement != ~iteration)
            {
                Console.Error.WriteLine(
                    "Publication mismatch at "
                    + iteration.ToString(CultureInfo.InvariantCulture)
                    + ": sequence="
                    + observedSequence.ToString(CultureInfo.InvariantCulture)
                    + ", complement="
                    + observedComplement.ToString(CultureInfo.InvariantCulture));
                return VisibilityFailureExitCode;
            }

            Volatile.Write(ref acknowledgement, iteration);
        }

        Console.WriteLine("OK publication-consumer " + iterations.ToString(CultureInfo.InvariantCulture) + " aligned=1");
        return 0;
    }

    public static int RunCasWorker(string[] arguments)
    {
        if (!TryParseArguments(arguments, expectedLength: 3, out var path, out var iterations))
        {
            return 64;
        }

        using var words = MappedAtomicWords.Open(path);
        if (!words.AreAligned(CasCounterOffset))
        {
            return AtomicFailureExitCode;
        }

        ref var counter = ref words[CasCounterOffset];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var spin = new SpinWait();
            while (true)
            {
                var observed = Volatile.Read(ref counter);
                if (Interlocked.CompareExchange(ref counter, checked(observed + 1), observed) == observed)
                {
                    break;
                }

                spin.SpinOnce();
            }
        }

        Console.WriteLine("OK cas-worker " + iterations.ToString(CultureInfo.InvariantCulture) + " aligned=1");
        return 0;
    }

    public static int RunDekkerWorker(string[] arguments)
    {
        if (arguments.Length != 4
            || !TryParsePositiveIterations(arguments[2], out var iterations)
            || !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var role)
            || role is < 0 or > 1)
        {
            return 64;
        }

        using var words = MappedAtomicWords.Open(arguments[1]);
        if (!words.AreAligned(
                DekkerReady0Offset,
                DekkerReady1Offset,
                DekkerPhaseOffset,
                DekkerDone0Offset,
                DekkerDone1Offset,
                DekkerWord0Offset,
                DekkerWord1Offset,
                DekkerSeen0Offset,
                DekkerSeen1Offset))
        {
            return AtomicFailureExitCode;
        }

        ref var ready = ref words[role == 0 ? DekkerReady0Offset : DekkerReady1Offset];
        ref var phase = ref words[DekkerPhaseOffset];
        ref var done = ref words[role == 0 ? DekkerDone0Offset : DekkerDone1Offset];
        ref var ownWord = ref words[role == 0 ? DekkerWord0Offset : DekkerWord1Offset];
        ref var otherWord = ref words[role == 0 ? DekkerWord1Offset : DekkerWord0Offset];
        ref var seen = ref words[role == 0 ? DekkerSeen0Offset : DekkerSeen1Offset];

        Volatile.Write(ref ready, 1);
        for (long iteration = 1; iteration <= iterations; iteration++)
        {
            if (!WaitForExact(ref phase, iteration))
            {
                return TimeoutExitCode;
            }

            Interlocked.Exchange(ref ownWord, 1);
            Volatile.Write(ref seen, Volatile.Read(ref otherWord));
            Volatile.Write(ref done, iteration);
        }

        Console.WriteLine(
            "OK dekker-worker role="
            + role.ToString(CultureInfo.InvariantCulture)
            + " iterations="
            + iterations.ToString(CultureInfo.InvariantCulture)
            + " aligned=1");
        return 0;
    }

    public static int RunDekkerCoordinator(string[] arguments)
    {
        if (!TryParseArguments(arguments, expectedLength: 3, out var path, out var iterations))
        {
            return 64;
        }

        using var words = MappedAtomicWords.Open(path);
        if (!words.AreAligned(
                DekkerReady0Offset,
                DekkerReady1Offset,
                DekkerPhaseOffset,
                DekkerDone0Offset,
                DekkerDone1Offset,
                DekkerWord0Offset,
                DekkerWord1Offset,
                DekkerSeen0Offset,
                DekkerSeen1Offset))
        {
            return AtomicFailureExitCode;
        }

        ref var ready0 = ref words[DekkerReady0Offset];
        ref var ready1 = ref words[DekkerReady1Offset];
        ref var phase = ref words[DekkerPhaseOffset];
        ref var done0 = ref words[DekkerDone0Offset];
        ref var done1 = ref words[DekkerDone1Offset];
        ref var word0 = ref words[DekkerWord0Offset];
        ref var word1 = ref words[DekkerWord1Offset];
        ref var seen0 = ref words[DekkerSeen0Offset];
        ref var seen1 = ref words[DekkerSeen1Offset];

        if (!WaitForExact(ref ready0, 1) || !WaitForExact(ref ready1, 1))
        {
            return TimeoutExitCode;
        }

        for (long iteration = 1; iteration <= iterations; iteration++)
        {
            if (!WaitForExact(ref done0, iteration - 1) || !WaitForExact(ref done1, iteration - 1))
            {
                return TimeoutExitCode;
            }

            Volatile.Write(ref word0, 0);
            Volatile.Write(ref word1, 0);
            Volatile.Write(ref seen0, -1);
            Volatile.Write(ref seen1, -1);
            Volatile.Write(ref phase, iteration);

            if (!WaitForExact(ref done0, iteration) || !WaitForExact(ref done1, iteration))
            {
                return TimeoutExitCode;
            }

            if (Volatile.Read(ref seen0) == 0 && Volatile.Read(ref seen1) == 0)
            {
                Console.Error.WriteLine("Forbidden Dekker outcome at iteration " + iteration.ToString(CultureInfo.InvariantCulture));
                return VisibilityFailureExitCode;
            }
        }

        Console.WriteLine("OK dekker-coordinator " + iterations.ToString(CultureInfo.InvariantCulture) + " forbidden=0 aligned=1");
        return 0;
    }

    private static bool TryParseArguments(
        string[] arguments,
        int expectedLength,
        out string path,
        out int iterations)
    {
        path = string.Empty;
        iterations = 0;
        if (arguments.Length != expectedLength || !TryParsePositiveIterations(arguments[2], out iterations))
        {
            return false;
        }

        path = arguments[1];
        return path.Length != 0;
    }

    private static bool TryParsePositiveIterations(string value, out int iterations)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            && iterations is > 0 and <= 1_000_000;
    }

    private static bool WaitForExact(ref long word, long expected)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(WaitTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (Volatile.Read(ref word) != expected)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            spin.SpinOnce();
        }

        return true;
    }
}

internal sealed unsafe class MappedAtomicWords : IDisposable
{
    public const int Length = 4096;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _pointer;
    private bool _disposed;

    private MappedAtomicWords(MemoryMappedFile mapping, MemoryMappedViewAccessor accessor, byte* pointer)
    {
        _mapping = mapping;
        _accessor = accessor;
        _pointer = pointer;
    }

    public ref long this[int byteOffset]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (byteOffset < 0 || byteOffset > Length - sizeof(long))
            {
                throw new ArgumentOutOfRangeException(nameof(byteOffset));
            }

            return ref *(long*)(_pointer + byteOffset);
        }
    }

    public static MappedAtomicWords Open(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? accessor = null;
        byte* pointer = null;
        try
        {
            if (stream.Length < Length)
            {
                throw new InvalidDataException("Atomic test mapping is shorter than the required control block.");
            }

            mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: Length,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);
            accessor = mapping.CreateViewAccessor(0, Length, MemoryMappedFileAccess.ReadWrite);
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += accessor.PointerOffset;
            var result = new MappedAtomicWords(mapping, accessor, pointer);
            mapping = null;
            accessor = null;
            return result;
        }
        catch
        {
            if (pointer is not null && accessor is not null)
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            accessor?.Dispose();
            mapping?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    public bool AreAligned(params int[] byteOffsets)
    {
        foreach (var byteOffset in byteOffsets)
        {
            if (byteOffset < 0
                || byteOffset > Length - sizeof(long)
                || ((nuint)(_pointer + byteOffset) & (sizeof(long) - 1)) != 0)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer is not null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _accessor.Dispose();
        _mapping.Dispose();
    }
}
