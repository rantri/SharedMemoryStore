using System.IO;
using System.IO.MemoryMappedFiles;

namespace SharedMemoryStore.Interop;

internal sealed unsafe class MemoryMappedStoreRegion : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _pointer;
    private bool _disposed;

    private MemoryMappedStoreRegion(MemoryMappedFile mapping, MemoryMappedViewAccessor accessor, long capacity)
    {
        _mapping = mapping;
        _accessor = accessor;
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

    public static StoreOpenStatus TryOpen(SharedMemoryStoreOptions options, out MemoryMappedStoreRegion? region)
    {
        region = null;

        if (!OperatingSystem.IsWindows())
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        try
        {
            var mapping = options.OpenMode switch
            {
                OpenMode.CreateNew => MemoryMappedFile.CreateNew(options.Name, options.TotalBytes, MemoryMappedFileAccess.ReadWrite),
                OpenMode.OpenExisting => MemoryMappedFile.OpenExisting(options.Name, MemoryMappedFileRights.ReadWrite),
                _ => MemoryMappedFile.CreateOrOpen(options.Name, options.TotalBytes, MemoryMappedFileAccess.ReadWrite)
            };

            var accessor = mapping.CreateViewAccessor(0, options.TotalBytes, MemoryMappedFileAccess.ReadWrite);
            region = new MemoryMappedStoreRegion(mapping, accessor, options.TotalBytes);
            return StoreOpenStatus.Success;
        }
        catch (FileNotFoundException) when (options.OpenMode == OpenMode.OpenExisting)
        {
            return StoreOpenStatus.NotFound;
        }
        catch (IOException) when (options.OpenMode == OpenMode.CreateNew)
        {
            return StoreOpenStatus.AlreadyExists;
        }
        catch (UnauthorizedAccessException)
        {
            return StoreOpenStatus.AccessDenied;
        }
        catch (PlatformNotSupportedException)
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }
        catch (ArgumentException)
        {
            return StoreOpenStatus.InvalidOptions;
        }
        catch (Exception)
        {
            return StoreOpenStatus.MappingFailed;
        }
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
    }
}
