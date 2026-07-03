using SharedMemoryStore.Options;

namespace SharedMemoryStore.ContractTests;

public sealed class ConfigurationContractTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ConfigurationContract)]
    public void OptionsExposeCreateAndValidationDetails()
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2);

        StoreOptionsValidationResult result = options.Validate();

        Assert.True(result.IsValid);
        Assert.Equal(StoreOpenStatus.Success, result.Status);
        Assert.Empty(result.Failures);
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ConfigurationContract)]
    public void InvalidOpenModeIsRejected()
    {
        var options = new SharedMemoryStoreOptions
        {
            Name = $"sms-{Guid.NewGuid():N}",
            OpenMode = (OpenMode)99,
            SlotCount = 2,
            MaxValueBytes = 16,
            MaxDescriptorBytes = 4,
            MaxKeyBytes = 8,
            LeaseRecordCount = 2,
            TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, 16, 4, 8, 2)
        };

        Assert.Equal(StoreOpenStatus.InvalidOptions, options.Validate().Status);
        Assert.Equal(StoreOpenStatus.InvalidOptions, MemoryStore.TryCreateOrOpen(options, out _));
    }
}
