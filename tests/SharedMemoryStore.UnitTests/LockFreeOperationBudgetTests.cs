using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using System.Runtime.InteropServices;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeOperationBudgetTests
{
    [Fact]
    public void PublicNoWaitAllowsOneProbeChunkButStructuralAttemptKeepsLegacyFullScan()
    {
        LockFreeOperationBudget noWait = LockFreeOperationBudget.Start(StoreWaitOptions.NoWait);

        Assert.Equal(StoreStatus.Success, noWait.CheckPeriodic(0));
        Assert.Equal(StoreStatus.Success, noWait.CheckPeriodic(63));
        Assert.Equal(StoreStatus.StoreBusy, noWait.CheckPeriodic(64));

        Assert.Equal(
            StoreStatus.Success,
            LockFreeOperationBudget.StructuralAttempt.CheckPeriodic(64));
        Assert.False(LockFreeOperationBudget.StructuralAttempt.TryContinueAfterContention(
            attempt: 128,
            out StoreStatus terminal));
        Assert.Equal(StoreStatus.StoreBusy, terminal);
    }

    [Fact]
    public void InfiniteBudgetStillObservesExplicitCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = LockFreeOperationBudget.Start(
            new StoreWaitOptions(Timeout.InfiniteTimeSpan, cancellation.Token));
        cancellation.Cancel();

        Assert.Equal(StoreStatus.OperationCanceled, budget.Check());
        Assert.False(budget.TryContinueAfterContention(0, out StoreStatus terminal));
        Assert.Equal(StoreStatus.OperationCanceled, terminal);
    }

    [Fact]
    public void RemainingWaitRejectsExpiredFiniteButPreservesTrueNoWait()
    {
        long oldStart = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        var expired = LockFreeOperationBudget.Start(
            new StoreWaitOptions(TimeSpan.FromMilliseconds(10)),
            oldStart);
        Assert.Equal(
            StoreStatus.StoreBusy,
            expired.TryGetRemainingWaitOptions(out _));

        var noWait = LockFreeOperationBudget.Start(StoreWaitOptions.NoWait, oldStart);
        Assert.Equal(
            StoreStatus.Success,
            noWait.TryGetRemainingWaitOptions(out StoreWaitOptions remaining));
        Assert.Equal(StoreWaitOptions.NoWait, remaining);
    }

    [Fact]
    public void ByteLinearHashAndCopyUseCanonicalIdentityAndBoundNoWaitQuantum()
    {
        byte[] belowQuantum = Enumerable.Repeat((byte)0x37, 4_095).ToArray();
        byte[] fullQuantum = Enumerable.Repeat((byte)0x42, 4_096).ToArray();
        LockFreeOperationBudget infinite = LockFreeOperationBudget.Start(StoreWaitOptions.Infinite);
        LockFreeOperationBudget noWait = LockFreeOperationBudget.Start(StoreWaitOptions.NoWait);

        Assert.Equal(
            StoreStatus.Success,
            LockFreeByteOperations.TryHash(belowQuantum, infinite, out ulong hash));
        Assert.Equal(SharedMemoryStore.LockFree.StoreKey.Hash(belowQuantum), hash);
        Assert.Equal(
            StoreStatus.StoreBusy,
            LockFreeByteOperations.TryHash(fullQuantum, noWait, out _));

        byte[] destination = new byte[belowQuantum.Length];
        Assert.Equal(
            StoreStatus.Success,
            LockFreeByteOperations.TryCopy(belowQuantum, destination, infinite));
        Assert.Equal(belowQuantum, destination);
        Assert.Equal(
            StoreStatus.StoreBusy,
            LockFreeByteOperations.TryCopy(fullQuantum, new byte[fullQuantum.Length], noWait));

        byte[] overQuantum = new byte[4_160];
        Assert.Equal(
            StoreStatus.StoreBusy,
            LockFreeByteOperations.TryCopy(
                overQuantum,
                new byte[overQuantum.Length],
                noWait,
                out int partiallyCopied));
        Assert.Equal(4_096, partiallyCopied);
    }

    [Fact]
    public void LargeExactKeyNoWaitReturnsBusyInsteadOfFalseNotFound()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        byte[] key = Enumerable.Repeat((byte)0x5A, 4_096).ToArray();
        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-key-budget-{Guid.NewGuid():N}",
            slotCount: 1,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: key.Length,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, out MemoryStore? store));
        using MemoryStore owned = Assert.IsType<MemoryStore>(store);
        Assert.Equal(
            StoreStatus.Success,
            owned.TryPublish(key, [0x7B], default, StoreWaitOptions.Infinite));

        LockFreeKeyDirectory directory = ReadDirectory(owned);
        ulong hash = StoreKey.Hash(key);
        LockFreeOperationBudget noWait = LockFreeOperationBudget.Start(StoreWaitOptions.NoWait);
        Assert.Equal(
            StoreStatus.StoreBusy,
            directory.TryLookup(key, hash, noWait, out _, out _));
        Assert.Equal(
            StoreStatus.Success,
            directory.TryLookup(
                key,
                hash,
                LockFreeOperationBudget.UnboundedScan,
                out ulong exactBinding,
                out DirectoryLocation exactLocation));
        Assert.NotEqual(0UL, exactBinding);
        Assert.NotEqual(0UL, exactLocation.Value);

        Assert.Equal(StoreStatus.StoreBusy, owned.TryAcquire(key, StoreWaitOptions.NoWait, out _));
        Assert.Equal(StoreStatus.Success, owned.TryAcquire(key, StoreWaitOptions.Infinite, out ValueLease lease));
        Assert.Equal(0x7B, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release(StoreWaitOptions.Infinite));
    }

    [Fact]
    public void ManyTinySegmentsNoWaitReturnsBusyAndAbortsOwnedReservation()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        byte[][] buffers = Enumerable.Range(0, 65)
            .Select(index => new[] { checked((byte)index) })
            .ToArray();
        ReadOnlySequence<byte> payload = Sequence(buffers);
        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-segment-budget-{Guid.NewGuid():N}",
            slotCount: 1,
            maxValueBytes: 65,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, out MemoryStore? store));
        using MemoryStore owned = Assert.IsType<MemoryStore>(store);

        Assert.Equal(
            StoreStatus.StoreBusy,
            owned.TryPublishSegments([1], payload, [], StoreWaitOptions.NoWait, out long copied));
        Assert.Equal(64, copied);
        Assert.Equal(StoreStatus.Success, owned.TryGetDiagnostics(out var afterAbort));
        Assert.Equal(1, afterAbort.FreeSlotCount);
        Assert.Equal(StoreStatus.Success, owned.TryPublish([1], [7], [], StoreWaitOptions.Infinite));
    }

    [Fact]
    public void MalformedSequenceRetainsBytesCopiedBeforeFailure()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { });
        using MemoryStore store = CreateInstrumentedStore(maxValueBytes: 64, checkpoint);
        var first = new BufferSegment(new byte[8]);
        BufferSegment last = first.Append(new byte[8], runningIndex: 1);
        var malformed = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);

        StoreStatus status = store.TryPublishSegments(
            [1],
            malformed,
            [],
            StoreWaitOptions.Infinite,
            out long copied);

        Assert.Equal(StoreStatus.UnknownFailure, status);
        Assert.Equal(8, copied);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out var afterAbort));
        Assert.Equal(1, afterAbort.FreeSlotCount);
    }

    [Fact]
    public void UnderEnumeratedMalformedSequenceRetainsActualCopiedBytes()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { });
        using MemoryStore store = CreateInstrumentedStore(maxValueBytes: 128, checkpoint);
        var first = new BufferSegment(new byte[8]);
        BufferSegment last = first.Append(new byte[8], runningIndex: 64);
        var malformed = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);

        StoreStatus status = store.TryPublishSegments(
            [1],
            malformed,
            [],
            StoreWaitOptions.Infinite,
            out long copied);

        Assert.Equal(StoreStatus.UnknownFailure, status);
        Assert.Equal(16, copied);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out var afterAbort));
        Assert.Equal(1, afterAbort.FreeSlotCount);
    }

    [Fact]
    public void OneLargeSegmentNoWaitReportsCompletedChunksBeforeBudgetExpiry()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        const int payloadLength = 4_160;
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { });
        using MemoryStore store = CreateInstrumentedStore(payloadLength, checkpoint);
        var payload = new ReadOnlySequence<byte>(new byte[payloadLength]);

        Assert.Equal(
            StoreStatus.StoreBusy,
            store.TryPublishSegments(
                [1],
                payload,
                [],
                StoreWaitOptions.NoWait,
                out long copied));
        Assert.Equal(4_096, copied);
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out var afterAbort));
        Assert.Equal(1, afterAbort.FreeSlotCount);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7], [], StoreWaitOptions.Infinite));
    }

    [Fact]
    public void CommitCancellationAfterFullSegmentCopyRetainsFullCount()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id == LockFreeCheckpointId.CommitBeforePublicationCas)
            {
                cancellation.Cancel();
            }
        });
        using MemoryStore store = CreateInstrumentedStore(maxValueBytes: 8, checkpoint);
        ReadOnlySequence<byte> payload = Sequence([[1, 2], [3, 4]]);
        var wait = new StoreWaitOptions(Timeout.InfiniteTimeSpan, cancellation.Token);

        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryPublishSegments([1], payload, [], wait, out long copied));
        Assert.Equal(4, copied);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out var afterAbort));
        Assert.Equal(1, afterAbort.FreeSlotCount);
    }

    [Fact]
    public void AdvanceNoWaitStopsAfterOneBoundedCasProbeQuantum()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        LockFreeSlotTable? slots = null;
        var slotIndex = -1;
        var injectedCasWins = 0;
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id == LockFreeCheckpointId.AdvanceBeforeBytesAdvancedCas)
            {
                IncrementBytesAdvanced(Assert.IsType<LockFreeSlotTable>(slots), slotIndex);
                Interlocked.Increment(ref injectedCasWins);
            }
        });

        using MemoryStore store = CreateInstrumentedStore(maxValueBytes: 128, checkpoint: checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 128, default, out ValueReservation reservation));
        slots = ReadSlotTable(store);
        slotIndex = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding).SlotIndex;

        Assert.Equal(StoreStatus.StoreBusy, reservation.Advance(1, StoreWaitOptions.NoWait));
        Assert.Equal(64, Volatile.Read(ref injectedCasWins));
        Assert.Equal(64, reservation.BytesWritten);
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
    }

    [Fact]
    public void AdvanceInfiniteRetriesAfterCasLossUntilItSucceeds()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        LockFreeSlotTable? slots = null;
        var slotIndex = -1;
        var beforeCasCount = 0;
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id != LockFreeCheckpointId.AdvanceBeforeBytesAdvancedCas)
            {
                return;
            }

            if (Interlocked.Increment(ref beforeCasCount) == 1)
            {
                IncrementBytesAdvanced(Assert.IsType<LockFreeSlotTable>(slots), slotIndex);
            }
        });

        using MemoryStore store = CreateInstrumentedStore(maxValueBytes: 2, checkpoint: checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out ValueReservation reservation));
        slots = ReadSlotTable(store);
        slotIndex = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding).SlotIndex;

        Assert.Equal(StoreStatus.Success, reservation.Advance(1, StoreWaitOptions.Infinite));
        Assert.Equal(2, Volatile.Read(ref beforeCasCount));
        Assert.Equal(2, reservation.BytesWritten);
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
    }

    [Fact]
    public void AdvanceInfiniteObservesCancellationAfterCasLossWithoutItsOwnWrite()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        LockFreeSlotTable? slots = null;
        var slotIndex = -1;
        var beforeCasCount = 0;
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id != LockFreeCheckpointId.AdvanceBeforeBytesAdvancedCas)
            {
                return;
            }

            int occurrence = Interlocked.Increment(ref beforeCasCount);
            if (occurrence == 1)
            {
                IncrementBytesAdvanced(Assert.IsType<LockFreeSlotTable>(slots), slotIndex);
            }
            else if (occurrence == 2)
            {
                cancellation.Cancel();
            }
        });

        using MemoryStore store = CreateInstrumentedStore(maxValueBytes: 2, checkpoint: checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out ValueReservation reservation));
        slots = ReadSlotTable(store);
        slotIndex = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding).SlotIndex;

        var infiniteCancelable = new StoreWaitOptions(Timeout.InfiniteTimeSpan, cancellation.Token);
        Assert.Equal(StoreStatus.OperationCanceled, reservation.Advance(1, infiniteCancelable));
        Assert.Equal(2, Volatile.Read(ref beforeCasCount));
        Assert.Equal(1, reservation.BytesWritten);
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
    }

    [Fact]
    public void InfiniteReservationClaimRepairsOlderDirectoryResidueBeforeReuse()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateInstrumentedStore(
            maxValueBytes: 1,
            checkpoint: LockFreeCheckpointFactory.CreateInstrumented(static _ => { }));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1], default, StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, store.TryRemove([1], StoreWaitOptions.Infinite));

        LockFreeSlotTable slots = ReadSlotTable(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        AtomicControlWord.StoreRelease(
            ref slot.DirectoryLocation,
            unchecked((long)DirectoryLocation.Encode(kind: 1, index: 0, generation: 1)));
        AtomicControlWord.StoreRelease(
            ref slot.DirectoryOperation,
            unchecked((long)DirectoryOperation.Encode(
                intent: 1,
                phase: 1,
                targetKind: 1,
                targetIndex: 0,
                generation: 1)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([2], 1, default, StoreWaitOptions.Infinite, out ValueReservation reservation));
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
    }

    private static ReadOnlySequence<byte> Sequence(byte[][] buffers)
    {
        BufferSegment? first = null;
        BufferSegment? last = null;
        foreach (byte[] buffer in buffers)
        {
            last = last is null
                ? first = new BufferSegment(buffer)
                : last.Append(buffer);
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static LockFreeKeyDirectory ReadDirectory(MemoryStore store)
    {
        FieldInfo engineField = typeof(MemoryStore).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is absent.");
        object engine = engineField.GetValue(store)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is null.");
        FieldInfo directoryField = engine.GetType().GetField(
            "_directory",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("Lock-free engine._directory is absent.");
        return Assert.IsType<LockFreeKeyDirectory>(directoryField.GetValue(engine));
    }

    private static LockFreeSlotTable ReadSlotTable(MemoryStore store)
    {
        FieldInfo engineField = typeof(MemoryStore).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is absent.");
        object engine = engineField.GetValue(store)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is null.");
        FieldInfo slotsField = engine.GetType().GetField(
            "_slots",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("Lock-free engine._slots is absent.");
        return Assert.IsType<LockFreeSlotTable>(slotsField.GetValue(engine));
    }

    private static MemoryStore CreateInstrumentedStore(
        int maxValueBytes,
        InstrumentedLockFreeCheckpoint checkpoint)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            $"sms-v2-advance-budget-{Guid.NewGuid():N}",
            slotCount: 1,
            maxValueBytes,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        Assert.Equal(
            StoreOpenStatus.Success,
            LockFreeInstrumentedStoreFactory.TryCreateOrOpen(options, checkpoint, out MemoryStore? store));
        return Assert.IsType<MemoryStore>(store);
    }

    private static void IncrementBytesAdvanced(LockFreeSlotTable slots, int slotIndex)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        long observed = AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced);
        Assert.Equal(
            observed,
            AtomicControlWord.CompareExchange(ref slot.BytesAdvanced, observed + 1, observed));
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        internal BufferSegment(byte[] buffer)
        {
            Memory = buffer;
        }

        internal BufferSegment Append(byte[] buffer, long? runningIndex = null)
        {
            var next = new BufferSegment(buffer)
            {
                RunningIndex = runningIndex ?? RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }
}
