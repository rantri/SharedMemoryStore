using System.Reflection;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.ContractTests;

public sealed class PublicApiContractTests
{
    [Fact]
    public void StoreOpenStatusValuesMatchContract()
    {
        Assert.Equal(0, (int)StoreOpenStatus.Success);
        Assert.Equal(1, (int)StoreOpenStatus.AlreadyExists);
        Assert.Equal(2, (int)StoreOpenStatus.NotFound);
        Assert.Equal(3, (int)StoreOpenStatus.InvalidOptions);
        Assert.Equal(4, (int)StoreOpenStatus.IncompatibleLayout);
        Assert.Equal(5, (int)StoreOpenStatus.UnsupportedPlatform);
        Assert.Equal(6, (int)StoreOpenStatus.InsufficientCapacity);
        Assert.Equal(7, (int)StoreOpenStatus.AccessDenied);
        Assert.Equal(8, (int)StoreOpenStatus.MappingFailed);
    }

    [Fact]
    public void StoreStatusValuesMatchContract()
    {
        var names = new[]
        {
            "Success",
            "DuplicateKey",
            "NotFound",
            "KeyTooLarge",
            "ValueTooLarge",
            "DescriptorTooLarge",
            "StoreFull",
            "LeaseTableFull",
            "InvalidLease",
            "LeaseAlreadyReleased",
            "RemovePending",
            "UnsupportedPlatform",
            "StoreDisposed",
            "CorruptStore",
            "AccessDenied",
            "UnknownFailure"
        };

        Assert.Equal(names, Enum.GetNames<StoreStatus>());
    }

    [Fact]
    public void PublicStoreMembersMatchContract()
    {
        Assert.NotNull(typeof(Store).GetMethod(nameof(Store.TryCreateOrOpen), BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(typeof(Store).GetMethod(nameof(Store.TryPublish), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(Store).GetMethod(nameof(Store.TryAcquire), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(Store).GetMethod(nameof(Store.TryRemove), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(Store).GetMethod(nameof(Store.TryRecoverLeases), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(Store).GetMethod(nameof(Store.GetDiagnostics), BindingFlags.Public | BindingFlags.Instance));
        Assert.Contains(typeof(IDisposable), typeof(Store).GetInterfaces());
    }

    [Fact]
    public void PublicOptionMembersMatchContract()
    {
        var properties = typeof(SharedMemoryStoreOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
        Assert.Contains(nameof(SharedMemoryStoreOptions.Name), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.OpenMode), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.TotalBytes), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.SlotCount), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.MaxValueBytes), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.MaxDescriptorBytes), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.MaxKeyBytes), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.LeaseRecordCount), properties);
        Assert.Contains(nameof(SharedMemoryStoreOptions.EnableLeaseRecovery), properties);
    }
}
