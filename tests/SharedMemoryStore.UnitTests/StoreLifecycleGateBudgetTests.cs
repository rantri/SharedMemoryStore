using System.Buffers;
using System.Diagnostics;
using SharedMemoryStore.Diagnostics;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Lifecycle;

namespace SharedMemoryStore.UnitTests;

public sealed class StoreLifecycleGateBudgetTests
{
    [Fact]
    public void BoundedEntryPreservesExpiredCanceledAndTrueNoWaitSemantics()
    {
        var gate = new StoreLifecycleGate();
        long oldStart = Stopwatch.GetTimestamp() - Stopwatch.Frequency;

        Assert.Equal(
            StoreStatus.StoreBusy,
            gate.TryEnter(
                new StoreWaitOptions(TimeSpan.FromMilliseconds(10)),
                oldStart,
                out _));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(
            StoreStatus.OperationCanceled,
            gate.TryEnter(
                new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token),
                Stopwatch.GetTimestamp(),
                out _));

        Assert.Equal(
            StoreStatus.Success,
            gate.TryEnter(StoreWaitOptions.NoWait, Stopwatch.GetTimestamp(), out var operation));
        operation.Dispose();
    }

    [Fact]
    public async Task HighContentionEntryAndDisposalConvergeWithoutUnboundedEntrants()
    {
        var gate = new StoreLifecycleGate();
        using var start = new ManualResetEventSlim(initialState: false);
        int workerCount = Math.Max(8, Environment.ProcessorCount * 2);
        long successes = 0;
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                while (true)
                {
                    StoreStatus status = gate.TryEnter(
                        StoreWaitOptions.NoWait,
                        Stopwatch.GetTimestamp(),
                        out StoreLifecycleGate.Operation operation);
                    if (status == StoreStatus.StoreDisposed)
                    {
                        return;
                    }

                    if (status == StoreStatus.StoreBusy)
                    {
                        continue;
                    }

                    Assert.Equal(StoreStatus.Success, status);
                    Interlocked.Increment(ref successes);
                    Thread.SpinWait(16);
                    operation.Dispose();
                }
            }))
            .ToArray();

        start.Set();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref successes) >= 1_000, TimeSpan.FromSeconds(5)));
        Task disposer = Task.Run(() =>
        {
            Assert.True(gate.TryBeginDispose());
            gate.CompleteDispose();
        });

        await Task.WhenAll(workers.Append(disposer)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(successes > 0);
        Assert.True(gate.IsDisposed);
    }

    [Fact]
    public void FacadePassesRemainingFiniteTimeAndDoesNotEnterEngineWhenCanceled()
    {
        var engine = new RecordingEngine();
        using var store = new MemoryStore(engine);
        TimeSpan requested = TimeSpan.FromSeconds(1);

        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish([1], [2], [], new StoreWaitOptions(requested)));
        Assert.Equal(1, engine.PublishCalls);
        Assert.InRange(engine.LastWait.Timeout, TimeSpan.FromTicks(1), requested - TimeSpan.FromTicks(1));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryPublish(
                [1],
                [2],
                [],
                new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token)));
        Assert.Equal(1, engine.PublishCalls);
        Assert.Equal(1, engine.FacadeStatusCalls);
        Assert.Equal(StoreStatus.OperationCanceled, engine.LastFacadeStatus);
    }

    private sealed class RecordingEngine : IStoreEngine
    {
        internal int PublishCalls { get; private set; }
        internal int FacadeStatusCalls { get; private set; }
        internal StoreStatus LastFacadeStatus { get; private set; }
        internal StoreWaitOptions LastWait { get; private set; }
        public StoreProfile Profile => StoreProfile.LockFree;
        public StoreProtocolInfo ProtocolInfo => default;
        public StoreStatus RecordFacadeStatus(StoreStatus status)
        {
            FacadeStatusCalls++;
            LastFacadeStatus = status;
            return status;
        }

        public DiagnosticsSnapshot CreateDisposedDiagnosticsSnapshot() => default;

        public StoreStatus TryPublish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor, StoreWaitOptions waitOptions)
        {
            PublishCalls++;
            LastWait = waitOptions;
            return StoreStatus.Success;
        }

        public StoreStatus TryReserve(ReadOnlySpan<byte> key, int payloadLength, ReadOnlySpan<byte> descriptor, StoreWaitOptions waitOptions, out ReservationHandle reservation) { reservation = default; return StoreStatus.UnknownFailure; }
        public StoreStatus TryPublishSegments(ReadOnlySpan<byte> key, ReadOnlySequence<byte> payload, ReadOnlySpan<byte> descriptor, StoreWaitOptions waitOptions, out long copiedBytes) { copiedBytes = 0; return StoreStatus.UnknownFailure; }
        public StoreStatus TryAcquire(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions, out LeaseHandle lease) { lease = default; return StoreStatus.UnknownFailure; }
        public StoreStatus TryRemove(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions) => StoreStatus.UnknownFailure;
        public StoreStatus TryRecoverLeases(LeaseRecoveryOptions options, StoreWaitOptions waitOptions, out LeaseRecoveryReport report) { report = default; return StoreStatus.UnknownFailure; }
        public StoreStatus TryRecoverReservations(ReservationRecoveryOptions options, StoreWaitOptions waitOptions, out ReservationRecoveryReport report) { report = default; return StoreStatus.UnknownFailure; }
        public StoreStatus TryGetMetrics(StoreWaitOptions waitOptions, out EngineMetrics metrics) { metrics = default; return StoreStatus.UnknownFailure; }
        public StoreStatus TryGetDiagnostics(StoreWaitOptions waitOptions, out DiagnosticsSnapshot snapshot) { snapshot = default; return StoreStatus.UnknownFailure; }
        public bool IsReservationPending(ReservationHandle reservation) => false;
        public int GetReservationBytesWritten(ReservationHandle reservation) => 0;
        public Span<byte> GetReservationSpan(ReservationHandle reservation, int sizeHint) => [];
        public Memory<byte> DangerousGetReservationMemory(ReservationHandle reservation, int sizeHint) => Memory<byte>.Empty;
        public StoreStatus AdvanceReservation(ReservationHandle reservation, int byteCount, StoreWaitOptions waitOptions) => StoreStatus.UnknownFailure;
        public StoreStatus CommitReservation(ReservationHandle reservation, StoreWaitOptions waitOptions) => StoreStatus.UnknownFailure;
        public StoreStatus AbortReservation(ReservationHandle reservation, StoreWaitOptions waitOptions) => StoreStatus.UnknownFailure;
        public bool IsLeaseActive(LeaseHandle lease) => false;
        public int GetValueLength(LeaseHandle lease) => 0;
        public int GetDescriptorLength(LeaseHandle lease) => 0;
        public ReadOnlySpan<byte> GetValueSpan(LeaseHandle lease) => [];
        public ReadOnlySpan<byte> GetDescriptorSpan(LeaseHandle lease) => [];
        public StoreStatus ReleaseLease(LeaseHandle lease, StoreWaitOptions waitOptions) => StoreStatus.UnknownFailure;
        public void Dispose() { }
    }
}
