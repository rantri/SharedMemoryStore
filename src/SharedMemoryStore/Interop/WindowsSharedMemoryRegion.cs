using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace SharedMemoryStore.Interop;

[SupportedOSPlatform("windows")]
internal static class WindowsSharedMemoryRegion
{
    public static StoreOpenStatus TryOpen(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        out MemoryMappedStoreRegion? region)
    {
        region = null;

        try
        {
            var mapping = options.OpenMode switch
            {
                OpenMode.CreateNew => MemoryMappedFile.CreateNew(
                    resourceName.WindowsRegionName,
                    options.TotalBytes,
                    MemoryMappedFileAccess.ReadWrite),
                OpenMode.OpenExisting => MemoryMappedFile.OpenExisting(
                    resourceName.WindowsRegionName,
                    MemoryMappedFileRights.ReadWrite),
                _ => MemoryMappedFile.CreateOrOpen(
                    resourceName.WindowsRegionName,
                    options.TotalBytes,
                    MemoryMappedFileAccess.ReadWrite)
            };

            var accessor = mapping.CreateViewAccessor(0, options.TotalBytes, MemoryMappedFileAccess.ReadWrite);
            region = MemoryMappedStoreRegion.Create(mapping, accessor, options.TotalBytes);
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
}
