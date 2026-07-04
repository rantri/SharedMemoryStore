using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace SharedMemoryStore.Interop;

[SupportedOSPlatform("linux")]
internal static class LinuxSharedMemoryRegion
{
    public static StoreOpenStatus TryOpen(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        out MemoryMappedStoreRegion? region)
    {
        region = null;

        var lifecycleLockStatus = LinuxFileLock.TryAcquire(
            resourceName.LinuxLifecycleLockPath,
            StoreWaitOptions.Infinite,
            out var lifecycleLock);
        if (lifecycleLockStatus != StoreStatus.Success || lifecycleLock is null)
        {
            return ToOpenStatus(lifecycleLockStatus);
        }

        using (lifecycleLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resourceName.LinuxRegionPath) ?? ".");
                var liveOwners = ReadLiveOwnerRecords(resourceName.LinuxOwnersPath);
                var hasLiveResource = File.Exists(resourceName.LinuxRegionPath) && liveOwners.Count > 0;
                if (!hasLiveResource)
                {
                    DeleteStaleResources(resourceName);
                }

                return options.OpenMode switch
                {
                    OpenMode.CreateNew when hasLiveResource => StoreOpenStatus.AlreadyExists,
                    OpenMode.OpenExisting when !hasLiveResource => StoreOpenStatus.NotFound,
                    OpenMode.CreateNew => CreateRegion(resourceName, options, out region),
                    OpenMode.OpenExisting => OpenExistingRegion(resourceName, options, out region),
                    _ => hasLiveResource
                        ? OpenExistingRegion(resourceName, options, out region)
                        : CreateRegion(resourceName, options, out region)
                };
            }
            catch (UnauthorizedAccessException)
            {
                return StoreOpenStatus.AccessDenied;
            }
            catch (PlatformNotSupportedException)
            {
                return StoreOpenStatus.UnsupportedPlatform;
            }
            catch
            {
                return StoreOpenStatus.MappingFailed;
            }
        }
    }

    private static StoreOpenStatus CreateRegion(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        out MemoryMappedStoreRegion? region)
    {
        region = null;
        try
        {
            var stream = new FileStream(
                resourceName.LinuxRegionPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(options.TotalBytes);
            return CreateMappedRegion(resourceName, options, stream, out region);
        }
        catch (IOException)
        {
            return StoreOpenStatus.AlreadyExists;
        }
    }

    private static StoreOpenStatus OpenExistingRegion(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        out MemoryMappedStoreRegion? region)
    {
        region = null;
        if (!File.Exists(resourceName.LinuxRegionPath))
        {
            return StoreOpenStatus.NotFound;
        }

        var stream = new FileStream(
            resourceName.LinuxRegionPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < options.TotalBytes)
        {
            stream.Dispose();
            return StoreOpenStatus.IncompatibleLayout;
        }

        return CreateMappedRegion(resourceName, options, stream, out region);
    }

    private static StoreOpenStatus CreateMappedRegion(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        FileStream stream,
        out MemoryMappedStoreRegion? region)
    {
        region = null;
        var ownerRecord = CreateOwnerRecord();
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: options.TotalBytes,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);

            accessor = mapping.CreateViewAccessor(0, options.TotalBytes, MemoryMappedFileAccess.ReadWrite);
            RegisterOwner(resourceName.LinuxOwnersPath, ownerRecord);
            region = MemoryMappedStoreRegion.Create(
                mapping,
                accessor,
                options.TotalBytes,
                () => ReleaseOwner(resourceName, ownerRecord));
            mapping = null;
            accessor = null;
            return StoreOpenStatus.Success;
        }
        catch
        {
            accessor?.Dispose();
            mapping?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private static string CreateOwnerRecord()
    {
        return Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Guid.NewGuid().ToString("N");
    }

    private static void RegisterOwner(string ownersPath, string ownerRecord)
    {
        var owners = ReadLiveOwnerRecords(ownersPath);
        owners.Add(ownerRecord);
        WriteOwners(ownersPath, owners);
    }

    private static void ReleaseOwner(PlatformResourceName resourceName, string ownerRecord)
    {
        try
        {
            var lifecycleLockStatus = LinuxFileLock.TryAcquire(
                resourceName.LinuxLifecycleLockPath,
                StoreWaitOptions.Infinite,
                out var lifecycleLock);
            if (lifecycleLockStatus != StoreStatus.Success || lifecycleLock is null)
            {
                return;
            }

            using (lifecycleLock)
            {
                var owners = ReadLiveOwnerRecords(resourceName.LinuxOwnersPath);
                owners.RemoveAll(owner => string.Equals(owner, ownerRecord, StringComparison.Ordinal));
                if (owners.Count == 0)
                {
                    DeleteStaleResources(resourceName);
                    return;
                }

                WriteOwners(resourceName.LinuxOwnersPath, owners);
            }
        }
        catch
        {
            // Cleanup is best effort; later opens re-check owner liveness before reusing stale files.
        }
    }

    private static List<string> ReadLiveOwnerRecords(string ownersPath)
    {
        if (!File.Exists(ownersPath))
        {
            return new List<string>();
        }

        var owners = new List<string>();
        foreach (var line in File.ReadAllLines(ownersPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (TryReadProcessId(trimmed, out var processId) && IsProcessLive(processId))
            {
                owners.Add(trimmed);
            }
        }

        return owners;
    }

    private static void WriteOwners(string ownersPath, List<string> owners)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ownersPath) ?? ".");
        File.WriteAllLines(ownersPath, owners);
    }

    private static bool TryReadProcessId(string ownerRecord, out int processId)
    {
        var separator = ownerRecord.IndexOf(':');
        var value = separator < 0 ? ownerRecord : ownerRecord[..separator];
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out processId);
    }

    private static bool IsProcessLive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (processId == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void DeleteStaleResources(PlatformResourceName resourceName)
    {
        DeleteIfExists(resourceName.LinuxRegionPath);
        DeleteIfExists(resourceName.LinuxSynchronizationPath);
        DeleteIfExists(resourceName.LinuxOwnersPath);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static StoreOpenStatus ToOpenStatus(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.Success => StoreOpenStatus.Success,
            StoreStatus.OperationCanceled => StoreOpenStatus.OperationCanceled,
            StoreStatus.StoreBusy => StoreOpenStatus.StoreBusy,
            StoreStatus.AccessDenied => StoreOpenStatus.AccessDenied,
            StoreStatus.UnsupportedPlatform => StoreOpenStatus.UnsupportedPlatform,
            _ => StoreOpenStatus.MappingFailed
        };
    }
}
