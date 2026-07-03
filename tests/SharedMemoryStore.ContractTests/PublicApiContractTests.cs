using System.Reflection;
using Store = SharedMemoryStore.MemoryStore;

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
        Assert.Equal(9, (int)StoreOpenStatus.StoreBusy);
        Assert.Equal(10, (int)StoreOpenStatus.OperationCanceled);
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
            "UnknownFailure",
            "InvalidReservation",
            "ReservationIncomplete",
            "ReservationAlreadyCompleted",
            "ReservationWriteOutOfRange",
            "InvalidKey",
            "StoreBusy",
            "OperationCanceled"
        };

        Assert.Equal(names, Enum.GetNames<StoreStatus>());
        Assert.Equal(16, (int)StoreStatus.InvalidReservation);
        Assert.Equal(17, (int)StoreStatus.ReservationIncomplete);
        Assert.Equal(18, (int)StoreStatus.ReservationAlreadyCompleted);
        Assert.Equal(19, (int)StoreStatus.ReservationWriteOutOfRange);
        Assert.Equal(20, (int)StoreStatus.InvalidKey);
        Assert.Equal(21, (int)StoreStatus.StoreBusy);
        Assert.Equal(22, (int)StoreStatus.OperationCanceled);
    }

    [Fact]
    public void PublicStoreMembersMatchContract()
    {
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryCreateOrOpen));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryPublish));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryAcquire));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryRemove));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryRecoverLeases));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryReserve));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryPublishSegments));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryRecoverReservations));
        Assert.NotNull(typeof(Store).GetMethod(nameof(Store.GetDiagnostics), BindingFlags.Public | BindingFlags.Instance));
        Assert.Contains(PublicStoreMethods(), method => method.Name == nameof(Store.TryGetDiagnostics));
        Assert.Contains(typeof(IDisposable), typeof(Store).GetInterfaces());
    }

    private static MethodInfo[] PublicStoreMethods()
    {
        return typeof(Store).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
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
