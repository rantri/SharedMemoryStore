using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharedMemoryStore.IntegrationTests.TestSupport;
using SharedMemoryStore.Interop;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

[SupportedOSPlatform("linux")]
public sealed class LinuxOwnerReleaseMarkerIntegrationTests
{
    private static readonly TimeSpan DisposeCompletionLimit = TimeSpan.FromSeconds(2);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HeldLifecycleBoundsDisposeKeepsSiblingUsableAndNextOpenRemovesOnlyExactGhost()
    {
        if (!IsQualifiedLinuxHost())
        {
            return;
        }

        var name = $"sms-owner-release-exact-{Guid.NewGuid():N}";
        var resource = PlatformResourceName.Create(name);
        var createOptions = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 3);
        var openOptions = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: 3);
        Store? first = null;
        Store? sibling = null;
        Store? next = null;
        LinuxFileLockHolder? heldLifecycle = null;

        try
        {
            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(createOptions, out first));
            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(openOptions, out sibling));
            Assert.NotNull(first);
            Assert.NotNull(sibling);

            var ownersBefore = ReadOwnerLines(resource.LinuxOwnersPath);
            Assert.Equal(2, ownersBefore.Length);
            heldLifecycle = LinuxFileLockHolder.Acquire(resource.LinuxLifecycleLockPath);
            Assert.Equal(
                StoreStatus.Success,
                LinuxFileLock.TryAcquire(
                    resource.LinuxSynchronizationPath,
                    StoreWaitOptions.NoWait,
                    out var ordinaryOperationLock));
            Assert.NotNull(ordinaryOperationLock);
            ordinaryOperationLock!.Dispose();

            var stopwatch = Stopwatch.StartNew();
            var disposeTask = Task.Run(first!.Dispose);
            await disposeTask.WaitAsync(DisposeCompletionLimit);
            stopwatch.Stop();
            first = null;

            Assert.True(stopwatch.Elapsed <= DisposeCompletionLimit);
            var markers = ReadFinalizedMarkers(resource);
            var marker = Assert.Single(markers);
            Assert.Equal(LinuxSharedMemoryDirectory.PrivateFileMode, File.GetUnixFileMode(marker.Path));
            Assert.Contains(marker.Owner, ownersBefore, StringComparer.Ordinal);
            Guid releasedOwnerToken = ParseOwnerToken(marker.Owner);
            Assert.Equal(
                LinuxOwnerAnchorState.Missing,
                LinuxOwnerAnchor.Probe(resource.LinuxOwnersPath, releasedOwnerToken));
            Assert.False(File.Exists(LinuxOwnerAnchor.GetPath(resource.LinuxOwnersPath, releasedOwnerToken)));
            string siblingOwner = Assert.Single(
                ownersBefore,
                owner => !string.Equals(owner, marker.Owner, StringComparison.Ordinal));
            Assert.Equal(
                LinuxOwnerAnchorState.Locked,
                LinuxOwnerAnchor.Probe(resource.LinuxOwnersPath, ParseOwnerToken(siblingOwner)));
            Assert.Equal(
                ownersBefore.OrderBy(static owner => owner, StringComparer.Ordinal),
                ReadOwnerLines(resource.LinuxOwnersPath).OrderBy(static owner => owner, StringComparer.Ordinal));

            Assert.Equal(StoreStatus.Success, sibling!.TryPublish([1], [42]));
            Assert.Equal(StoreStatus.Success, sibling.TryAcquire([1], out var lease));
            Assert.Equal(42, lease.ValueSpan[0]);
            Assert.Equal(StoreStatus.Success, lease.Release());

            heldLifecycle.Dispose();
            heldLifecycle = null;

            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(openOptions, out next));
            Assert.NotNull(next);
            var ownersAfter = ReadOwnerLines(resource.LinuxOwnersPath);
            Assert.Equal(2, ownersAfter.Length);
            Assert.DoesNotContain(marker.Owner, ownersAfter, StringComparer.Ordinal);
            Assert.Contains(ownersBefore.Single(owner => !string.Equals(owner, marker.Owner, StringComparison.Ordinal)), ownersAfter, StringComparer.Ordinal);
            Assert.Empty(GetFinalizedMarkerPaths(resource));
            Assert.Equal(StoreStatus.Success, sibling.TryRemove([1]));
        }
        finally
        {
            heldLifecycle?.Dispose();
            next?.Dispose();
            sibling?.Dispose();
            first?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FinalOwnerMarkerAllowsCreateNewWhileReleasingProcessRemainsAlive()
    {
        if (!IsQualifiedLinuxHost())
        {
            return;
        }

        var name = $"sms-owner-release-recreate-{Guid.NewGuid():N}";
        var resource = PlatformResourceName.Create(name);
        var options = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 1);
        Store? first = null;
        Store? recreated = null;
        LinuxFileLockHolder? heldLifecycle = null;

        try
        {
            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(options, out first));
            Assert.NotNull(first);
            var originalOwner = Assert.Single(ReadOwnerLines(resource.LinuxOwnersPath));
            heldLifecycle = LinuxFileLockHolder.Acquire(resource.LinuxLifecycleLockPath);

            var disposeTask = Task.Run(first!.Dispose);
            await disposeTask.WaitAsync(DisposeCompletionLimit);
            first = null;
            var marker = Assert.Single(ReadFinalizedMarkers(resource));
            Assert.Equal(originalOwner, marker.Owner);
            Assert.Equal(Environment.ProcessId, ParseOwnerProcessId(marker.Owner));
            Assert.Equal(
                LinuxOwnerAnchorState.Missing,
                LinuxOwnerAnchor.Probe(resource.LinuxOwnersPath, ParseOwnerToken(marker.Owner)));

            heldLifecycle!.Dispose();
            heldLifecycle = null;

            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(options, out recreated));
            Assert.NotNull(recreated);
            var recreatedOwner = Assert.Single(ReadOwnerLines(resource.LinuxOwnersPath));
            Assert.NotEqual(originalOwner, recreatedOwner);
            Assert.Empty(GetFinalizedMarkerPaths(resource));
        }
        finally
        {
            heldLifecycle?.Dispose();
            recreated?.Dispose();
            first?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentBlockedReleasesPublishDistinctPrivateMarkersWithoutLoss()
    {
        if (!IsQualifiedLinuxHost())
        {
            return;
        }

        const int ownerCount = 8;
        var name = $"sms-owner-release-concurrent-{Guid.NewGuid():N}";
        var resource = PlatformResourceName.Create(name);
        var createOptions = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: ownerCount + 1);
        var openOptions = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: ownerCount + 1);
        var recreateOptions = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: ownerCount + 1);
        var stores = new List<Store>(ownerCount);
        Store? recreated = null;
        LinuxFileLockHolder? heldLifecycle = null;

        try
        {
            for (var index = 0; index < ownerCount; index++)
            {
                var status = Store.TryCreateOrOpen(index == 0 ? createOptions : openOptions, out var store);
                Assert.Equal(StoreOpenStatus.Success, status);
                stores.Add(Assert.IsType<Store>(store));
            }

            var ownersBefore = ReadOwnerLines(resource.LinuxOwnersPath);
            Assert.Equal(ownerCount, ownersBefore.Length);
            heldLifecycle = LinuxFileLockHolder.Acquire(resource.LinuxLifecycleLockPath);

            using var start = new ManualResetEventSlim();
            var releaseTasks = stores
                .Select(store => Task.Run(() =>
                {
                    start.Wait();
                    store.Dispose();
                }))
                .ToArray();
            start.Set();
            await Task.WhenAll(releaseTasks).WaitAsync(DisposeCompletionLimit);
            stores.Clear();

            var markers = ReadFinalizedMarkers(resource);
            Assert.Equal(ownerCount, markers.Length);
            Assert.Equal(ownerCount, markers.Select(static marker => marker.Path).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ownerCount, markers.Select(static marker => marker.Owner).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                ownersBefore.OrderBy(static owner => owner, StringComparer.Ordinal),
                markers.Select(static marker => marker.Owner).OrderBy(static owner => owner, StringComparer.Ordinal));
            Assert.All(
                markers,
                marker => Assert.Equal(
                    LinuxSharedMemoryDirectory.PrivateFileMode,
                    File.GetUnixFileMode(marker.Path)));
            Assert.All(
                markers,
                marker => Assert.Equal(
                    LinuxOwnerAnchorState.Missing,
                    LinuxOwnerAnchor.Probe(resource.LinuxOwnersPath, ParseOwnerToken(marker.Owner))));

            heldLifecycle!.Dispose();
            heldLifecycle = null;

            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(recreateOptions, out recreated));
            Assert.NotNull(recreated);
            Assert.Empty(GetFinalizedMarkerPaths(resource));
            Assert.Single(ReadOwnerLines(resource.LinuxOwnersPath));
        }
        finally
        {
            heldLifecycle?.Dispose();
            recreated?.Dispose();
            foreach (var store in stores)
            {
                store.Dispose();
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MalformedFinalizedMarkerFailsOpenClosedAndIsPreserved()
    {
        if (!IsQualifiedLinuxHost())
        {
            return;
        }

        var name = $"sms-owner-release-malformed-{Guid.NewGuid():N}";
        var resource = PlatformResourceName.Create(name);
        var createOptions = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        var openOptions = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: 2);
        var markerPath = resource.LinuxOwnersPath
            + ".released."
            + Guid.NewGuid().ToString("N")
            + ".ready";

        using var store = IntegrationStoreFactory.Create(createOptions);
        File.WriteAllText(markerPath, "not-an-owner-record");
        File.SetUnixFileMode(markerPath, LinuxSharedMemoryDirectory.PrivateFileMode);
        try
        {
            var status = Store.TryCreateOrOpen(openOptions, out var rejected);

            rejected?.Dispose();
            Assert.Equal(StoreOpenStatus.MappingFailed, status);
            Assert.Null(rejected);
            Assert.True(File.Exists(markerPath));
            Assert.Equal(StoreStatus.Success, store.TryPublish([7], [9]));
            Assert.Equal(StoreStatus.Success, store.TryRemove([7]));
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailedOpenAfterMappingUsesTheSameBoundedReleaseMarkerPath()
    {
        if (!IsQualifiedLinuxHost())
        {
            return;
        }

        var name = $"sms-owner-release-open-failure-{Guid.NewGuid():N}";
        var resource = PlatformResourceName.Create(name);
        var createOptions = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 3);
        var openOptions = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: 3);
        using var anchor = IntegrationStoreFactory.Create(createOptions);
        var anchorOwner = Assert.Single(ReadOwnerLines(resource.LinuxOwnersPath));
        LinuxFileLockHolder? operationLock = null;
        LinuxFileLockHolder? lifecycleLock = null;
        Store? next = null;

        try
        {
            operationLock = LinuxFileLockHolder.Acquire(resource.LinuxSynchronizationPath);
            var failedOpen = Task.Run(() =>
            {
                var status = Store.TryCreateOrOpen(
                    openOptions,
                    new StoreWaitOptions(TimeSpan.FromMilliseconds(600)),
                    out var rejected);
                rejected?.Dispose();
                return status;
            });

            await WaitForOwnerCountAsync(resource.LinuxOwnersPath, expectedCount: 2);
            lifecycleLock = LinuxFileLockHolder.Acquire(resource.LinuxLifecycleLockPath);
            Assert.Equal(StoreOpenStatus.StoreBusy, await failedOpen.WaitAsync(TimeSpan.FromSeconds(3)));

            var marker = Assert.Single(ReadFinalizedMarkers(resource));
            Assert.NotEqual(anchorOwner, marker.Owner);
            Assert.Equal(LinuxSharedMemoryDirectory.PrivateFileMode, File.GetUnixFileMode(marker.Path));
            Assert.Equal(
                LinuxOwnerAnchorState.Missing,
                LinuxOwnerAnchor.Probe(resource.LinuxOwnersPath, ParseOwnerToken(marker.Owner)));
            Assert.Equal(
                LinuxOwnerAnchorState.Locked,
                LinuxOwnerAnchor.Probe(resource.LinuxOwnersPath, ParseOwnerToken(anchorOwner)));
            Assert.Equal(StoreStatus.Success, anchor.TryPublish([3], [5]));

            lifecycleLock.Dispose();
            lifecycleLock = null;
            operationLock.Dispose();
            operationLock = null;

            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(openOptions, out next));
            Assert.NotNull(next);
            var ownersAfter = ReadOwnerLines(resource.LinuxOwnersPath);
            Assert.Contains(anchorOwner, ownersAfter, StringComparer.Ordinal);
            Assert.DoesNotContain(marker.Owner, ownersAfter, StringComparer.Ordinal);
            Assert.Empty(GetFinalizedMarkerPaths(resource));
            Assert.Equal(StoreStatus.Success, anchor.TryRemove([3]));
        }
        finally
        {
            lifecycleLock?.Dispose();
            operationLock?.Dispose();
            next?.Dispose();
        }
    }

    private static bool IsQualifiedLinuxHost()
    {
        return OperatingSystem.IsLinux()
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }

    private static SharedMemoryStoreOptions CreateOptions(
        string name,
        OpenMode openMode,
        int participantRecordCount)
    {
        return SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 4,
            maxValueBytes: 64,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount: 8,
            participantRecordCount,
            openMode);
    }

    private static string[] ReadOwnerLines(string path)
    {
        Assert.True(File.Exists(path));
        return File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .ToArray();
    }

    private static ReleaseMarker[] ReadFinalizedMarkers(PlatformResourceName resource)
    {
        return GetFinalizedMarkerPaths(resource)
            .Select(path => new ReleaseMarker(path, File.ReadAllText(path).Trim()))
            .ToArray();
    }

    private static string[] GetFinalizedMarkerPaths(PlatformResourceName resource)
    {
        var directory = Path.GetDirectoryName(resource.LinuxOwnersPath)!;
        var pattern = Path.GetFileName(resource.LinuxOwnersPath) + ".released.*.ready";
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            : [];
    }

    private static int ParseOwnerProcessId(string owner)
    {
        return int.Parse(
            owner.AsSpan(0, owner.IndexOf(':')),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Guid ParseOwnerToken(string owner)
    {
        string[] parts = owner.Split(':', 3);
        Assert.Equal(3, parts.Length);
        Assert.True(Guid.TryParseExact(parts[2], "N", out Guid token));
        return token;
    }

    private static async Task WaitForOwnerCountAsync(string ownersPath, int expectedCount)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (File.Exists(ownersPath)
                && File.ReadAllLines(ownersPath).Count(static line => line.Trim().Length != 0) == expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException($"The owner sidecar did not reach {expectedCount} records.");
    }

    private sealed record ReleaseMarker(string Path, string Owner);

    private sealed class LinuxFileLockHolder : IDisposable
    {
        private readonly string _path;
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly ManualResetEventSlim _finished = new();
        private readonly Thread _ownerThread;
        private Exception? _failure;
        private bool _disposed;

        private LinuxFileLockHolder(string path)
        {
            _path = path;
            _ownerThread = new Thread(HoldLock)
            {
                IsBackground = true,
                Name = "SharedMemoryStore file-lock test holder"
            };
            _ownerThread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The file-lock holder did not start.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException("The file-lock holder failed to acquire the lock.", _failure);
            }
        }

        public static LinuxFileLockHolder Acquire(string path) => new(path);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _release.Set();
            if (!_finished.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The file-lock holder did not release the lock.");
            }

            _release.Dispose();
            _ready.Dispose();
            _finished.Dispose();
            if (_failure is not null)
            {
                throw new InvalidOperationException("The file-lock holder failed.", _failure);
            }
        }

        private void HoldLock()
        {
            LinuxFileLock? heldLock = null;
            try
            {
                var status = LinuxFileLock.TryAcquire(
                    _path,
                    StoreWaitOptions.Infinite,
                    out heldLock);
                if (status != StoreStatus.Success || heldLock is null)
                {
                    throw new InvalidOperationException($"File-lock acquisition returned {status}.");
                }

                _ready.Set();
                _release.Wait();
            }
            catch (Exception exception)
            {
                _failure = exception;
                _ready.Set();
            }
            finally
            {
                try
                {
                    heldLock?.Dispose();
                }
                catch (Exception exception)
                {
                    _failure ??= exception;
                }
                finally
                {
                    _finished.Set();
                }
            }
        }
    }
}
