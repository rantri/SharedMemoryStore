using System.Reflection;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.Options;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class StoreOptionsValidationTests
{
    private const int MaximumCanonicalCount = 1_048_575;

    [Fact]
    public void OrdinaryCreateDerivesCanonicalBytesAndParticipantCapacity()
    {
        SharedMemoryStoreOptions options = InvokeCreate(
            StoreTestNames.Create(),
            slotCount: 4,
            maxValueBytes: 128,
            maxDescriptorBytes: 16,
            maxKeyBytes: 32,
            leaseRecordCount: 4,
            participantRecordCount: 7,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);

        Assert.Equal(7, options.ParticipantRecordCount);
        Assert.Equal(
            StoreLayoutV2.CalculateRequiredBytes(4, 128, 16, 32, 4, 7),
            options.TotalBytes);
        Assert.True(options.EnableLeaseRecovery);
        Assert.Equal(OpenMode.CreateNew, options.OpenMode);
        Assert.True(options.Validate().IsValid);
        Assert.Equal(StoreOpenStatus.Success, options.Validate().Status);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(7)]
    [InlineData(MaximumCanonicalCount)]
    public void OrdinarySizingAlwaysUsesCanonicalSms2(int participantRecordCount)
    {
        long actual = InvokeCalculateRequiredBytes(
            slotCount: 4,
            maxValueBytes: 128,
            maxDescriptorBytes: 16,
            maxKeyBytes: 32,
            leaseRecordCount: 4,
            participantRecordCount: participantRecordCount);

        Assert.Equal(
            StoreLayoutV2.CalculateRequiredBytes(4, 128, 16, 32, 4, participantRecordCount),
            actual);
    }

    [Fact]
    public void CanonicalMaximumSlotAndParticipantCountsAreAccepted()
    {
        Assert.True(InvokeCalculateRequiredBytes(
            MaximumCanonicalCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1,
            participantRecordCount: 1) > 0);
        Assert.True(InvokeCalculateRequiredBytes(
            slotCount: 1,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1,
            participantRecordCount: MaximumCanonicalCount) > 0);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1_048_576, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1_048_576)]
    public void CanonicalSlotAndParticipantCountsOutsideTheRangeAreRejected(
        int slotCount,
        int participantRecordCount)
    {
        Exception thrown = Assert.ThrowsAny<Exception>(() => InvokeCalculateRequiredBytes(
            slotCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1,
            participantRecordCount: participantRecordCount));

        Assert.IsType<ArgumentOutOfRangeException>(Unwrap(thrown));
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
            ParticipantRecordCount = 64,
            TotalBytes = 1
        };

        StoreOptionsValidationResult result = options.Validate();

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
            ParticipantRecordCount = 64,
            TotalBytes = 64
        };

        StoreOptionsValidationResult result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(StoreOpenStatus.InsufficientCapacity, result.Status);
        Assert.Contains(result.Failures, failure => failure.MemberName == nameof(SharedMemoryStoreOptions.TotalBytes));
    }

    [Fact]
    public void CanonicalLayoutCalculationThrowsInsteadOfWrapping()
    {
        Exception thrown = Assert.ThrowsAny<Exception>(() => InvokeCalculateRequiredBytes(
            slotCount: 1,
            maxValueBytes: int.MaxValue,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1,
            participantRecordCount: 1));

        Assert.IsType<OverflowException>(Unwrap(thrown));
    }

    [Fact]
    public void ValidateAppliesTheSms2SlotCeilingUnconditionally()
    {
        var options = new SharedMemoryStoreOptions
        {
            Name = StoreTestNames.Create(),
            OpenMode = OpenMode.CreateNew,
            SlotCount = MaximumCanonicalCount + 1,
            MaxValueBytes = 1,
            MaxDescriptorBytes = 0,
            MaxKeyBytes = 1,
            LeaseRecordCount = 1,
            ParticipantRecordCount = 1,
            TotalBytes = long.MaxValue
        };

        StoreOptionsValidationResult result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(StoreOpenStatus.InvalidOptions, result.Status);
        StoreOptionsValidationFailure failure = Assert.Single(
            result.Failures,
            static candidate => candidate.MemberName == nameof(SharedMemoryStoreOptions.SlotCount));
        Assert.Contains("1,048,575", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_048_576)]
    public void ValidateAppliesTheSms2ParticipantCeilingUnconditionally(int participantRecordCount)
    {
        var options = new SharedMemoryStoreOptions
        {
            Name = StoreTestNames.Create(),
            OpenMode = OpenMode.CreateNew,
            SlotCount = 1,
            MaxValueBytes = 1,
            MaxDescriptorBytes = 0,
            MaxKeyBytes = 1,
            LeaseRecordCount = 1,
            ParticipantRecordCount = participantRecordCount,
            TotalBytes = long.MaxValue
        };

        StoreOptionsValidationResult result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(StoreOpenStatus.InvalidOptions, result.Status);
        StoreOptionsValidationFailure failure = Assert.Single(
            result.Failures,
            static candidate => candidate.MemberName == nameof(SharedMemoryStoreOptions.ParticipantRecordCount));
        Assert.Contains("1,048,575", failure.Message, StringComparison.Ordinal);
    }

    private static long InvokeCalculateRequiredBytes(
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount)
    {
        MethodInfo method = typeof(SharedMemoryStoreOptions).GetMethod(
                nameof(SharedMemoryStoreOptions.CalculateRequiredBytes),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)],
                modifiers: null)
            ?? throw new Xunit.Sdk.XunitException(
                "The ordinary participant-aware SMS2 CalculateRequiredBytes overload is absent.");
        return (long)method.Invoke(
            null,
            [slotCount, maxValueBytes, maxDescriptorBytes, maxKeyBytes, leaseRecordCount, participantRecordCount])!;
    }

    private static SharedMemoryStoreOptions InvokeCreate(
        string name,
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount,
        OpenMode openMode,
        bool enableLeaseRecovery)
    {
        MethodInfo method = typeof(SharedMemoryStoreOptions).GetMethod(
                nameof(SharedMemoryStoreOptions.Create),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [
                    typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                    typeof(int), typeof(OpenMode), typeof(bool)
                ],
                modifiers: null)
            ?? throw new Xunit.Sdk.XunitException("The ordinary participant-aware SMS2 Create helper is absent.");
        return (SharedMemoryStoreOptions)method.Invoke(
            null,
            [
                name, slotCount, maxValueBytes, maxDescriptorBytes, maxKeyBytes, leaseRecordCount,
                participantRecordCount, openMode, enableLeaseRecovery
            ])!;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
        {
            exception = invocation.InnerException!;
        }

        return exception;
    }
}
