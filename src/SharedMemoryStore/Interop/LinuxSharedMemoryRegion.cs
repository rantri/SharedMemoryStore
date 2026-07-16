using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;

namespace SharedMemoryStore.Interop;

[SupportedOSPlatform("linux")]
internal static class LinuxSharedMemoryRegion
{
    private const string ReleaseMarkerSegment = ".released.";
    private const string FinalizedReleaseMarkerSuffix = ".ready";
    private const int MaximumReleaseMarkerBytes = 1024;
    private static readonly StoreWaitOptions OwnerReleaseWaitOptions = new(TimeSpan.FromMilliseconds(250));

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
                PrepareOpen(
                    resourceName,
                    out List<string> committedOwners,
                    out bool hasLiveResource);
                return OpenPreparedRegion(
                    resourceName,
                    options,
                    committedOwners,
                    hasLiveResource,
                    out region,
                    out _);
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
        IReadOnlyList<string> committedOwners,
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
        }
        catch (IOException) when (File.Exists(resourceName.LinuxRegionPath))
        {
            return StoreOpenStatus.AlreadyExists;
        }

        try
        {
            File.SetUnixFileMode(
                resourceName.LinuxRegionPath,
                LinuxSharedMemoryDirectory.PrivateFileMode);
            stream.SetLength(options.TotalBytes);
            return CreateMappedRegion(
                resourceName,
                options.TotalBytes,
                stream,
                committedOwners,
                out region);
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
        IReadOnlyList<string> committedOwners,
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
        try
        {
            File.SetUnixFileMode(
                resourceName.LinuxRegionPath,
                LinuxSharedMemoryDirectory.PrivateFileMode);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        // Always map the existing file at its actual capacity. Header validation decides
        // whether the requested dimensions/profile are compatible; the requested size must
        // never prevent probing a readable existing mapping.
        return CreateMappedRegion(
            resourceName,
            stream.Length,
            stream,
            committedOwners,
            out region);
    }

    private static StoreOpenStatus CreateMappedRegion(
        PlatformResourceName resourceName,
        long mappingCapacity,
        FileStream stream,
        IReadOnlyList<string> committedOwners,
        out MemoryMappedStoreRegion? region)
    {
        region = null;
        var ownerRecord = CreateOwnerRecord(out Guid ownerToken);
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? accessor = null;
        MemoryMappedStoreRegion? candidate = null;
        LinuxOwnerAnchor? ownerAnchor = null;
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
            ownerAnchor = LinuxOwnerAnchor.Create(resourceName.LinuxOwnersPath, ownerToken);
            LinuxOwnerAnchor registeredAnchor = ownerAnchor;
            candidate = MemoryMappedStoreRegion.Create(
                mapping,
                accessor,
                () =>
                {
                    OwnerReleaseOutcome releaseOutcome = ownerRegistered
                        ? ReleaseOwner(resourceName, ownerRecord)
                        : OwnerReleaseOutcome.OwnerAbsent;
                    if (releaseOutcome != OwnerReleaseOutcome.Failed)
                    {
                        // The exact owner line is either absent or covered by a
                        // durable finalized release marker. Both are authoritative
                        // after the mapped view has already been unmapped.
                        registeredAnchor.Dispose();
                    }
                });
            mapping = null;
            accessor = null;
            CommitOwnerRegistration(resourceName, committedOwners, ownerRecord);
            // Publish the callback state at the sidecar replacement commit point.
            // The pre-registration sweep already covered every unreferenced
            // artifact visible at lifecycle entry. A crash after anchor creation
            // but before this commit is repaired by the next cold lifecycle.
            ownerRegistered = true;
            region = candidate;
            candidate = null;
            ownerAnchor = null;
            return StoreOpenStatus.Success;
        }
        catch
        {
            if (candidate is not null)
            {
                candidate.Dispose();
                candidate = null;
                // The callback owns the anchor outcome, including deliberate
                // retention after a bounded release-marker fallback.
                ownerAnchor = null;
            }

            ownerAnchor?.Dispose();
            accessor?.Dispose();
            mapping?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Begins a Linux cold-open transaction. The retained lifecycle lock is
    /// acquired before the ordinary operation lock, so stale-resource cleanup
    /// cannot race the stable operation-lock rendezvous while the scope holds it.
    /// Both locks remain held across mapped initialization/validation.
    /// </summary>
    internal static StoreOpenStatus TryBeginColdOpen(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        long waitStartTimestamp,
        out SharedStoreOpenScope? scope)
    {
        scope = null;
        LinuxFileLock? lifecycleLock = null;
        LinuxSharedStoreSynchronization? synchronization = null;
        MemoryMappedStoreRegion? region = null;
        bool synchronizationEntered = false;

        try
        {
            StoreStatus remainingStatus = SharedStorePlatform.TryGetRemainingWaitOptions(
                waitOptions,
                waitStartTimestamp,
                out StoreWaitOptions remainingWait);
            if (remainingStatus != StoreStatus.Success)
            {
                return ToOpenStatus(remainingStatus);
            }

            StoreStatus lifecycleLockStatus = LinuxFileLock.TryAcquire(
                resourceName.LinuxLifecycleLockPath,
                remainingWait,
                out lifecycleLock);
            if (lifecycleLockStatus != StoreStatus.Success || lifecycleLock is null)
            {
                return ToOpenStatus(lifecycleLockStatus);
            }

            PrepareOpen(
                resourceName,
                out List<string> committedOwners,
                out bool hasLiveResource);

            remainingStatus = SharedStorePlatform.TryGetRemainingWaitOptions(
                waitOptions,
                waitStartTimestamp,
                out remainingWait);
            if (remainingStatus != StoreStatus.Success)
            {
                return ToOpenStatus(remainingStatus);
            }

            if (options.OpenMode == OpenMode.CreateNew && hasLiveResource)
            {
                return StoreOpenStatus.AlreadyExists;
            }

            if (options.OpenMode == OpenMode.OpenExisting && !hasLiveResource)
            {
                return StoreOpenStatus.NotFound;
            }

            // Open the ordinary lock only after stale data-resource deletion
            // has completed under .lifecycle. Current cleanup retains this
            // stable rendezvous inode; ordering also protects older clients.
            synchronization = new LinuxSharedStoreSynchronization(resourceName);
            StoreStatus enterStatus = synchronization.TryEnter(remainingWait);
            if (enterStatus != StoreStatus.Success)
            {
                return ToOpenStatus(enterStatus);
            }

            synchronizationEntered = true;

            // The ordinary lock wait is part of the same end-to-end open
            // budget. Do not map the region or publish an owner marker after
            // that wait consumed the deadline (or cancellation was observed).
            remainingStatus = SharedStorePlatform.TryGetRemainingWaitOptions(
                waitOptions,
                waitStartTimestamp,
                out _);
            if (remainingStatus != StoreStatus.Success)
            {
                return ToOpenStatus(remainingStatus);
            }

            StoreOpenStatus openStatus = OpenPreparedRegion(
                resourceName,
                options,
                committedOwners,
                hasLiveResource,
                out region,
                out RegionOpenDisposition disposition);
            if (openStatus != StoreOpenStatus.Success || region is null)
            {
                return openStatus;
            }

            scope = new SharedStoreOpenScope(
                region,
                synchronization,
                lifecycleLock,
                disposition);
            region = null;
            synchronization = null;
            lifecycleLock = null;
            synchronizationEntered = false;
            return StoreOpenStatus.Success;
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
        finally
        {
            try
            {
                if (synchronizationEntered)
                {
                    synchronization?.Exit();
                }
            }
            finally
            {
                try
                {
                    lifecycleLock?.Dispose();
                }
                finally
                {
                    try
                    {
                        // Close the ordinary descriptor before region cleanup
                        // can reacquire .lifecycle and publish final-owner state.
                        synchronization?.Dispose();
                    }
                    finally
                    {
                        // The region callback may acquire .lifecycle, so mapping
                        // cleanup follows lifecycle release on every failure.
                        region?.Dispose();
                    }
                }
            }
        }
    }

    private static void PrepareOpen(
        PlatformResourceName resourceName,
        out List<string> committedOwners,
        out bool hasLiveResource)
    {
        LinuxSharedMemoryDirectory.EnsureExists(Path.GetDirectoryName(resourceName.LinuxRegionPath) ?? ".");
        ReconcileReleaseMarkers(resourceName);
        OwnerSnapshot ownerSnapshot = ReadOwnerSnapshot(resourceName);
        committedOwners = ownerSnapshot.CommittedOwners;
        // A live witness makes the existing sidecar an already-committed
        // conservative owner set. It need not be rewritten or fully
        // reclassified merely to attach another handle. When no owner is
        // live, commit the filtered empty set before stale-anchor cleanup.
        if (!ownerSnapshot.HasLiveOwner)
        {
            WriteOwners(resourceName.LinuxOwnersPath, committedOwners);
        }

        SweepUnreferencedOwnerAnchors(resourceName.LinuxOwnersPath, committedOwners);
        hasLiveResource = File.Exists(resourceName.LinuxRegionPath)
            && ownerSnapshot.HasLiveOwner;
        if (!hasLiveResource)
        {
            DeleteStaleResources(resourceName);
            committedOwners = [];
        }
    }

    private static StoreOpenStatus OpenPreparedRegion(
        PlatformResourceName resourceName,
        SharedMemoryStoreOptions options,
        IReadOnlyList<string> committedOwners,
        bool hasLiveResource,
        out MemoryMappedStoreRegion? region,
        out RegionOpenDisposition disposition)
    {
        region = null;
        disposition = default;
        if (options.OpenMode == OpenMode.CreateNew && hasLiveResource)
        {
            return StoreOpenStatus.AlreadyExists;
        }

        if (options.OpenMode == OpenMode.OpenExisting && !hasLiveResource)
        {
            return StoreOpenStatus.NotFound;
        }

        bool createNew = options.OpenMode == OpenMode.CreateNew
            || (options.OpenMode == OpenMode.CreateOrOpen && !hasLiveResource);
        StoreOpenStatus status = createNew
            ? CreateRegion(resourceName, options, committedOwners, out region)
            : OpenExistingRegion(resourceName, options, committedOwners, out region);
        if (status == StoreOpenStatus.Success)
        {
            disposition = createNew
                ? RegionOpenDisposition.CreatedNew
                : RegionOpenDisposition.OpenedExisting;
        }

        return status;
    }

    private static string CreateOwnerRecord(out Guid ownerToken)
    {
        ownerToken = Guid.NewGuid();
        return string.Join(
            ':',
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetProcessStartToken(Environment.ProcessId),
            ownerToken.ToString("N"));
    }

    private static void CommitOwnerRegistration(
        PlatformResourceName resourceName,
        IReadOnlyList<string> committedOwners,
        string ownerRecord)
    {
        var nextOwners = new List<string>(committedOwners.Count + 1);
        nextOwners.AddRange(committedOwners);
        nextOwners.Add(ownerRecord);
        WriteOwners(resourceName.LinuxOwnersPath, nextOwners);
    }

    private static OwnerReleaseOutcome ReleaseOwner(
        PlatformResourceName resourceName,
        string ownerRecord)
    {
        var ownerCommittedAbsent = false;
        try
        {
            var lifecycleLockStatus = LinuxFileLock.TryAcquire(
                resourceName.LinuxLifecycleLockPath,
                OwnerReleaseWaitOptions,
                out var lifecycleLock);
            if (lifecycleLockStatus != StoreStatus.Success || lifecycleLock is null)
            {
                return TryPublishReleaseMarker(resourceName, ownerRecord)
                    ? OwnerReleaseOutcome.FinalizedMarkerPublished
                    : OwnerReleaseOutcome.Failed;
            }

            using (lifecycleLock)
            {
                ReconcileReleaseMarkers(resourceName);
                OwnerScan ownerScan = ReadLiveOwnerRecords(resourceName);
                var owners = ownerScan.LiveOwners;
                owners.RemoveAll(owner => string.Equals(owner, ownerRecord, StringComparison.Ordinal));
                // The sidecar replacement is the commit point. Marker deletion and stale
                // resource cleanup must happen only after this exact owner is absent there.
                WriteOwners(resourceName.LinuxOwnersPath, owners);
                ownerCommittedAbsent = true;
                SweepUnreferencedOwnerAnchors(resourceName.LinuxOwnersPath, owners);
                if (owners.Count == 0)
                {
                    DeleteStaleResources(resourceName);
                }
            }
        }
        catch
        {
            // The finalized marker below makes this exact release replayable by a later C# opener.
        }

        if (ownerCommittedAbsent)
        {
            return OwnerReleaseOutcome.OwnerAbsent;
        }

        return TryPublishReleaseMarker(resourceName, ownerRecord)
            ? OwnerReleaseOutcome.FinalizedMarkerPublished
            : OwnerReleaseOutcome.Failed;
    }

    private static OwnerScan ReadLiveOwnerRecords(PlatformResourceName resourceName)
    {
        var owners = new List<string>();
        foreach (var trimmed in ReadOwnerRecords(resourceName.LinuxOwnersPath))
        {
            if (IsOwnerRecordLive(resourceName.LinuxOwnersPath, trimmed))
            {
                owners.Add(trimmed);
            }
        }

        return new OwnerScan(owners);
    }

    private static OwnerSnapshot ReadOwnerSnapshot(PlatformResourceName resourceName)
    {
        List<string> committedOwners = ReadOwnerRecords(resourceName.LinuxOwnersPath);
        foreach (string owner in committedOwners)
        {
            if (IsOwnerRecordLive(resourceName.LinuxOwnersPath, owner))
            {
                // One authoritative live witness is sufficient to prove that the
                // mapping remains owned. Preserve the complete committed sidecar
                // so attach cost does not grow with every prior process. Full
                // stale-record pruning remains a release/no-live responsibility.
                return new OwnerSnapshot(committedOwners, HasLiveOwner: true);
            }
        }

        return new OwnerSnapshot([], HasLiveOwner: false);
    }

    private static bool IsOwnerRecordLive(string ownersPath, string ownerRecord)
    {
        if (!TryReadOwnerIdentity(ownerRecord, out int processId, out string? startToken))
        {
            return false;
        }

        if (TryReadOwnerToken(ownerRecord, out Guid ownerToken))
        {
            LinuxOwnerAnchorState anchorState = LinuxOwnerAnchor.Probe(ownersPath, ownerToken);
            if (anchorState is LinuxOwnerAnchorState.Locked or LinuxOwnerAnchorState.Ambiguous)
            {
                return true;
            }

            if (anchorState == LinuxOwnerAnchorState.Unlocked)
            {
                return false;
            }
        }

        // Missing anchors are expected for C++/Python and older managed
        // owners. Preserve the resource-v1 PID/start-token classification.
        return IsProcessLive(processId, startToken);
    }

    private static List<string> ReadOwnerRecords(string ownersPath)
    {
        if (!File.Exists(ownersPath))
        {
            return new List<string>();
        }

        var owners = new List<string>();
        foreach (var line in File.ReadAllLines(ownersPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                owners.Add(trimmed);
            }
        }

        return owners;
    }

    private static void ReconcileReleaseMarkers(PlatformResourceName resourceName)
    {
        var finalizedMarkers = EnumerateReleaseMarkerArtifacts(resourceName, finalizedOnly: true);
        if (finalizedMarkers.Length == 0)
        {
            return;
        }

        var owners = ReadOwnerRecords(resourceName.LinuxOwnersPath);
        var reconciledMarkers = new List<string>(finalizedMarkers.Length);
        var releasedTokens = new List<Guid>(finalizedMarkers.Length);
        foreach (var markerPath in finalizedMarkers)
        {
            if (!TryReadReleaseMarker(resourceName, markerPath, out var releasedOwner))
            {
                // A finalized marker is a protocol record, not disposable debris. Retain
                // malformed state and fail the cold operation closed rather than risking
                // deletion of a still-owned mapping.
                throw new InvalidDataException($"Invalid owner-release marker '{Path.GetFileName(markerPath)}'.");
            }

            owners.RemoveAll(owner => string.Equals(owner, releasedOwner, StringComparison.Ordinal));
            reconciledMarkers.Add(markerPath);
            if (TryReadOwnerToken(releasedOwner, out Guid releasedToken))
            {
                releasedTokens.Add(releasedToken);
            }
        }

        if (reconciledMarkers.Count == 0)
        {
            return;
        }

        // Rewrite even when every exact line was already absent. This makes a crash after
        // the prior rewrite but before marker deletion idempotently replayable.
        WriteOwners(resourceName.LinuxOwnersPath, owners);
        foreach (Guid releasedToken in releasedTokens)
        {
            // A same-process bounded close may have retained its flock until a
            // later lifecycle action committed this exact line absent.
            LinuxOwnerAnchor.ReleaseLocalAfterOwnerAbsent(
                resourceName.LinuxOwnersPath,
                releasedToken);
        }

        foreach (var markerPath in reconciledMarkers)
        {
            DeleteIfExists(markerPath);
        }
    }

    private static bool TryReadReleaseMarker(
        PlatformResourceName resourceName,
        string markerPath,
        out string ownerRecord)
    {
        ownerRecord = string.Empty;
        var markerInfo = new FileInfo(markerPath);
        if (!markerInfo.Exists
            || markerInfo.Length <= 0
            || markerInfo.Length > MaximumReleaseMarkerBytes
            || markerInfo.LinkTarget is not null
            || (markerInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var markerName = Path.GetFileName(markerPath);
        var prefix = Path.GetFileName(resourceName.LinuxOwnersPath) + ReleaseMarkerSegment;
        if (!markerName.StartsWith(prefix, StringComparison.Ordinal)
            || !markerName.EndsWith(FinalizedReleaseMarkerSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var uniqueToken = markerName[
            prefix.Length..^FinalizedReleaseMarkerSuffix.Length];
        if (!Guid.TryParseExact(uniqueToken, "N", out _))
        {
            return false;
        }

        var candidate = File.ReadAllText(markerPath, Encoding.UTF8).Trim();
        if (candidate.Length == 0 || candidate.IndexOfAny('\r', '\n') >= 0)
        {
            return false;
        }

        var parts = candidate.Split(':', 3);
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[2], "N", out _)
            || !string.Equals(parts[2], uniqueToken, StringComparison.OrdinalIgnoreCase)
            || !TryReadOwnerIdentity(candidate, out var processId, out _)
            || processId <= 0)
        {
            return false;
        }

        ownerRecord = candidate;
        return true;
    }

    private static bool TryPublishReleaseMarker(
        PlatformResourceName resourceName,
        string ownerRecord)
    {
        string? temporaryPath = null;
        var published = false;
        try
        {
            var ownerParts = ownerRecord.Split(':', 3);
            if (ownerParts.Length != 3 || !Guid.TryParseExact(ownerParts[2], "N", out var uniqueToken))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(resourceName.LinuxOwnersPath) ?? ".";
            LinuxSharedMemoryDirectory.EnsureExists(directory);
            var finalPath = resourceName.LinuxOwnersPath
                + ReleaseMarkerSegment
                + uniqueToken.ToString("N")
                + FinalizedReleaseMarkerSuffix;
            temporaryPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = LinuxSharedMemoryDirectory.PrivateFileMode
            }))
            {
                using (var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 256,
                    leaveOpen: true))
                {
                    writer.WriteLine(ownerRecord);
                    writer.Flush();
                }

                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temporaryPath, LinuxSharedMemoryDirectory.PrivateFileMode);
            File.Move(temporaryPath, finalPath, overwrite: true);
            temporaryPath = null;
            published = true;
            File.SetUnixFileMode(finalPath, LinuxSharedMemoryDirectory.PrivateFileMode);
        }
        catch
        {
            // Unmapping/Dispose must complete even if the private resource directory is damaged.
            // The live owner record remains conservative when marker publication is impossible.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    DeleteIfExists(temporaryPath);
                }
                catch
                {
                    // Stale-resource cleanup removes release-marker temporary artifacts.
                }
            }
        }

        return published;
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

    private static bool TryReadOwnerToken(string ownerRecord, out Guid ownerToken)
    {
        ownerToken = default;
        var parts = ownerRecord.Split(':', 3);
        return parts.Length == 3
            && Guid.TryParseExact(parts[2], "N", out ownerToken);
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
        // Callers commit an owner-sidecar rewrite before entering this method. A marker
        // arriving after their reconciliation is safe to remove only when its owner was
        // also absent from the just-classified live set.
        DeleteIfExists(resourceName.LinuxRegionPath);
        // Keep the ordinary lock inode as a permanent rendezvous, just like
        // .lifecycle. It carries no store generation state, costs one empty
        // mode-0600 file, and cannot split active and reopening participants
        // across unlinked/replacement inodes.
        DeleteIfExists(resourceName.LinuxOwnersPath);
        DeleteIfExists(resourceName.LinuxOwnersPath + ".tmp");
        foreach (var markerPath in EnumerateReleaseMarkerArtifacts(resourceName, finalizedOnly: false))
        {
            DeleteIfExists(markerPath);
        }
    }

    private static void SweepUnreferencedOwnerAnchors(
        string ownersPath,
        IEnumerable<string> committedOwners)
    {
        // Production callers hold .lifecycle and invoke this only with the
        // unchanged committed sidecar or after a replacement sidecar commit.
        // It repairs a crash between anchor creation/locking and publication of
        // that anchor's owner line without adding work to a key-value path.
        var referencedOwnerTokens = new HashSet<Guid>();
        foreach (string owner in committedOwners)
        {
            if (TryReadExactOwnerToken(owner, out Guid ownerToken))
            {
                referencedOwnerTokens.Add(ownerToken);
            }
        }

        LinuxOwnerAnchor.SweepUnreferencedArtifacts(ownersPath, referencedOwnerTokens);
    }

    private static bool TryReadExactOwnerToken(string ownerRecord, out Guid ownerToken)
    {
        ownerToken = default;
        string[] parts = ownerRecord.Split(':', 3);
        if (parts.Length != 3
            || !int.TryParse(
                parts[0],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int processId)
            || processId <= 0
            || !string.Equals(
                parts[0],
                processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[2], "N", out ownerToken)
            || !string.Equals(parts[2], ownerToken.ToString("N"), StringComparison.Ordinal))
        {
            ownerToken = default;
            return false;
        }

        return true;
    }

    private static string[] EnumerateReleaseMarkerArtifacts(
        PlatformResourceName resourceName,
        bool finalizedOnly)
    {
        var directory = Path.GetDirectoryName(resourceName.LinuxOwnersPath) ?? ".";
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var pattern = Path.GetFileName(resourceName.LinuxOwnersPath)
            + ReleaseMarkerSegment
            + (finalizedOnly ? "*" + FinalizedReleaseMarkerSuffix : "*");
        return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
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

    private sealed record OwnerScan(List<string> LiveOwners);

    private sealed record OwnerSnapshot(List<string> CommittedOwners, bool HasLiveOwner);

    private enum OwnerReleaseOutcome
    {
        OwnerAbsent,
        FinalizedMarkerPublished,
        Failed
    }
}
