using System.Runtime.InteropServices;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreePackageIntegrationTests
{
    [Fact]
    [Trait("Category", "PackageConsumption")]
    public void ParticipantCapacityIsConsumedPerHandleAndReusableAfterClose()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-package-participants-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = Options(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions open = Options(name, OpenMode.OpenExisting, participantRecordCount: 2);

        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(create, out MemoryStore? first));
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(open, out MemoryStore? second));
        Assert.Equal(StoreOpenStatus.ParticipantTableFull, MemoryStore.TryCreateOrOpen(open, out MemoryStore? rejected));
        Assert.Null(rejected);

        try
        {
            Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), first!.ProtocolInfo);
            second!.Dispose();
            second = null;

            Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(open, out MemoryStore? replacement));
            using (replacement)
            {
                Assert.NotNull(replacement);
                Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), replacement!.ProtocolInfo);
            }
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
        }
    }

    private static SharedMemoryStoreOptions Options(
        string name,
        OpenMode openMode,
        int participantRecordCount)
    {
        return SharedMemoryStoreOptions.Create(
            name,
            slotCount: 4,
            maxValueBytes: 64,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount: 8,
            participantRecordCount,
            openMode);
    }

    private static bool IsSupportedHost()
    {
        return (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }
}
