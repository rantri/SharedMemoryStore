namespace SharedMemoryStore.Interop;

/// <summary>
/// Owns one cold create/open transaction. The ordinary synchronization gate
/// and, on Linux, the outer lifecycle gate remain held until header validation
/// and participant registration finish. Mapped resource ownership can then be
/// transferred to the constructed engine without relying on lock reentrancy.
/// </summary>
internal sealed class SharedStoreOpenScope : IDisposable
{
    private readonly IDisposable? _outerLifecycleGate;
    private int _disposed;
    private bool _resourcesTransferred;

    internal SharedStoreOpenScope(
        MemoryMappedStoreRegion region,
        ISharedStoreSynchronization synchronization,
        IDisposable? outerLifecycleGate,
        RegionOpenDisposition disposition)
    {
        Region = region;
        Synchronization = synchronization;
        _outerLifecycleGate = outerLifecycleGate;
        Disposition = disposition;
    }

    internal MemoryMappedStoreRegion Region { get; }

    internal ISharedStoreSynchronization Synchronization { get; }

    internal RegionOpenDisposition Disposition { get; }

    /// <summary>
    /// Transfers the mapped region and ordinary synchronization object to a
    /// fully constructed core/engine. The scope continues to own only the held
    /// gate state and releases it when disposed.
    /// </summary>
    internal void TransferResourceOwnership()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _resourcesTransferred = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Synchronization.Exit();
        }
        finally
        {
            try
            {
                // Linux acquires .lifecycle before .lock, so release the
                // ordinary gate before the outer lifecycle rendezvous.
                _outerLifecycleGate?.Dispose();
            }
            finally
            {
                if (!_resourcesTransferred)
                {
                    try
                    {
                        // Region disposal may acquire Linux .lifecycle through
                        // owner cleanup and must therefore run after both held
                        // gates have been released.
                        Region.Dispose();
                    }
                    finally
                    {
                        Synchronization.Dispose();
                    }
                }
            }
        }
    }
}
