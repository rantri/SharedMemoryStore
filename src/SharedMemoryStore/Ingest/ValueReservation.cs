namespace SharedMemoryStore;

/// <summary>
/// Lifecycle token for one pending store-owned payload reservation.
/// </summary>
public struct ValueReservation : IDisposable
{
    private readonly SharedMemoryStore? _store;
    private readonly int _slotIndex;
    private readonly int _generation;
    private readonly int _payloadLength;

    internal ValueReservation(SharedMemoryStore store, int slotIndex, int generation, int payloadLength)
    {
        _store = store;
        _slotIndex = slotIndex;
        _generation = generation;
        _payloadLength = payloadLength;
    }

    /// <summary>Gets a value indicating whether this token still references a pending reservation.</summary>
    public readonly bool IsValid => _store?.IsReservationPending(_slotIndex, _generation) == true;

    /// <summary>Gets the announced payload length, in bytes.</summary>
    public readonly int PayloadLength => IsValid ? _payloadLength : 0;

    /// <summary>Gets the number of payload bytes advanced by the producer.</summary>
    public readonly int BytesWritten => _store?.GetReservationBytesWritten(_slotIndex, _generation) ?? 0;

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
            : _store.GetReservationSpan(_slotIndex, _generation, sizeHint);
    }

    /// <summary>
    /// Gets writable memory over remaining store-owned payload bytes while the reservation is pending.
    /// </summary>
    /// <param name="sizeHint">Minimum useful remaining size requested by the caller, or zero for any remaining bytes.</param>
    public readonly Memory<byte> GetMemory(int sizeHint = 0)
    {
        return _store is null
            ? Memory<byte>.Empty
            : _store.GetReservationMemory(_slotIndex, _generation, sizeHint);
    }

    /// <summary>
    /// Advances the exact number of payload bytes written into the current writable view.
    /// </summary>
    public readonly StoreStatus Advance(int byteCount)
    {
        return _store?.AdvanceReservation(_slotIndex, _generation, byteCount) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Commits the reservation atomically after exactly the announced payload length has been advanced.
    /// </summary>
    public readonly StoreStatus Commit()
    {
        return _store?.CommitReservation(_slotIndex, _generation) ?? StoreStatus.InvalidReservation;
    }

    /// <summary>
    /// Aborts the pending reservation, removes its pending key, and returns the slot to reusable storage.
    /// </summary>
    public readonly StoreStatus Abort()
    {
        return _store?.AbortReservation(_slotIndex, _generation, countAbort: true) ?? StoreStatus.InvalidReservation;
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
}
