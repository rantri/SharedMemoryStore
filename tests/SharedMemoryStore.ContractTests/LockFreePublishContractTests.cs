using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.ContractTests;

public sealed class LockFreePublishContractTests
{
    [Fact]
    public void SimpleAndReservationPublicationAreInvisibleUntilCommitAndThenExact()
    {
        using var store = CreateLockFreeStore(slotCount: 4);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1, 2, 3], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var simple));
        Assert.Equal(new byte[] { 1, 2, 3 }, simple.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 7 }, simple.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, simple.Release());

        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 3, [8], out var reservation));
        reservation.GetSpan(3).Fill(9);
        Assert.Equal(StoreStatus.Success, reservation.Advance(2));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([2], out _));
        Assert.Equal(StoreStatus.ReservationIncomplete, reservation.Commit());
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var reserved));
        Assert.Equal(new byte[] { 9, 9, 9 }, reserved.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 8 }, reserved.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, reserved.Release());
    }

    [Fact]
    public void SegmentedPublicationCopiesTheLogicalSequenceAndDescriptorExactly()
    {
        using var store = CreateLockFreeStore();
        var sequence = SequenceFactory.Create([1, 2], [3], [4, 5]);

        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([1], sequence, [6], out var copied));
        Assert.Equal(5, copied);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 6 }, lease.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void EmptyOversizedAndZeroLengthBoundariesReturnStableStatuses()
    {
        using var store = CreateLockFreeStore(
            slotCount: 6,
            maxValueBytes: 3,
            maxDescriptorBytes: 1,
            maxKeyBytes: 2);

        Assert.Equal(StoreStatus.InvalidKey, store.TryPublish([], [1]));
        Assert.Equal(StoreStatus.InvalidKey, store.TryReserve([], 1, default, out _));
        Assert.Equal(StoreStatus.KeyTooLarge, store.TryPublish([1, 2, 3], [1]));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryPublish([1], [1, 2, 3, 4]));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryReserve([1], -1, default, out _));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryReserve([1], 4, default, out _));
        Assert.Equal(StoreStatus.DescriptorTooLarge, store.TryPublish([1], [1], [1, 2]));
        Assert.Equal(StoreStatus.DescriptorTooLarge, store.TryReserve([1], 1, [1, 2], out _));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], []));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var emptySimple));
        Assert.Equal(0, emptySimple.ValueLength);
        Assert.True(emptySimple.ValueSpan.IsEmpty);
        Assert.Equal(StoreStatus.Success, emptySimple.Release());

        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 0, default, out var emptyReservation));
        Assert.Equal(0, emptyReservation.PayloadLength);
        Assert.Equal(StoreStatus.Success, emptyReservation.Commit());
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var emptyReserved));
        Assert.True(emptyReserved.ValueSpan.IsEmpty);
        Assert.Equal(StoreStatus.Success, emptyReserved.Release());
    }

    [Fact]
    public void DuplicateAndCapacityStatusesDoNotCreateSecondCurrentValue()
    {
        using var store = CreateLockFreeStore(slotCount: 1);

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [2]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryReserve([1], 1, default, out _));
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish([2], [2]));
        Assert.Equal(StoreStatus.Success, reservation.Abort());

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [3]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [4]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(3, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void PreCanceledPublicationLeavesNoKeySlotOrCopiedBytes()
    {
        using var store = CreateLockFreeStore(slotCount: 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var wait = new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token);
        var sequence = new ReadOnlySequence<byte>(new byte[] { 2 });

        Assert.Equal(StoreStatus.OperationCanceled, store.TryPublish([1], [1], default, wait));
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryPublishSegments([2], sequence, default, wait, out var copied));
        Assert.Equal(0, copied);
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryReserve([3], 1, default, wait, out var canceledReservation));
        Assert.False(canceledReservation.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([2], sequence, default, out copied));
        Assert.Equal(1, copied);
        Assert.Equal(StoreStatus.Success, store.TryReserve([3], 1, default, out var reservation));
        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    [Fact]
    public void PublishReserveAdvanceCommitAndSegmentsNeverEnterOperationSynchronizer()
    {
        using var store = CreateLockFreeStore(slotCount: 4);
        using var held = new HeldOperationSynchronizer(storeName: StoreName(store));
        var sequence = new ReadOnlySequence<byte>(new byte[] { 2 });
        var stopwatch = Stopwatch.StartNew();

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1], default, StoreWaitOptions.NoWait));
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublishSegments([2], sequence, default, StoreWaitOptions.NoWait, out var copied));
        Assert.Equal(1, copied);
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([3], 1, default, StoreWaitOptions.NoWait, out var reservation));
        reservation.GetSpan()[0] = 3;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1, StoreWaitOptions.NoWait));
        Assert.Equal(StoreStatus.Success, reservation.Commit(StoreWaitOptions.NoWait));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void WarmedSimpleSegmentedAndReservationPublicationAllocateZeroBytes()
    {
        const int WarmupIterations = 16;
        const int MeasuredIterations = 64;
        using var store = CreateLockFreeStore(slotCount: 3 * (WarmupIterations + MeasuredIterations));
        var simpleValue = new byte[] { 1 };
        var segmentValue = new byte[] { 2 };
        var segmented = new ReadOnlySequence<byte>(segmentValue);

        PublishBatch(store, 0, WarmupIterations, simpleValue, segmented);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        PublishBatch(store, WarmupIterations, MeasuredIterations, simpleValue, segmented);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void PublishBatch(
        MemoryStore store,
        int start,
        int count,
        byte[] simpleValue,
        ReadOnlySequence<byte> segmented)
    {
        Span<byte> key = stackalloc byte[5];
        for (var offset = 0; offset < count; offset++)
        {
            var id = start + offset;
            BitConverter.TryWriteBytes(key[1..], id);

            key[0] = 1;
            RequireSuccess(store.TryPublish(key, simpleValue));

            key[0] = 2;
            RequireSuccess(store.TryPublishSegments(key, segmented, default, out var copied));
            if (copied != 1)
            {
                throw new InvalidOperationException("Segmented publish copied an unexpected length.");
            }

            key[0] = 3;
            RequireSuccess(store.TryReserve(key, 1, default, out var reservation));
            reservation.GetSpan()[0] = 3;
            RequireSuccess(reservation.Advance(1));
            RequireSuccess(reservation.Commit());
        }
    }

    private static void RequireSuccess(StoreStatus status)
    {
        if (status != StoreStatus.Success)
        {
            throw new InvalidOperationException($"Expected Success, received {status}.");
        }
    }

    private static MemoryStore CreateLockFreeStore(
        int slotCount = 8,
        int maxValueBytes = 16,
        int maxDescriptorBytes = 4,
        int maxKeyBytes = 8)
    {
        var name = $"sms-v2-publish-contract-{Guid.NewGuid():N}";
        var options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount: Math.Max(8, slotCount),
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        var status = MemoryStore.TryCreateOrOpen(options, out var store);

        Assert.Equal(StoreOpenStatus.Success, status);
        var result = Assert.IsType<MemoryStore>(store);
        Assert.Equal(StoreProfile.LockFree, result.Profile);
        StoreNames.Add(result, new StoreNameHolder(name));
        return result;
    }

    private static readonly ConditionalWeakTable<MemoryStore, StoreNameHolder> StoreNames = new();

    private static string StoreName(MemoryStore store) => StoreNames.GetValue(
        store,
        static _ => throw new InvalidOperationException("The lock-free store name was not registered.")).Name;

    private sealed record StoreNameHolder(string Name);

    private sealed class HeldOperationSynchronizer : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private Exception? _failure;

        public HeldOperationSynchronizer(string storeName)
        {
            _thread = new Thread(() => Hold(storeName)) { IsBackground = true };
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)), "The operation synchronizer holder did not start.");
            if (_failure is not null)
            {
                throw new InvalidOperationException("The operation synchronizer could not be held.", _failure);
            }
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "The operation synchronizer holder did not stop.");
            _ready.Dispose();
            _release.Dispose();
            if (_failure is not null)
            {
                throw new InvalidOperationException("The operation synchronizer holder failed.", _failure);
            }
        }

        private void Hold(string storeName)
        {
            try
            {
                using var synchronization = SharedStorePlatform.CreateSynchronization(
                    PlatformResourceName.Create(storeName));
                var status = synchronization.TryEnter(StoreWaitOptions.Infinite);
                if (status != StoreStatus.Success)
                {
                    throw new InvalidOperationException($"Unable to enter operation synchronizer: {status}.");
                }

                _ready.Set();
                _release.Wait();
                synchronization.Exit();
            }
            catch (Exception error)
            {
                _failure = error;
                _ready.Set();
            }
        }
    }

    private static class SequenceFactory
    {
        public static ReadOnlySequence<byte> Create(params byte[][] segments)
        {
            BufferSegment? first = null;
            BufferSegment? last = null;
            foreach (var segment in segments)
            {
                last = last is null ? first = new BufferSegment(segment) : last.Append(segment);
            }

            return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
        }
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(byte[] memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(byte[] memory)
        {
            var segment = new BufferSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
