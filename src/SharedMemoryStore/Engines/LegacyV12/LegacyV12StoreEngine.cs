using System.Buffers;

namespace SharedMemoryStore.Engines.LegacyV12;

/// <summary>
/// Layout-v1.2 engine adapter. The contained core preserves the frozen v1.2
/// implementation while the public <see cref="MemoryStore"/> stays profile-neutral.
/// </summary>
internal sealed class LegacyV12StoreEngine : IStoreEngine
{
    internal LegacyV12StoreEngine(MemoryStore core)
    {
        Core = core;
    }

    internal MemoryStore Core { get; }

    public StoreProfile Profile => StoreProfile.Legacy;

    public StoreProtocolInfo ProtocolInfo => new(StoreProfile.Legacy, 1, 2, 1, 0, 0);

    public StoreStatus RecordFacadeStatus(StoreStatus status) =>
        Core.RecordFacadeStatus(status);

    public DiagnosticsSnapshot CreateDisposedDiagnosticsSnapshot() =>
        Core.CreateDisposedSnapshot();

    public StoreStatus TryPublish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor, StoreWaitOptions waitOptions) =>
        Core.TryPublish(key, value, descriptor, waitOptions);

    public StoreStatus TryReserve(ReadOnlySpan<byte> key, int payloadLength, ReadOnlySpan<byte> descriptor, StoreWaitOptions waitOptions, out ReservationHandle reservation)
    {
        var status = Core.TryReserve(key, payloadLength, descriptor, waitOptions, out var publicReservation);
        reservation = status == StoreStatus.Success ? publicReservation.HandleForEngine : default;
        return status;
    }

    public StoreStatus TryPublishSegments(ReadOnlySpan<byte> key, ReadOnlySequence<byte> payload, ReadOnlySpan<byte> descriptor, StoreWaitOptions waitOptions, out long copiedBytes) =>
        Core.TryPublishSegments(key, payload, descriptor, waitOptions, out copiedBytes);

    public StoreStatus TryAcquire(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions, out LeaseHandle lease)
    {
        var status = Core.TryAcquire(key, waitOptions, out var publicLease);
        lease = status == StoreStatus.Success ? publicLease.HandleForEngine : default;
        return status;
    }

    public StoreStatus TryRemove(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions) => Core.TryRemove(key, waitOptions);

    public StoreStatus TryRecoverLeases(LeaseRecoveryOptions options, StoreWaitOptions waitOptions, out LeaseRecoveryReport report) =>
        Core.TryRecoverLeases(options, waitOptions, out report);

    public StoreStatus TryRecoverReservations(ReservationRecoveryOptions options, StoreWaitOptions waitOptions, out ReservationRecoveryReport report) =>
        Core.TryRecoverReservations(options, waitOptions, out report);

    public StoreStatus TryGetMetrics(StoreWaitOptions waitOptions, out EngineMetrics metrics)
    {
        var status = Core.TryGetDiagnostics(waitOptions, out var snapshot);
        metrics = status == StoreStatus.Success
            ? new EngineMetrics
            {
                TotalBytes = snapshot.TotalBytes,
                SlotCount = snapshot.SlotCount,
                FreeSlotCount = snapshot.FreeSlotCount,
                ReservedSlotCount = snapshot.ActiveReservationCount,
                PublishedSlotCount = snapshot.PublishedSlotCount,
                PendingRemovalCount = snapshot.PendingRemovalCount,
                ActiveLeaseCount = snapshot.ActiveLeaseCount,
                IndexEntryCount = snapshot.IndexEntryCount,
                OccupiedIndexEntryCount = snapshot.OccupiedIndexEntryCount,
                TombstoneIndexEntryCount = snapshot.TombstoneIndexEntryCount,
                EmptyIndexEntryCount = snapshot.EmptyIndexEntryCount,
                UsableIndexCapacity = snapshot.UsableIndexCapacity,
                LastObservedProbeLength = snapshot.LastObservedProbeLength,
                MaxObservedProbeLength = snapshot.MaxObservedProbeLength,
                IndexCompactionCount = snapshot.IndexCompactionCount
            }
            : default;
        return status;
    }

    public StoreStatus TryGetDiagnostics(StoreWaitOptions waitOptions, out DiagnosticsSnapshot snapshot) =>
        Core.TryGetDiagnostics(waitOptions, out snapshot);

    public bool IsReservationPending(ReservationHandle reservation) => Core.IsReservationPending(reservation);
    public int GetReservationBytesWritten(ReservationHandle reservation) => Core.GetReservationBytesWritten(reservation);
    public Span<byte> GetReservationSpan(ReservationHandle reservation, int sizeHint) => Core.GetReservationSpan(reservation, sizeHint);
    public Memory<byte> DangerousGetReservationMemory(ReservationHandle reservation, int sizeHint) => Core.GetReservationMemory(reservation, sizeHint);
    public StoreStatus AdvanceReservation(ReservationHandle reservation, int byteCount, StoreWaitOptions waitOptions) => Core.AdvanceReservation(reservation, byteCount, waitOptions);
    public StoreStatus CommitReservation(ReservationHandle reservation, StoreWaitOptions waitOptions) => Core.CommitReservation(reservation, waitOptions);
    public StoreStatus AbortReservation(ReservationHandle reservation, StoreWaitOptions waitOptions) => Core.AbortReservation(reservation, countAbort: true, waitOptions);
    public bool IsLeaseActive(LeaseHandle lease) => Core.IsLeaseActive(lease);
    public int GetValueLength(LeaseHandle lease) =>
        Core.IsLeaseActive(lease) ? Core.GetValueLength(lease) : 0;
    public int GetDescriptorLength(LeaseHandle lease) =>
        Core.IsLeaseActive(lease) ? Core.GetDescriptorLength(lease) : 0;
    public ReadOnlySpan<byte> GetValueSpan(LeaseHandle lease) =>
        Core.IsLeaseActive(lease) ? Core.GetValueSpan(lease) : ReadOnlySpan<byte>.Empty;
    public ReadOnlySpan<byte> GetDescriptorSpan(LeaseHandle lease) =>
        Core.IsLeaseActive(lease) ? Core.GetDescriptorSpan(lease) : ReadOnlySpan<byte>.Empty;
    public StoreStatus ReleaseLease(LeaseHandle lease, StoreWaitOptions waitOptions) => Core.ReleaseLease(lease, waitOptions);
    public void Dispose() => Core.Dispose();
}
