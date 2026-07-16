using System.Runtime.CompilerServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Process-local access to the mapped store-wide control word. The control
/// word is the fail-closed boundary for persistent structural corruption; it
/// is never used for ordinary data-path mutual exclusion.
/// </summary>
internal sealed unsafe class LockFreeStoreControl : IDisposable
{
    private readonly long* _control;
    private int _disposed;

    internal LockFreeStoreControl(MemoryMappedStoreRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.Capacity < LayoutV2Constants.HeaderLength)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        _control = &((StoreHeaderV2*)region.Pointer)->Control;
    }

    internal bool IsReady => Validate() == StoreStatus.Success;

    /// <summary>
    /// Performs an acquire read of the mapped control state. Disposal is
    /// checked first so this method never dereferences an unmapped view.
    /// </summary>
    internal StoreStatus Validate()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return StoreStatus.StoreDisposed;
        }

        long observed = AtomicControlWord.LoadAcquire(ref *_control);
        return observed switch
        {
            LayoutV2Constants.StoreReady => StoreStatus.Success,
            LayoutV2Constants.StoreUnsupported => StoreStatus.UnsupportedPlatform,
            LayoutV2Constants.StoreCorrupt => StoreStatus.CorruptStore,
            _ => StoreStatus.CorruptStore
        };
    }

    /// <summary>
    /// Irreversibly publishes persistent mapped corruption. Only Ready may be
    /// changed; Unsupported and unknown future states are never overwritten.
    /// </summary>
    internal void MarkCorrupt()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        long observed = AtomicControlWord.LoadAcquire(ref *_control);
        while (observed == LayoutV2Constants.StoreReady)
        {
            long exchanged = AtomicControlWord.CompareExchange(
                ref *_control,
                LayoutV2Constants.StoreCorrupt,
                LayoutV2Constants.StoreReady);
            if (exchanged == LayoutV2Constants.StoreReady)
            {
                return;
            }

            observed = exchanged;
        }
    }

    internal static StoreStatus ReportCorruption(
        LockFreeStoreControl? control,
        string component,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
    {
        _ = LockFreeCorruptionTrace.Corrupt(component, member, line);
        control?.MarkCorrupt();
        return StoreStatus.CorruptStore;
    }

    public void Dispose() => Volatile.Write(ref _disposed, 1);
}
