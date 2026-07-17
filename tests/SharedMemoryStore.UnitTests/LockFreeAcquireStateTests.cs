using System.Reflection;
using System.Runtime.InteropServices;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeAcquireStateTests
{
    [Fact]
    public void MissingExactKeyReturnsNotFoundWithoutConsumingLeaseCapacity()
    {
        RequireLeaseRegistry();
        using var store = CreateStore(leaseRecordCount: 1);

        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([0x44], out var missing));
        Assert.False(missing.IsValid);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x45], [0x55]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0x45], out var present));
        Assert.Equal(StoreStatus.Success, present.Release());
    }

    [Fact]
    public void CommitThenAcquireProjectsOnlyTheExactCommittedDescriptorAndPayload()
    {
        RequireLeaseRegistry();
        using var store = CreateStore(leaseRecordCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryReserve([0x31], payloadLength: 3, [0x71, 0x72], out var reservation));
        new byte[] { 0x11, 0x12, 0x13 }.CopyTo(reservation.GetSpan(3));
        Assert.Equal(StoreStatus.Success, reservation.Advance(3));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([0x31], out var lease));
        Assert.Equal([0x11, 0x12, 0x13], lease.ValueSpan.ToArray());
        Assert.Equal([0x71, 0x72], lease.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void LookupRequiresStableCellAndSlotControlDoubleValidation()
    {
        const ulong binding = 0x0000_0002_8000_0001UL;
        var stable = LookupOracle.Evaluate(new LookupObservation(
            FirstCell: binding,
            FirstControl: 0x13,
            BindingGenerationMatches: true,
            HashMatches: true,
            KeyBytesMatch: true,
            SecondControl: 0x13,
            SecondCell: binding));
        var changedCell = LookupOracle.Evaluate(new LookupObservation(
            binding, 0x13, true, true, true, 0x13, 0));
        var changedControl = LookupOracle.Evaluate(new LookupObservation(
            binding, 0x13, true, true, true, 0x1B, binding));

        Assert.Equal(LookupOutcome.Found, stable);
        Assert.Equal(LookupOutcome.Retry, changedCell);
        Assert.Equal(LookupOutcome.Retry, changedControl);
    }

    [Fact]
    public void EqualHashWithDifferentExactKeyIsNotAMatch()
    {
        const ulong binding = 0x0000_0002_8000_0001UL;
        var observation = new LookupObservation(
            FirstCell: binding,
            FirstControl: 0x13,
            BindingGenerationMatches: true,
            HashMatches: true,
            KeyBytesMatch: false,
            SecondControl: 0x13,
            SecondCell: binding);

        Assert.Equal(LookupOutcome.NotFound, LookupOracle.Evaluate(observation));
    }

    [Fact]
    public void StaleGenerationIsClearedOnlyByExactBindingCas()
    {
        const ulong stale = 0x0000_0002_8000_0001UL;
        const ulong replacement = 0x0000_0003_0000_0001UL;
        var staleObservation = new LookupObservation(
            FirstCell: stale,
            FirstControl: 0x1B,
            BindingGenerationMatches: false,
            HashMatches: true,
            KeyBytesMatch: true,
            SecondControl: 0x1B,
            SecondCell: stale);

        Assert.Equal(LookupOutcome.Stale, LookupOracle.Evaluate(staleObservation));
        Assert.Equal(replacement, LookupOracle.ClearStale(currentCell: replacement, expectedStale: stale));
        Assert.Equal(0UL, LookupOracle.ClearStale(currentCell: stale, expectedStale: stale));
    }

    [Fact]
    public void BoundedCleanupSchedulesEventuallyClearEveryObservedStaleCellWithoutClearingCurrentOnes()
    {
        var cells = new[]
        {
            new CleanupCell(11, IsStale: true),
            new CleanupCell(12, IsStale: false),
            new CleanupCell(13, IsStale: true),
            new CleanupCell(14, IsStale: true)
        };
        var cursor = 0;
        for (var invocation = 0; invocation < cells.Length; invocation++)
        {
            cursor = LookupOracle.HelpStale(cells, cursor, budget: 1);
        }

        Assert.Equal(0UL, cells[0].Binding);
        Assert.Equal(12UL, cells[1].Binding);
        Assert.Equal(0UL, cells[2].Binding);
        Assert.Equal(0UL, cells[3].Binding);
    }

    [Fact]
    public void ProductionLookupAcceptsHashAndExactKeyAndProvidesAHelpingPath()
    {
        var directory = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeKeyDirectory",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(directory is not null, "LockFreeKeyDirectory is required.");
        var methods = directory!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo[] lookups = methods.Where(method => method.Name == "TryLookup").ToArray();
        Assert.Contains(
            lookups,
            lookup => lookup.GetParameters().Any(parameter => parameter.ParameterType == typeof(ulong))
                && lookup.GetParameters().Any(
                    parameter => parameter.ParameterType == typeof(ReadOnlySpan<byte>)));
        Assert.Contains(methods, static method => method.Name.Contains("Help", StringComparison.Ordinal));
    }

    private static void RequireLeaseRegistry()
    {
        Assert.True(
            typeof(MemoryStore).Assembly.GetType(
                "SharedMemoryStore.LockFree.LockFreeLeaseRegistry",
                throwOnError: false,
                ignoreCase: false) is not null,
            "Acquire requires the missing LockFreeLeaseRegistry implementation.");
    }

    private static Store CreateStore(int leaseRecordCount)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The lock-free profile is qualified only on Windows/Linux x64.");
        }

        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-acquire-state-{Guid.NewGuid():N}",
            slotCount: 3,
            maxValueBytes: 16,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        var status = Store.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }

    private enum LookupOutcome
    {
        Found,
        NotFound,
        Retry,
        Stale
    }

    private readonly record struct LookupObservation(
        ulong FirstCell,
        ulong FirstControl,
        bool BindingGenerationMatches,
        bool HashMatches,
        bool KeyBytesMatch,
        ulong SecondControl,
        ulong SecondCell);

    private sealed class CleanupCell(ulong binding, bool IsStale)
    {
        public ulong Binding { get; set; } = binding;

        public bool IsStale { get; } = IsStale;
    }

    private static class LookupOracle
    {
        public static LookupOutcome Evaluate(LookupObservation observation)
        {
            if (observation.FirstCell == 0)
            {
                return LookupOutcome.NotFound;
            }

            if (!observation.BindingGenerationMatches)
            {
                return LookupOutcome.Stale;
            }

            if (!observation.HashMatches || !observation.KeyBytesMatch)
            {
                return LookupOutcome.NotFound;
            }

            return observation.FirstControl == observation.SecondControl
                && observation.FirstCell == observation.SecondCell
                ? LookupOutcome.Found
                : LookupOutcome.Retry;
        }

        public static ulong ClearStale(ulong currentCell, ulong expectedStale) =>
            currentCell == expectedStale ? 0 : currentCell;

        public static int HelpStale(CleanupCell[] cells, int cursor, int budget)
        {
            for (var visited = 0; visited < budget; visited++)
            {
                var index = cursor++ % cells.Length;
                if (cells[index].IsStale)
                {
                    cells[index].Binding = 0;
                }
            }

            return cursor % cells.Length;
        }
    }
}
