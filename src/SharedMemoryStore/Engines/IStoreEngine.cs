using System.Buffers;

namespace SharedMemoryStore.Engines;

/// <summary>
/// Synchronous profile-neutral implementation boundary behind
/// <see cref="MemoryStore"/>.
/// </summary>
/// <remarks>
/// Span-returning members validate their opaque handle synchronously and never
/// retain a caller span. Returned mapped-memory views remain governed by the
/// corresponding reservation or lease lifetime. Implementations must not use
/// asynchronous continuations or hidden background workers.
/// </remarks>
internal interface IStoreEngine : IDisposable
{
    StoreProfile Profile { get; }

    StoreProtocolInfo ProtocolInfo { get; }

    /// <summary>
    /// Records a non-success status produced by the facade before engine entry.
    /// Implementations must update managed-local diagnostics only and must not
    /// touch mapped memory, synchronization objects, or other disposable state.
    /// </summary>
    StoreStatus RecordFacadeStatus(StoreStatus status);

    /// <summary>
    /// Creates the best available diagnostic snapshot after facade disposal has
    /// closed operation entry. Implementations must use only immutable layout
    /// metadata and managed-local counters; this member must never project or
    /// scan disposed mapped memory or acquire disposable synchronization.
    /// </summary>
    DiagnosticsSnapshot CreateDisposedDiagnosticsSnapshot();

    StoreStatus TryPublish(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions);

    StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out ReservationHandle reservation);

    StoreStatus TryPublishSegments(
        ReadOnlySpan<byte> key,
        ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out long copiedBytes);

    StoreStatus TryAcquire(
        ReadOnlySpan<byte> key,
        StoreWaitOptions waitOptions,
        out LeaseHandle lease);

    StoreStatus TryRemove(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions);

    StoreStatus TryRecoverLeases(
        LeaseRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out LeaseRecoveryReport report);

    StoreStatus TryRecoverReservations(
        ReservationRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out ReservationRecoveryReport report);

    StoreStatus TryGetMetrics(StoreWaitOptions waitOptions, out EngineMetrics metrics);

    StoreStatus TryGetDiagnostics(StoreWaitOptions waitOptions, out DiagnosticsSnapshot snapshot);

    bool IsReservationPending(ReservationHandle reservation);

    int GetReservationBytesWritten(ReservationHandle reservation);

    Span<byte> GetReservationSpan(ReservationHandle reservation, int sizeHint);

    Memory<byte> DangerousGetReservationMemory(ReservationHandle reservation, int sizeHint);

    StoreStatus AdvanceReservation(
        ReservationHandle reservation,
        int byteCount,
        StoreWaitOptions waitOptions);

    StoreStatus CommitReservation(
        ReservationHandle reservation,
        StoreWaitOptions waitOptions);

    StoreStatus AbortReservation(
        ReservationHandle reservation,
        StoreWaitOptions waitOptions);

    bool IsLeaseActive(LeaseHandle lease);

    int GetValueLength(LeaseHandle lease);

    int GetDescriptorLength(LeaseHandle lease);

    ReadOnlySpan<byte> GetValueSpan(LeaseHandle lease);

    ReadOnlySpan<byte> GetDescriptorSpan(LeaseHandle lease);

    StoreStatus ReleaseLease(LeaseHandle lease, StoreWaitOptions waitOptions);
}
