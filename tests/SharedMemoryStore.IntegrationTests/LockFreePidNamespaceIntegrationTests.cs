using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.IntegrationTests.TestSupport;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreePidNamespaceIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public unsafe void CreatedHeaderCarriesThePlatformPidNamespaceIdentity()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-pidns-header-{Guid.NewGuid():N}";
        using MemoryStore store = IntegrationStoreFactory.Create(
            Options(name, OpenMode.CreateNew));
        MemoryMappedStoreRegion region = ReadRegion(store);
        ref StoreHeaderV2 header = ref *(StoreHeaderV2*)region.Pointer;

        if (OperatingSystem.IsLinux())
        {
            Assert.NotEqual(0UL, header.PidNamespaceId);
        }
        else
        {
            Assert.Equal(0UL, header.PidNamespaceId);
        }
        Assert.Equal(LayoutV2Constants.PidNamespaceRecoveryEnabled, header.PidNamespaceMode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public unsafe void LinuxMismatchedHeaderNamespaceDowngradesRecoveryAndKeepsOrdinaryKvAccess()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string name = $"sms-v2-pidns-reject-{Guid.NewGuid():N}";
        using MemoryStore anchor = IntegrationStoreFactory.Create(
            Options(name, OpenMode.CreateNew));
        MemoryMappedStoreRegion region = ReadRegion(anchor);
        ref StoreHeaderV2 header = ref *(StoreHeaderV2*)region.Pointer;
        ulong originalNamespace = header.PidNamespaceId;
        Assert.NotEqual(0UL, originalNamespace);
        ulong mismatchedNamespace = originalNamespace == ulong.MaxValue
            ? originalNamespace - 1
            : originalNamespace + 1;
        header.PidNamespaceId = mismatchedNamespace;
        try
        {
            StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
                Options(name, OpenMode.OpenExisting),
                out MemoryStore? candidate);

            using MemoryStore opened = Assert.IsType<MemoryStore>(candidate);
            Assert.Equal(StoreOpenStatus.Success, status);
            Assert.Equal(mismatchedNamespace, header.PidNamespaceId);
            Assert.Equal(
                LayoutV2Constants.PidNamespaceRecoveryMixed,
                Volatile.Read(ref header.PidNamespaceMode));
            Assert.Equal(StoreStatus.Success, opened.TryPublish([0x31], [0x41, 0x42]));
            Assert.Equal(StoreStatus.Success, anchor.TryAcquire([0x31], out ValueLease lease));
            using (lease)
            {
                Assert.Equal(new byte[] { 0x41, 0x42 }, lease.ValueSpan.ToArray());
            }
            Assert.Equal(StoreStatus.Success, anchor.TryRemove([0x31]));
        }
        finally
        {
            header.PidNamespaceId = originalNamespace;
        }
    }

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount: 4,
            maxValueBytes: 32,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount: 4,
            participantRecordCount: 2,
            openMode: openMode,
            enableLeaseRecovery: true);

    private static MemoryMappedStoreRegion ReadRegion(MemoryStore store)
    {
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        return Assert.IsAssignableFrom<MemoryMappedStoreRegion>(engine.GetType()
            .GetField("_region", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
    }
}
