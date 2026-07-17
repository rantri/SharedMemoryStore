using SharedMemoryStore.Engines;
using System.Runtime.CompilerServices;

namespace SharedMemoryStore;

/// <summary>
/// Exclusive single-producer lifecycle token for one pending store-owned payload reservation.
/// The struct may be copied for ordinary value passing, but concurrent lifecycle or writable-view
/// calls through copied tokens are unsupported. Safe writable views end at the next lifecycle
/// action and are invalid after commit, abort, recovery, token disposal, or store disposal.
/// </summary>
public struct ValueReservation : IDisposable
{
    private readonly MemoryStore? _store;
    private readonly ReservationHandle _handle;

    internal ValueReservation(MemoryStore store, in ReservationHandle handle)
    {
        _store = store;
        _handle = handle;
    }

    /// <summary>Gets a value indicating whether this token still references a pending reservation.</summary>
    public readonly bool IsValid => _store?.IsReservationPending(_handle) == true;

    /// <summary>Gets the announced payload length, in bytes.</summary>
    public readonly int PayloadLength => IsValid ? _handle.PayloadLength : 0;

    /// <summary>Gets the number of payload bytes advanced by the producer.</summary>
    public readonly int BytesWritten => _store?.GetReservationBytesWritten(_handle) ?? 0;

    /// <summary>Gets the number of payload bytes that remain before the reservation can commit.</summary>
    public readonly int RemainingBytes => Math.Max(0, PayloadLength - BytesWritten);

    /// <summary>
    /// Gets an immediate writable span over remaining store-owned payload bytes while pending.
    /// The span is borrowed until the next reservation lifecycle action.
    /// </summary>
    public readonly Span<byte> GetSpan(int sizeHint = 0) =>
        _store is null ? Span<byte>.Empty : _store.GetReservationSpan(_handle, sizeHint);

    /// <summary>
    /// Gets retained-capable writable memory whose logical lifetime is still bounded by this
    /// reservation; accessing it after that lifetime is explicitly unsafe.
    /// </summary>
    public readonly Memory<byte> DangerousGetMemory(int sizeHint = 0) =>
        _store is null ? Memory<byte>.Empty : _store.GetReservationMemory(_handle, sizeHint);

    /// <summary>Advances the exact number of payload bytes written into the current writable view.</summary>
    public readonly StoreStatus Advance(int byteCount) => Advance(byteCount, StoreWaitOptions.Default);

    /// <summary>Advances written bytes using the supplied bounded wait policy.</summary>
    public readonly StoreStatus Advance(int byteCount, StoreWaitOptions waitOptions) =>
        _store?.AdvanceReservation(_handle, byteCount, waitOptions) ?? StoreStatus.InvalidReservation;

    /// <summary>Commits the reservation after exactly the announced payload length has been advanced.</summary>
    public readonly StoreStatus Commit() => Commit(StoreWaitOptions.Default);

    /// <summary>Commits the reservation using the supplied bounded wait policy.</summary>
    public readonly StoreStatus Commit(StoreWaitOptions waitOptions) =>
        _store?.CommitReservation(_handle, waitOptions) ?? StoreStatus.InvalidReservation;

    /// <summary>Aborts the pending reservation and makes its storage reusable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public readonly StoreStatus Abort() => Abort(StoreWaitOptions.Default);

    /// <summary>Aborts the reservation using the supplied bounded wait policy.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public readonly StoreStatus Abort(StoreWaitOptions waitOptions) =>
        _store?.AbortReservation(_handle, waitOptions) ?? StoreStatus.InvalidReservation;

    /// <summary>Best-effort abort of a still-current reservation.</summary>
    public readonly void Dispose()
    {
        if (IsValid)
        {
            _ = Abort();
        }
    }

    internal readonly ReservationHandle HandleForEngine => _handle;
}
