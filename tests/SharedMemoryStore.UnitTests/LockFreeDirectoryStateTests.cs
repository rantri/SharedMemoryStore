using System.Reflection;
using System.Runtime.InteropServices;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeDirectoryStateTests
{
    [Fact]
    public async Task ConcurrentSameKeyPublishCallsHaveOneCurrentWinner()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-unit-publish-history-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 4,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew);
        var openStatus = Store.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, openStatus);
        using var ownedStore = Assert.IsType<Store>(store);

        var statuses = new StoreStatus[2];
        using var start = new Barrier(participantCount: 3);
        var first = Task.Run(() =>
        {
            start.SignalAndWait();
            statuses[0] = ownedStore.TryPublish([0x41], [0x11]);
        });
        var second = Task.Run(() =>
        {
            start.SignalAndWait();
            statuses[1] = ownedStore.TryPublish([0x41], [0x22]);
        });
        start.SignalAndWait();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StoreProfile.LockFree, ownedStore.Profile);
        Assert.Single(statuses, static status => status == StoreStatus.Success);
        Assert.Single(statuses, static status => status == StoreStatus.DuplicateKey);
    }

    [Fact]
    public void DirectoryImplementationExposesLookupInsertUnlinkAndHelpingWithoutOwnerLocks()
    {
        var directoryType = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeKeyDirectory",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(directoryType is not null, "The layout-v2 engine requires a LockFreeKeyDirectory implementation.");

        var methodNames = directoryType!
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(static method => method.Name)
            .ToArray();
        Assert.Contains(methodNames, static name => name.Contains("Lookup", StringComparison.Ordinal));
        Assert.Contains(methodNames, static name => name.Contains("Insert", StringComparison.Ordinal));
        Assert.Contains(methodNames, static name => name.Contains("Unlink", StringComparison.Ordinal));
        Assert.Contains(methodNames, static name => name.Contains("Help", StringComparison.Ordinal));

        var forbiddenTypes = new[]
        {
            typeof(Mutex),
            typeof(Semaphore),
            typeof(SemaphoreSlim),
            typeof(ReaderWriterLockSlim)
        };
        var fields = directoryType.GetFields(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => forbiddenTypes.Contains(field.FieldType));
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
