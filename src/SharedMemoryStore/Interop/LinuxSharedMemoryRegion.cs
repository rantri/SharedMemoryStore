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
        StoreWaitOptions waitOptions,
        out MemoryMappedStoreRegion? region)
    {
        region = null;

        var lifecycleLockStatus = LinuxFileLock.TryAcquire(
            resourceName.LinuxLifecycleLockPath,
            waitOptions,
            out var lifecycleLock);
        if (lifecycleLockStatus != StoreStatus.Success || lifecycleLock is null)
        {
            return ToOpenStatus(lifecycleLockStatus);
        }

        using (lifecycleLock)
        {
            try
            {
                LinuxSharedMemoryDirectory.EnsureExists(Path.GetDirectoryName(resourceName.LinuxRegionPath) ?? ".");
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
        FileStream stream;
        try
        {
            stream = new FileStream(resourceName.LinuxRegionPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete,
                UnixCreateMode = LinuxSharedMemoryDirectory.PrivateFileMode
            });
            File.SetUnixFileMode(resourceName.LinuxRegionPath, LinuxSharedMemoryDirectory.PrivateFileMode);
        }
        catch (IOException) when (File.Exists(resourceName.LinuxRegionPath))
        {
            return StoreOpenStatus.AlreadyExists;
        }

        try
        {
            stream.SetLength(options.TotalBytes);
            return CreateMappedRegion(resourceName, options.TotalBytes, stream, out region);
        }
        catch
        {
            stream.Dispose();
            DeleteIfExists(resourceName.LinuxRegionPath);
            throw;
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
        File.SetUnixFileMode(resourceName.LinuxRegionPath, LinuxSharedMemoryDirectory.PrivateFileMode);

        if (stream.Length < options.TotalBytes)
        {
            stream.Dispose();
            return StoreOpenStatus.IncompatibleLayout;
        }

        return CreateMappedRegion(resourceName, stream.Length, stream, out region);
    }

    private static StoreOpenStatus CreateMappedRegion(
        PlatformResourceName resourceName,
        long mappingCapacity,
        FileStream stream,
        out MemoryMappedStoreRegion? region)
    {
        region = null;
        var ownerRecord = CreateOwnerRecord();
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? accessor = null;
        MemoryMappedStoreRegion? candidate = null;
        var ownerRegistered = false;
        try
        {
            mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: mappingCapacity,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);

            accessor = mapping.CreateViewAccessor(0, mappingCapacity, MemoryMappedFileAccess.ReadWrite);
            candidate = MemoryMappedStoreRegion.Create(
                mapping,
                accessor,
                mappingCapacity,
                () =>
                {
                    if (ownerRegistered)
                    {
                        ReleaseOwner(resourceName, ownerRecord);
                    }
                });
            mapping = null;
            accessor = null;
            RegisterOwner(resourceName.LinuxOwnersPath, ownerRecord);
            ownerRegistered = true;
            region = candidate;
            candidate = null;
            return StoreOpenStatus.Success;
        }
        catch
        {
            candidate?.Dispose();
            accessor?.Dispose();
            mapping?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private static string CreateOwnerRecord()
    {
        return string.Join(
            ':',
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetProcessStartToken(Environment.ProcessId),
            Guid.NewGuid().ToString("N"));
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

            if (TryReadOwnerIdentity(trimmed, out var processId, out var startToken)
                && IsProcessLive(processId, startToken))
            {
                owners.Add(trimmed);
            }
        }

        return owners;
    }

    private static void WriteOwners(string ownersPath, List<string> owners)
    {
        LinuxSharedMemoryDirectory.EnsureExists(Path.GetDirectoryName(ownersPath) ?? ".");
        var temporaryPath = ownersPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = LinuxSharedMemoryDirectory.PrivateFileMode
            }))
            using (var writer = new StreamWriter(stream))
            {
                foreach (var owner in owners)
                {
                    writer.WriteLine(owner);
                }
            }

            File.SetUnixFileMode(temporaryPath, LinuxSharedMemoryDirectory.PrivateFileMode);
            File.Move(temporaryPath, ownersPath, overwrite: true);
        }
        finally
        {
            try
            {
                DeleteIfExists(temporaryPath);
            }
            catch
            {
                // A later owner update or stale-resource cleanup will retry temporary-file cleanup.
            }
        }
    }

    private static bool TryReadOwnerIdentity(string ownerRecord, out int processId, out string? startToken)
    {
        processId = 0;
        startToken = null;
        var parts = ownerRecord.Split(':', 3);
        if (!int.TryParse(
            parts[0],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out processId))
        {
            return false;
        }

        if (parts.Length >= 3)
        {
            startToken = parts[1];
        }

        return true;
    }

    private static bool IsProcessLive(int processId, string? startToken)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            if (string.IsNullOrEmpty(startToken))
            {
                return true;
            }

            var observedStartToken = GetProcessStartToken(processId);
            return observedStartToken.Length == 0
                || string.Equals(observedStartToken, startToken, StringComparison.Ordinal);
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

    private static string GetProcessStartToken(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var commandEnd = stat.LastIndexOf(')');
            if (commandEnd >= 0 && commandEnd + 2 < stat.Length)
            {
                var fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 19)
                {
                    return "proc-" + fields[19];
                }
            }
        }
        catch
        {
            // Fall back to the runtime process timestamp when procfs is unavailable.
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return "utc-" + process.StartTime.ToUniversalTime().Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void DeleteStaleResources(PlatformResourceName resourceName)
    {
        DeleteIfExists(resourceName.LinuxRegionPath);
        DeleteIfExists(resourceName.LinuxSynchronizationPath);
        DeleteIfExists(resourceName.LinuxOwnersPath);
        DeleteIfExists(resourceName.LinuxOwnersPath + ".tmp");
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
