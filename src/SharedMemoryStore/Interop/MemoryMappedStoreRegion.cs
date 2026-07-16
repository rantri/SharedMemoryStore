using System.IO;
using System.IO.MemoryMappedFiles;

namespace SharedMemoryStore.Interop;

internal sealed unsafe class MemoryMappedStoreRegion : ISharedStoreRegion
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Action? _disposeCallback;
    private byte* _pointer;
    private bool _disposed;

    private MemoryMappedStoreRegion(
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor accessor,
        Action? disposeCallback)
    {
        _mapping = mapping;
        _accessor = accessor;
        _disposeCallback = disposeCallback;
        Capacity = accessor.Capacity;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
    }

    public long Capacity { get; }

    public byte* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pointer;
        }
    }

    public static MemoryMappedStoreRegion Create(
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor accessor,
        Action? disposeCallback = null)
    {
        return new MemoryMappedStoreRegion(mapping, accessor, disposeCallback);
    }

    public static StoreOpenStatus TryOpen(SharedMemoryStoreOptions options, out MemoryMappedStoreRegion? region)
    {
        return SharedStorePlatform.TryOpenRegion(options, StoreWaitOptions.Default, out region);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_pointer is not null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointer = null;
            }
        }
        finally
        {
            try
            {
                _accessor.Dispose();
            }
            finally
            {
                try
                {
                    _mapping.Dispose();
                }
                finally
                {
                    // Linux owner cleanup must run only after the view is unmapped, and it
                    // must still run when an earlier local-handle teardown reports an error.
                    _disposeCallback?.Invoke();
                }
            }
        }
    }
}
