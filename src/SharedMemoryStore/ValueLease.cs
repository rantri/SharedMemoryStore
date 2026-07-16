using SharedMemoryStore.Engines;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore;

/// <summary>
/// Shared read-lease token that protects one published value generation and projects read-only
/// spans over shared memory. Several leases may protect the same generation. Borrowed spans remain
/// valid only until this exact lease is released or recovered, or until the local store is disposed.
/// </summary>
public readonly struct ValueLease : IDisposable
{
    private readonly MemoryStore? _store;
    private readonly LeaseHandle _handle;

    internal ValueLease(MemoryStore store, in LeaseHandle handle)
    {
        _store = store;
        _handle = handle;
    }

    internal ValueLease(MemoryStore store, int slotIndex, SlotLifecycleId lifecycleId, int leaseRecordId)
        : this(store, store.CreateLegacyLeaseHandle(slotIndex, lifecycleId, leaseRecordId))
    {
    }

    /// <summary>Gets a value indicating whether this token still references an active lease record.</summary>
    public bool IsValid => _store?.IsLeaseActive(_handle) == true;

    /// <summary>Gets the value span length for the protected slot generation.</summary>
    public int ValueLength => _store?.GetValueLength(_handle) ?? 0;

    /// <summary>Gets the descriptor span length for the protected slot generation.</summary>
    public int DescriptorLength => _store?.GetDescriptorLength(_handle) ?? 0;

    /// <summary>
    /// Gets a borrowed read-only span over the protected value bytes, valid until lease release,
    /// recovery, or local store disposal.
    /// </summary>
    public ReadOnlySpan<byte> ValueSpan =>
        _store is null ? ReadOnlySpan<byte>.Empty : _store.GetValueSpan(_handle);

    /// <summary>
    /// Gets a borrowed read-only span over the protected descriptor bytes, valid until lease
    /// release, recovery, or local store disposal.
    /// </summary>
    public ReadOnlySpan<byte> DescriptorSpan =>
        _store is null ? ReadOnlySpan<byte>.Empty : _store.GetDescriptorSpan(_handle);

    /// <summary>Releases the lease exactly once and returns the deterministic release status.</summary>
    public StoreStatus Release() =>
        _store?.ReleaseLease(_handle, StoreWaitOptions.Default) ?? StoreStatus.InvalidLease;

    /// <summary>
    /// Releases the lease exactly once using the supplied profile-specific bounded wait policy.
    /// </summary>
    public StoreStatus Release(StoreWaitOptions waitOptions) =>
        _store?.ReleaseLease(_handle, waitOptions) ?? StoreStatus.InvalidLease;

    /// <summary>Releases the lease on a best-effort basis when it is still active.</summary>
    public void Dispose() => _ = Release();

    internal int LeaseRecordIdForTesting => MemoryStore.DecodeLegacyLeaseRecordId(_handle);

    internal int SlotIndexForTesting => MemoryStore.DecodeLegacySlotIndex(_handle.SlotBinding);

    internal SlotLifecycleId LifecycleIdForTesting => MemoryStore.DecodeLegacyLifecycle(_handle);

    internal LeaseHandle HandleForEngine => _handle;
}
