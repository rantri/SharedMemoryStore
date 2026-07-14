using System.Buffers;
using SharedMemoryStore.Diagnostics;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Ingest;

namespace SharedMemoryStore.UnitTests;

public sealed class StoreEngineFactoryOwnershipTests
{
    [Fact]
    public void FacadeConstructionFailureDisposesTransferredEngineExactlyOnce()
    {
        var engine = new ThrowingProfileEngine();

        Assert.Throws<InjectedFacadeConstructionException>(
            () => StoreEngineFactory.WrapOwnedEngine(engine));

        Assert.Equal(1, engine.DisposeCount);
    }

    private sealed class InjectedFacadeConstructionException : Exception;

    private sealed class ThrowingProfileEngine : IStoreEngine
    {
        internal int DisposeCount { get; private set; }

        public StoreProfile Profile => throw new InjectedFacadeConstructionException();
        public StoreProtocolInfo ProtocolInfo => default;
        public StoreStatus RecordFacadeStatus(StoreStatus status) => status;
        public DiagnosticsSnapshot CreateDisposedDiagnosticsSnapshot() => default;
        public StoreStatus TryPublish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor, StoreWaitOptions waitOptions) => StoreStatus.UnknownFailure;
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
        public void Dispose() => DisposeCount++;
    }
}
