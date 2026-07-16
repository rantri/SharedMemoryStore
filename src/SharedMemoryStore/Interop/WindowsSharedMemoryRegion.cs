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
        return TryOpen(resourceName, options, out region, out _);
    }

    internal static StoreOpenStatus TryOpen(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        out MemoryMappedStoreRegion? region,
        out RegionOpenDisposition disposition)
    {
        region = null;
        disposition = default;
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? accessor = null;

        try
        {
            switch (options.OpenMode)
            {
                case OpenMode.CreateNew:
                    mapping = MemoryMappedFile.CreateNew(
                        resourceName.WindowsRegionName,
                        options.TotalBytes,
                        MemoryMappedFileAccess.ReadWrite);
                    disposition = RegionOpenDisposition.CreatedNew;
                    break;

                case OpenMode.OpenExisting:
                    mapping = MemoryMappedFile.OpenExisting(
                        resourceName.WindowsRegionName,
                        MemoryMappedFileRights.ReadWrite);
                    disposition = RegionOpenDisposition.OpenedExisting;
                    break;

                default:
                    mapping = OpenExistingOrCreate(
                        resourceName.WindowsRegionName,
                        options.TotalBytes,
                        out bool createdNew);
                    disposition = createdNew
                        ? RegionOpenDisposition.CreatedNew
                        : RegionOpenDisposition.OpenedExisting;
                    break;
            }

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

    private static MemoryMappedFile OpenExistingOrCreate(
        string mappingName,
        long requestedCapacity,
        out bool createdNew)
    {
        try
        {
            MemoryMappedFile existing = MemoryMappedFile.OpenExisting(
                mappingName,
                MemoryMappedFileRights.ReadWrite);
            createdNew = false;
            return existing;
        }
        catch (FileNotFoundException)
        {
            try
            {
                MemoryMappedFile created = MemoryMappedFile.CreateNew(
                    mappingName,
                    requestedCapacity,
                    MemoryMappedFileAccess.ReadWrite);
                createdNew = true;
                return created;
            }
            catch (IOException)
            {
                // Another creator won after the initial probe. Opening its mapping also
                // avoids projecting our requested capacity onto that existing resource.
                MemoryMappedFile existing = MemoryMappedFile.OpenExisting(
                    mappingName,
                    MemoryMappedFileRights.ReadWrite);
                createdNew = false;
                return existing;
            }
        }
    }
}
