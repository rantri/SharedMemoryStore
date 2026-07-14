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
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? accessor = null;

        try
        {
            mapping = options.OpenMode switch
            {
                OpenMode.CreateNew => MemoryMappedFile.CreateNew(
                    resourceName.WindowsRegionName,
                    options.TotalBytes,
                    MemoryMappedFileAccess.ReadWrite),
                OpenMode.OpenExisting => MemoryMappedFile.OpenExisting(
                    resourceName.WindowsRegionName,
                    MemoryMappedFileRights.ReadWrite),
                _ => OpenExistingOrCreate(resourceName.WindowsRegionName, options.TotalBytes)
            };

            // A zero-length view projects the actual mapping capacity. In particular, an
            // opener with larger requested dimensions can still inspect an existing small
            // mapping's header and report IncompatibleLayout instead of failing view creation.
            accessor = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            region = MemoryMappedStoreRegion.Create(mapping, accessor);
            mapping = null;
            accessor = null;
            return StoreOpenStatus.Success;
        }
        catch (FileNotFoundException) when (options.OpenMode == OpenMode.OpenExisting)
        {
            return StoreOpenStatus.NotFound;
        }
        catch (IOException) when (options.OpenMode == OpenMode.CreateNew && mapping is null)
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
        finally
        {
            accessor?.Dispose();
            mapping?.Dispose();
        }
    }

    private static MemoryMappedFile OpenExistingOrCreate(string mappingName, long requestedCapacity)
    {
        try
        {
            return MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite);
        }
        catch (FileNotFoundException)
        {
            try
            {
                return MemoryMappedFile.CreateNew(
                    mappingName,
                    requestedCapacity,
                    MemoryMappedFileAccess.ReadWrite);
            }
            catch (IOException)
            {
                // Another creator won after the initial probe. Opening its mapping also
                // avoids projecting our requested capacity onto that existing resource.
                return MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite);
            }
        }
    }
}
