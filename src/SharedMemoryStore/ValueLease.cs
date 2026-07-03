using SharedMemoryStore.Layout;

namespace SharedMemoryStore;

/// <summary>
/// Struct token that protects one published value generation and projects read-only spans over shared memory.
/// </summary>
public readonly struct ValueLease : IDisposable
{
    private readonly SharedMemoryStore? _store;
    private readonly int _slotIndex;
    private readonly SlotLifecycleId _lifecycleId;
    private readonly int _leaseRecordId;

    internal ValueLease(SharedMemoryStore store, int slotIndex, SlotLifecycleId lifecycleId, int leaseRecordId)
    {
        _store = store;
        _slotIndex = slotIndex;
        _lifecycleId = lifecycleId;
        _leaseRecordId = leaseRecordId;
    }

    /// <summary>Gets a value indicating whether this token still references an active lease record.</summary>
    public bool IsValid => IsActive;

    /// <summary>Gets the value span length for the protected slot generation.</summary>
    public int ValueLength => IsActive ? _store!.GetValueLength(_slotIndex, _lifecycleId) : 0;

    /// <summary>Gets the descriptor span length for the protected slot generation.</summary>
    public int DescriptorLength => IsActive ? _store!.GetDescriptorLength(_slotIndex, _lifecycleId) : 0;

    /// <summary>Gets a read-only span over the protected value bytes.</summary>
    public ReadOnlySpan<byte> ValueSpan => IsActive ? _store!.GetValueSpan(_slotIndex, _lifecycleId) : ReadOnlySpan<byte>.Empty;

    /// <summary>Gets a read-only span over the protected descriptor bytes.</summary>
    public ReadOnlySpan<byte> DescriptorSpan => IsActive ? _store!.GetDescriptorSpan(_slotIndex, _lifecycleId) : ReadOnlySpan<byte>.Empty;

    /// <summary>
    /// Releases the lease exactly once.
    /// </summary>
    public StoreStatus Release()
    {
        return _store?.ReleaseLease(_slotIndex, _lifecycleId, _leaseRecordId) ?? StoreStatus.InvalidLease;
    }

    /// <summary>
    /// Releases the lease when it is still active.
    /// </summary>
    public void Dispose()
    {
        _ = Release();
    }

    internal int LeaseRecordIdForTesting => _leaseRecordId;

    internal int SlotIndexForTesting => _slotIndex;

    internal SlotLifecycleId LifecycleIdForTesting => _lifecycleId;

    private bool IsActive => _store?.IsLeaseActive(_slotIndex, _lifecycleId, _leaseRecordId) == true;
}
