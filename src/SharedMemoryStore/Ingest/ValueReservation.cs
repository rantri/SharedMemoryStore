using SharedMemoryStore.Layout;

namespace SharedMemoryStore;

/// <summary>
/// Lifecycle token for one pending store-owned payload reservation.
/// </summary>
public struct ValueReservation : IDisposable
{
    private readonly MemoryStore? _store;
    private readonly int _slotIndex;
    private readonly SlotLifecycleId _lifecycleId;
    private readonly int _payloadLength;

    internal ValueReservation(MemoryStore store, int slotIndex, SlotLifecycleId lifecycleId, int payloadLength)
    {
        _store = store;
        _slotIndex = slotIndex;
        _lifecycleId = lifecycleId;
        _payloadLength = payloadLength;
    }

    /// <summary>Gets a value indicating whether this token still references a pending reservation.</summary>
    public readonly bool IsValid => _store?.IsReservationPending(_slotIndex, _lifecycleId) == true;

    /// <summary>Gets the announced payload length, in bytes.</summary>
    public readonly int PayloadLength => IsValid ? _payloadLength : 0;

    /// <summary>Gets the number of payload bytes advanced by the producer.</summary>
    public readonly int BytesWritten => _store?.GetReservationBytesWritten(_slotIndex, _lifecycleId) ?? 0;

    /// <summary>Gets the number of payload bytes that remain before the reservation can commit.</summary>
    public readonly int RemainingBytes => Math.Max(0, PayloadLength - BytesWritten);

    /// <summary>
    /// Gets a writable span over remaining store-owned payload bytes while the reservation is pending.
    /// </summary>
    /// <param name="sizeHint">Minimum useful remaining size requested by the caller, or zero for any remaining bytes.</param>
    public readonly Span<byte> GetSpan(int sizeHint = 0)
    {
        return _store is null
            ? Span<byte>.Empty
            : _store.GetReservationSpan(_slotIndex, _lifecycleId, sizeHint);
    }

    /// <summary>
    /// Advances the exact number of payload bytes written into the current writable view.
    /// </summary>
    public readonly StoreStatus Advance(int byteCount)
    {
        return _store?.AdvanceReservation(_slotIndex, _lifecycleId, byteCount) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Advances the exact number of payload bytes written using the supplied wait policy.
    /// </summary>
    public readonly StoreStatus Advance(int byteCount, StoreWaitOptions waitOptions)
    {
        return _store?.AdvanceReservation(_slotIndex, _lifecycleId, byteCount, waitOptions) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Commits the reservation atomically after exactly the announced payload length has been advanced.
    /// </summary>
    public readonly StoreStatus Commit()
    {
        return _store?.CommitReservation(_slotIndex, _lifecycleId) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Commits the reservation using the supplied wait policy.
    /// </summary>
    public readonly StoreStatus Commit(StoreWaitOptions waitOptions)
    {
        return _store?.CommitReservation(_slotIndex, _lifecycleId, waitOptions) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Aborts the pending reservation, removes its pending key, and returns the slot to reusable storage.
    /// </summary>
    public readonly StoreStatus Abort()
    {
        return _store?.AbortReservation(_slotIndex, _lifecycleId, countAbort: true) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Aborts the reservation using the supplied wait policy.
    /// </summary>
    public readonly StoreStatus Abort(StoreWaitOptions waitOptions)
    {
        return _store?.AbortReservation(_slotIndex, _lifecycleId, countAbort: true, waitOptions) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Aborts the reservation when it is still pending; completed reservations are left unchanged.
    /// </summary>
    public readonly void Dispose()
    {
        if (IsValid)
        {
            _ = Abort();
        }
    }

    internal readonly int SlotIndexForTesting => _slotIndex;

    internal readonly SlotLifecycleId LifecycleIdForTesting => _lifecycleId;
}
