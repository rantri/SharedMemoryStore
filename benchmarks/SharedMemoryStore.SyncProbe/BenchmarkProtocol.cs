using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharedMemoryStore;

internal enum OperationKind
{
    Acquire,
    Release,
    Publish,
    Remove,
    Reserve,
    Advance,
    Commit,
    RecoverLeases,
    RecoverReservations
}

internal sealed class StatusCounters
{
    private readonly long[,] _counts = new long[
        Enum.GetValues<OperationKind>().Length,
        Enum.GetValues<StoreStatus>().Length];
    private long _checksumFailures;
    private long _operations;
    private readonly Dictionary<string, long> _corruptReasons = new(StringComparer.Ordinal);

    internal long TotalOperations => _operations;

    internal void Record(OperationKind operation, StoreStatus status)
    {
        _counts[(int)operation, (int)status]++;
        _operations++;
    }

    internal void RecordChecksumFailure()
    {
        _checksumFailures++;
    }

    internal void RecordCorruptReason(string reason)
    {
        _corruptReasons.TryGetValue(reason, out long count);
        _corruptReasons[reason] = count + 1;
    }

    internal SortedDictionary<string, long> ToHistogram()
    {
        var histogram = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var operation in Enum.GetValues<OperationKind>())
        {
            foreach (var status in Enum.GetValues<StoreStatus>())
            {
                long count = _counts[(int)operation, (int)status];
                if (count != 0)
                {
                    histogram[operation + "." + status] = count;
                }
            }
        }

        if (_checksumFailures != 0)
        {
            histogram["Validation.ChecksumMismatch"] = _checksumFailures;
        }

        foreach ((string reason, long count) in _corruptReasons)
        {
            histogram["CorruptReason." + reason] = count;
        }

        return histogram;
    }
}

internal enum ProbeScenarioKind
{
    Autonomous,
    MixedChurn,
    BrokerDirected,
    LargeIngest,
    StickyOverflow
}

internal sealed record ScenarioPlan(
    string Name,
    ProbeScenarioKind Kind,
    int[] ProcessCounts,
    int PublisherCount = 0,
    int ObserverCount = 0);

internal static class ProbeScenarioCatalog
{
    private static readonly ScenarioPlan[] Sync =
    [
        new("acquire-release", ProbeScenarioKind.Autonomous, [1, 2, 4, 8, 12]),
        new("publish-remove", ProbeScenarioKind.Autonomous, [1, 2, 4, 8, 12])
    ];

    private static readonly ScenarioPlan[] Readers =
    [
        new("same-key-read", ProbeScenarioKind.Autonomous, [1, 2, 4, 6, 8, 12]),
        new("distributed-key-read", ProbeScenarioKind.Autonomous, [1, 2, 4, 6, 8, 12])
    ];

    private static readonly ScenarioPlan Broker =
        new("broker-directed", ProbeScenarioKind.BrokerDirected, [1, 12], ObserverCount: 1);

    private static readonly ScenarioPlan Churn =
        new("mixed-churn", ProbeScenarioKind.MixedChurn, [12], PublisherCount: 2);

    private static readonly ScenarioPlan Large =
        new("large-ingest", ProbeScenarioKind.LargeIngest, [1, 12]);

    private static readonly ScenarioPlan StickyOverflow =
        new("sticky-overflow-miss", ProbeScenarioKind.StickyOverflow, [1]);

    internal static ScenarioPlan[] Select(string mode) => mode switch
    {
        "sync" => Sync,
        "readers" => Readers,
        "broker" => [Broker],
        "churn" => [Churn],
        "large" => [Large],
        "overflow" => [StickyOverflow],
        "all" => [.. Sync, .. Readers],
        "full" => [.. Sync, .. Readers, Broker, Churn, Large, StickyOverflow],
        _ => throw new ArgumentException(
            "--mode must be sync, readers, broker, churn, large, overflow, all, or full.")
    };
}

internal enum BrokerMessageKind
{
    Key = 1,
    Stop = 2,
    Reset = 3
}

internal readonly record struct BrokerKeyMessage(
    BrokerMessageKind Kind,
    string KeyHex,
    int KeyIndex,
    long Generation,
    int PayloadLength,
    long PublishedTimestamp);

internal readonly record struct BrokerAcknowledgement(
    int WorkerId,
    string Role,
    int KeyIndex,
    long Generation,
    StoreStatus AcquireStatus,
    StoreStatus ReleaseStatus,
    bool DescriptorValid,
    bool PayloadValid,
    int BytesObserved,
    double EndToEndMicroseconds);

internal readonly record struct BucketPairCollisionSet(
    byte[][] Keys,
    long CandidatesExamined);

internal static class BenchmarkProtocol
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const int PatternBlockBytes = 256;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    internal static byte[] Key(int keyIndex)
    {
        var key = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(key, keyIndex + 1L);
        return key;
    }

    internal static BenchmarkKeyCatalog CreateKeyCatalog(int count) => new(count);

    internal static byte[][] CreateCollisionKeys(int count, int canonicalBucketCount)
    {
        if (count <= 0 || canonicalBucketCount <= 0 || (canonicalBucketCount & (canonicalBucketCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var keys = new byte[count][];
        var found = 0;
        long candidate = 1;
        while (found < count)
        {
            var key = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(key, candidate++);
            if ((Mix(Hash(key)) & (uint)(canonicalBucketCount - 1)) == 0)
            {
                keys[found++] = key;
            }
        }

        return keys;
    }

    internal static BucketPairCollisionSet CreateBucketPairCollisionKeys(
        int count,
        int bucketCount,
        int firstBucket,
        int secondBucket)
    {
        if (count <= 0
            || bucketCount <= 1
            || (bucketCount & (bucketCount - 1)) != 0
            || (uint)firstBucket >= (uint)bucketCount
            || (uint)secondBucket >= (uint)bucketCount
            || firstBucket == secondBucket)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var keys = new byte[count][];
        Span<byte> candidateKey = stackalloc byte[sizeof(long)];
        var found = 0;
        long candidate = 1;
        while (found < count)
        {
            BinaryPrimitives.WriteInt64LittleEndian(candidateKey, candidate++);
            (int actualFirst, int actualSecond) = GetBucketPair(candidateKey, bucketCount);

            if (actualFirst == firstBucket && actualSecond == secondBucket)
            {
                keys[found++] = candidateKey.ToArray();
            }
        }

        return new BucketPairCollisionSet(keys, candidate - 1);
    }

    internal static (int First, int Second) GetBucketPair(ReadOnlySpan<byte> key, int bucketCount)
    {
        if (bucketCount <= 1 || (bucketCount & (bucketCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount));
        }

        uint mask = (uint)(bucketCount - 1);
        ulong hash = Hash(key);
        int first = (int)(Mix(hash) & mask);
        int second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & mask);
        if (second == first)
        {
            second = (first + 1) & (int)mask;
        }

        return (first, second);
    }

    internal static int CalculatePrimaryBucketCount(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        int laneTarget = Math.Max(32, checked(slotCount * 4));
        var primaryLaneCount = 1;
        while (primaryLaneCount < laneTarget)
        {
            primaryLaneCount = checked(primaryLaneCount << 1);
        }

        // LayoutV2Constants.PrimaryLanesPerBucket is protocol-fixed at eight.
        return primaryLaneCount / 8;
    }

    internal static void WriteDescriptor(Span<byte> descriptor, int keyIndex, long generation, int payloadLength)
    {
        if (descriptor.Length != 16)
        {
            throw new ArgumentException("The benchmark descriptor is exactly 16 bytes.", nameof(descriptor));
        }

        BinaryPrimitives.WriteInt64LittleEndian(descriptor, generation);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[8..], keyIndex);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[12..], payloadLength);
    }

    internal static bool ValidateDescriptor(
        ReadOnlySpan<byte> descriptor,
        int keyIndex,
        long generation,
        int payloadLength) =>
        descriptor.Length == 16
        && BinaryPrimitives.ReadInt64LittleEndian(descriptor) == generation
        && BinaryPrimitives.ReadInt32LittleEndian(descriptor[8..]) == keyIndex
        && BinaryPrimitives.ReadInt32LittleEndian(descriptor[12..]) == payloadLength;

    internal static void FillGenerationPayload(Span<byte> payload, int keyIndex, long generation)
    {
        Span<byte> pattern = stackalloc byte[PatternBlockBytes];
        FillPatternBlock(pattern, keyIndex, generation);
        var offset = 0;
        while (offset < payload.Length)
        {
            int length = Math.Min(pattern.Length, payload.Length - offset);
            pattern[..length].CopyTo(payload[offset..]);
            offset += length;
        }
    }

    internal static bool ValidateGenerationPayload(ReadOnlySpan<byte> payload, int keyIndex, long generation)
    {
        Span<byte> pattern = stackalloc byte[PatternBlockBytes];
        FillPatternBlock(pattern, keyIndex, generation);
        var offset = 0;
        while (offset < payload.Length)
        {
            int length = Math.Min(pattern.Length, payload.Length - offset);
            if (!payload.Slice(offset, length).SequenceEqual(pattern[..length]))
            {
                return false;
            }

            offset += length;
        }

        return true;
    }

    internal static ulong Hash(ReadOnlySpan<byte> key)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte value in key)
        {
            hash ^= value;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static void FillPatternBlock(Span<byte> pattern, int keyIndex, long generation)
    {
        BinaryPrimitives.WriteInt64LittleEndian(pattern, generation);
        BinaryPrimitives.WriteInt64LittleEndian(pattern[8..], ~generation);
        BinaryPrimitives.WriteInt32LittleEndian(pattern[16..], keyIndex);
        BinaryPrimitives.WriteInt32LittleEndian(pattern[20..], ~keyIndex);
        for (var index = 24; index < pattern.Length; index++)
        {
            pattern[index] = unchecked((byte)(
                (generation * 131)
                + (keyIndex * 17L)
                + (index * 29L)
                + ((generation >> (index & 7)) * 7)));
        }
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }
}

/// <summary>
/// Process-local, read-only benchmark keys. Keeping the backing arrays private
/// prevents the measured loop from allocating one short-lived byte array per
/// lookup while still passing the library a normal <see cref="ReadOnlySpan{T}"/>.
/// </summary>
internal sealed class BenchmarkKeyCatalog
{
    private readonly ReadOnlyMemory<byte>[] _keys;
    private readonly string[] _hexKeys;

    internal BenchmarkKeyCatalog(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _keys = new ReadOnlyMemory<byte>[count];
        _hexKeys = new string[count];
        for (var index = 0; index < count; index++)
        {
            byte[] key = BenchmarkProtocol.Key(index);
            _keys[index] = key;
            _hexKeys[index] = Convert.ToHexString(key);
        }
    }

    internal int Count => _keys.Length;

    internal ReadOnlyMemory<byte> this[int index] => _keys[index];

    internal string Hex(int index) => _hexKeys[index];
}

internal static class ProcessorAffinityPlanner
{
    private const int CpuSetInformationType = 0;

    internal static bool TryApply(int ordinal, out int assignedProcessor, out string strategy)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return TryApply(process, ordinal, out assignedProcessor, out strategy);
        }
        catch
        {
            assignedProcessor = -1;
            strategy = "logical-order-fallback-apply-failed";
            return false;
        }
    }

    internal static bool TryApply(
        Process process,
        int ordinal,
        out int assignedProcessor,
        out string strategy)
    {
        ArgumentNullException.ThrowIfNull(process);
        assignedProcessor = -1;
        strategy = ordinal < 0 ? "not-requested" : "logical-order-fallback";
        if (ordinal < 0)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            strategy = "unsupported-platform";
            return false;
        }

        try
        {
            ulong available = unchecked((ulong)(nuint)process.ProcessorAffinity);
            int[] order = TryGetPhysicalCoreFirstOrder(available, out strategy);
            if (order.Length == 0)
            {
                return false;
            }

            assignedProcessor = order[ordinal % order.Length];
            process.ProcessorAffinity = unchecked((nint)(1UL << assignedProcessor));
            return true;
        }
        catch
        {
            assignedProcessor = -1;
            strategy += "-apply-failed";
            return false;
        }
    }

    private static int[] TryGetPhysicalCoreFirstOrder(ulong available, out string strategy)
    {
        if (OperatingSystem.IsWindows()
            && TryGetWindowsTopology(available, out int[] windowsOrder))
        {
            strategy = "windows-physical-core-first";
            return windowsOrder;
        }

        if (OperatingSystem.IsLinux()
            && TryGetLinuxTopology(available, out int[] linuxOrder))
        {
            strategy = "linux-physical-core-first";
            return linuxOrder;
        }

        strategy = "logical-order-fallback";
        return Enumerable.Range(0, Math.Min(64, IntPtr.Size * 8))
            .Where(cpu => (available & (1UL << cpu)) != 0)
            .ToArray();
    }

    private static bool TryGetWindowsTopology(ulong available, out int[] order)
    {
        order = [];
        if (!GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint requiredBytes, IntPtr.Zero, 0)
            && Marshal.GetLastWin32Error() != 122)
        {
            return false;
        }

        if (requiredBytes < 24 || requiredBytes > 16 * 1024 * 1024)
        {
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetSystemCpuSetInformation(buffer, requiredBytes, out uint returnedBytes, IntPtr.Zero, 0))
            {
                return false;
            }

            var entries = new List<(int Cpu, int Core, int Efficiency)>();
            int offset = 0;
            while (offset + 8 <= returnedBytes)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                int type = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > returnedBytes)
                {
                    return false;
                }

                if (type == CpuSetInformationType && size >= 21)
                {
                    short group = Marshal.ReadInt16(buffer, offset + 12);
                    int logicalProcessor = Marshal.ReadByte(buffer, offset + 14);
                    int coreIndex = Marshal.ReadByte(buffer, offset + 15);
                    int efficiencyClass = Marshal.ReadByte(buffer, offset + 18);
                    if (group == 0
                        && logicalProcessor < Math.Min(64, IntPtr.Size * 8)
                        && (available & (1UL << logicalProcessor)) != 0)
                    {
                        entries.Add((logicalProcessor, coreIndex, efficiencyClass));
                    }
                }

                offset += size;
            }

            order = PhysicalFirst(entries.Select(static entry =>
                (entry.Cpu, Package: 0, entry.Core, entry.Efficiency)));
            return order.Length != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetLinuxTopology(ulong available, out int[] order)
    {
        order = [];
        const string CpuRoot = "/sys/devices/system/cpu";
        if (!Directory.Exists(CpuRoot))
        {
            return false;
        }

        var entries = new List<(int Cpu, int Package, int Core)>();
        foreach (string directory in Directory.EnumerateDirectories(CpuRoot, "cpu*"))
        {
            string suffix = Path.GetFileName(directory).AsSpan(3).ToString();
            if (!int.TryParse(suffix, out int cpu)
                || cpu < 0
                || cpu >= Math.Min(64, IntPtr.Size * 8)
                || (available & (1UL << cpu)) == 0)
            {
                continue;
            }

            string packagePath = Path.Combine(directory, "topology", "physical_package_id");
            string corePath = Path.Combine(directory, "topology", "core_id");
            if (!int.TryParse(File.ReadAllText(packagePath).Trim(), out int package)
                || !int.TryParse(File.ReadAllText(corePath).Trim(), out int core))
            {
                return false;
            }

            entries.Add((cpu, package, core));
        }

        order = PhysicalFirst(entries.Select(static entry =>
            (entry.Cpu, entry.Package, entry.Core, Efficiency: 0)));
        return order.Length != 0;
    }

    private static int[] PhysicalFirst(
        IEnumerable<(int Cpu, int Package, int Core, int Efficiency)> source)
    {
        var entries = source
            .DistinctBy(static entry => entry.Cpu)
            .OrderByDescending(static entry => entry.Efficiency)
            .ThenBy(static entry => entry.Package)
            .ThenBy(static entry => entry.Core)
            .ThenBy(static entry => entry.Cpu)
            .ToArray();
        var firstThreads = entries
            .GroupBy(static entry => (entry.Package, entry.Core))
            .OrderByDescending(static group => group.Max(static entry => entry.Efficiency))
            .ThenBy(static group => group.Key.Package)
            .ThenBy(static group => group.Key.Core)
            .Select(static group => group.First().Cpu);
        var siblings = entries
            .GroupBy(static entry => (entry.Package, entry.Core))
            .OrderByDescending(static group => group.Max(static entry => entry.Efficiency))
            .ThenBy(static group => group.Key.Package)
            .ThenBy(static group => group.Key.Core)
            .SelectMany(static group => group.Skip(1).Select(static entry => entry.Cpu));
        return firstThreads.Concat(siblings).ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);
}

internal sealed class TemporaryProcessAffinity : IDisposable
{
    private readonly Process? _process;
    private readonly nint _originalAffinity;

    private TemporaryProcessAffinity(
        Process? process,
        nint originalAffinity,
        bool applied,
        int assignedProcessor,
        string strategy)
    {
        _process = process;
        _originalAffinity = originalAffinity;
        Applied = applied;
        AssignedProcessor = assignedProcessor;
        Strategy = strategy;
    }

    internal bool Applied { get; }

    internal int AssignedProcessor { get; }

    internal string Strategy { get; }

    internal static TemporaryProcessAffinity Apply(int ordinal)
    {
        if (ordinal < 0 || (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()))
        {
            string unavailableStrategy = ordinal < 0 ? "not-requested" : "unsupported-platform";
            return new TemporaryProcessAffinity(null, 0, false, -1, unavailableStrategy);
        }

        Process process = Process.GetCurrentProcess();
        nint originalAffinity = process.ProcessorAffinity;
        bool applied = ProcessorAffinityPlanner.TryApply(
            ordinal,
            out int assignedProcessor,
            out string strategy);
        return new TemporaryProcessAffinity(
            process,
            originalAffinity,
            applied,
            assignedProcessor,
            strategy);
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (Applied && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                _process.ProcessorAffinity = _originalAffinity;
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
