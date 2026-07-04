namespace SharedMemoryStore.Interop;

internal static class SharedStorePlatform
{
    public static StoreOpenStatus TryOpen(
        SharedMemoryStoreOptions options,
        out MemoryMappedStoreRegion? region,
        out ISharedStoreSynchronization? synchronization)
    {
        region = null;
        synchronization = null;

        var status = TryOpenRegion(options, out region);
        if (status != StoreOpenStatus.Success || region is null)
        {
            return status;
        }

        try
        {
            synchronization = CreateSynchronization(PlatformResourceName.Create(options.Name));
            return StoreOpenStatus.Success;
        }
        catch (UnauthorizedAccessException)
        {
            region.Dispose();
            region = null;
            return StoreOpenStatus.AccessDenied;
        }
        catch (PlatformNotSupportedException)
        {
            region.Dispose();
            region = null;
            return StoreOpenStatus.UnsupportedPlatform;
        }
        catch
        {
            region.Dispose();
            region = null;
            return StoreOpenStatus.MappingFailed;
        }
    }

    public static StoreOpenStatus TryOpenRegion(
        SharedMemoryStoreOptions options,
        out MemoryMappedStoreRegion? region)
    {
        region = null;
        var resourceName = PlatformResourceName.Create(options.Name);
        if (OperatingSystem.IsWindows())
        {
            return WindowsSharedMemoryRegion.TryOpen(resourceName, options, out region);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxSharedMemoryRegion.TryOpen(resourceName, options, out region);
        }

        return StoreOpenStatus.UnsupportedPlatform;
    }

    public static ISharedStoreSynchronization CreateSynchronization(PlatformResourceName resourceName)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSharedStoreSynchronization(resourceName);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSharedStoreSynchronization(resourceName);
        }

        throw new PlatformNotSupportedException("SharedMemoryStore supports Linux and Windows shared synchronization.");
    }
}
