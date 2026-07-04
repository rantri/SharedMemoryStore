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
        long capacity,
        Action? disposeCallback)
    {
        _mapping = mapping;
        _accessor = accessor;
        _disposeCallback = disposeCallback;
        Capacity = capacity;
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
        long capacity,
        Action? disposeCallback = null)
    {
        return new MemoryMappedStoreRegion(mapping, accessor, capacity, disposeCallback);
    }

    public static StoreOpenStatus TryOpen(SharedMemoryStoreOptions options, out MemoryMappedStoreRegion? region)
    {
        return SharedStorePlatform.TryOpenRegion(options, out region);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer is not null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _accessor.Dispose();
        _mapping.Dispose();
        _disposeCallback?.Invoke();
    }
}
