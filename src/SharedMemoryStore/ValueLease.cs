namespace SharedMemoryStore;

/// <summary>
/// Struct token that protects one published value generation and projects read-only spans over shared memory.
/// </summary>
public readonly struct ValueLease : IDisposable
{
    private readonly SharedMemoryStore? _store;
    private readonly int _slotIndex;
    private readonly int _generation;
    private readonly int _leaseRecordId;

    internal ValueLease(SharedMemoryStore store, int slotIndex, int generation, int leaseRecordId)
    {
        _store = store;
        _slotIndex = slotIndex;
        _generation = generation;
        _leaseRecordId = leaseRecordId;
    }

    /// <summary>Gets a value indicating whether this token still references an active lease record.</summary>
    public bool IsValid => IsActive;

    /// <summary>Gets the value span length for the protected slot generation.</summary>
    public int ValueLength => IsActive ? _store!.GetValueLength(_slotIndex, _generation) : 0;

    /// <summary>Gets the descriptor span length for the protected slot generation.</summary>
    public int DescriptorLength => IsActive ? _store!.GetDescriptorLength(_slotIndex, _generation) : 0;

    /// <summary>Gets a read-only span over the protected value bytes.</summary>
    public ReadOnlySpan<byte> ValueSpan => IsActive ? _store!.GetValueSpan(_slotIndex, _generation) : ReadOnlySpan<byte>.Empty;

    /// <summary>Gets a read-only span over the protected descriptor bytes.</summary>
    public ReadOnlySpan<byte> DescriptorSpan => IsActive ? _store!.GetDescriptorSpan(_slotIndex, _generation) : ReadOnlySpan<byte>.Empty;

    /// <summary>
    /// Releases the lease exactly once.
    /// </summary>
    public StoreStatus Release()
    {
        return _store?.ReleaseLease(_slotIndex, _generation, _leaseRecordId) ?? StoreStatus.InvalidLease;
    }

    /// <summary>
    /// Releases the lease when it is still active.
    /// </summary>
    public void Dispose()
    {
        _ = Release();
    }

    private bool IsActive => _store?.IsLeaseActive(_slotIndex, _generation, _leaseRecordId) == true;
}
