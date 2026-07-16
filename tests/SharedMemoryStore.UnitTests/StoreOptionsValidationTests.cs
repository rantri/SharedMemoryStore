using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class StoreOptionsValidationTests
{
    [Fact]
    public void CreateDerivesRequiredBytesAndProducesValidOptions()
    {
        var options = SharedMemoryStoreOptions.Create(
            StoreTestNames.Create(),
            slotCount: 4,
            maxValueBytes: 128,
            maxDescriptorBytes: 16,
            maxKeyBytes: 32,
            leaseRecordCount: 4,
            enableLeaseRecovery: true);

        Assert.True(options.TotalBytes >= SharedMemoryStoreOptions.CalculateRequiredBytes(4, 128, 16, 32, 4));
        Assert.True(options.Validate().IsValid);
        Assert.Equal(StoreOpenStatus.Success, options.Validate().Status);
    }

    [Fact]
    public void ValidateReportsInvalidOpenModeAndCapacityDetails()
    {
        var options = new SharedMemoryStoreOptions
        {
            Name = StoreTestNames.Create(),
            OpenMode = (OpenMode)42,
            SlotCount = 4,
            MaxValueBytes = 128,
            MaxDescriptorBytes = 16,
            MaxKeyBytes = 32,
            LeaseRecordCount = 4,
            TotalBytes = 1
        };

        var result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(StoreOpenStatus.InvalidOptions, result.Status);
        Assert.Contains(result.Failures, failure => failure.MemberName == nameof(SharedMemoryStoreOptions.OpenMode));
    }

    [Fact]
    public void TooSmallTotalBytesReturnsInsufficientCapacityDetail()
    {
        var options = new SharedMemoryStoreOptions
        {
            Name = StoreTestNames.Create(),
            OpenMode = OpenMode.CreateOrOpen,
            SlotCount = 4,
            MaxValueBytes = 128,
            MaxDescriptorBytes = 16,
            MaxKeyBytes = 32,
            LeaseRecordCount = 4,
            TotalBytes = 64
        };

        var result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(StoreOpenStatus.InsufficientCapacity, result.Status);
        Assert.Contains(result.Failures, failure => failure.MemberName == nameof(SharedMemoryStoreOptions.TotalBytes));
    }

    [Fact]
    public void LayoutCalculationRejectsDimensionsThatOverflowInternalIndexing()
    {
        Assert.Throws<OverflowException>(() => SharedMemoryStoreOptions.CalculateRequiredBytes(
            int.MaxValue,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1));

        Assert.Throws<OverflowException>(() => SharedMemoryStoreOptions.CalculateRequiredBytes(
            slotCount: 1,
            maxValueBytes: int.MaxValue,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1));
    }

    [Fact]
    public void ValidateRejectsDimensionsThatCannotBeRepresentedByTheLayout()
    {
        var options = new SharedMemoryStoreOptions
        {
            Name = StoreTestNames.Create(),
            SlotCount = int.MaxValue,
            MaxValueBytes = 1,
            MaxDescriptorBytes = 0,
            MaxKeyBytes = 1,
            LeaseRecordCount = 1,
            TotalBytes = long.MaxValue
        };

        var result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(StoreOpenStatus.InvalidOptions, result.Status);
        Assert.Contains(result.Failures, failure => failure.MemberName == nameof(SharedMemoryStoreOptions.TotalBytes));
    }

    [Fact]
    public void ValidateAppliesTheSlotCountCeilingOnlyToTheLockFreeProfile()
    {
        const int firstRejectedLockFreeSlotCount = 1_048_576;
        var lockFree = new SharedMemoryStoreOptions
        {
            Profile = StoreProfile.LockFree,
            Name = StoreTestNames.Create(),
            OpenMode = OpenMode.CreateNew,
            SlotCount = firstRejectedLockFreeSlotCount,
            MaxValueBytes = 1,
            MaxDescriptorBytes = 0,
            MaxKeyBytes = 1,
            LeaseRecordCount = 1,
            ParticipantRecordCount = 1,
            TotalBytes = long.MaxValue
        };

        var lockFreeResult = lockFree.Validate();

        Assert.False(lockFreeResult.IsValid);
        Assert.Equal(StoreOpenStatus.InvalidOptions, lockFreeResult.Status);
        var failure = Assert.Single(
            lockFreeResult.Failures,
            static candidate => candidate.MemberName == nameof(SharedMemoryStoreOptions.SlotCount));
        Assert.Contains("1,048,575", failure.Message, StringComparison.Ordinal);

        long legacyBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(
            firstRejectedLockFreeSlotCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1);
        var legacy = new SharedMemoryStoreOptions
        {
            Profile = StoreProfile.Legacy,
            Name = StoreTestNames.Create(),
            OpenMode = OpenMode.CreateNew,
            SlotCount = firstRejectedLockFreeSlotCount,
            MaxValueBytes = 1,
            MaxDescriptorBytes = 0,
            MaxKeyBytes = 1,
            LeaseRecordCount = 1,
            ParticipantRecordCount = 0,
            TotalBytes = legacyBytes
        };

        Assert.Equal(StoreOpenStatus.Success, legacy.Validate().Status);
    }
}
