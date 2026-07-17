using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

public sealed class SingleProtocolOpenIntegrationTests
{
    private const uint RetiredSms1Magic = 0x3153_4d53;
    private const uint CanonicalSms2Magic = 0x3253_4d53;

    [Fact]
    [Trait("Category", "Integration")]
    public void OrdinaryCreateReportsTheCanonicalFiveFieldIdentity()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        SharedMemoryStoreOptions options = CreateOptions(
            $"sms-single-protocol-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            participantRecordCount: 2);

        StoreOpenStatus status = Store.TryCreateOrOpen(options, out Store? store);

        try
        {
            Assert.Equal(StoreOpenStatus.Success, status);
            Assert.NotNull(store);
            AssertCanonicalProtocol(store!);
        }
        finally
        {
            store?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateNewHasOnePhysicalCreatorAndLeavesTheExistingStoreUntouched()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        byte[] key = [0xa1, 0x00, 0xb2];
        byte[] value = [0x10, 0x20, 0x30];
        SharedMemoryStoreOptions options = CreateOptions(
            $"sms-physical-creator-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            participantRecordCount: 2);
        using Store owner = CreateStore(options);
        Assert.Equal(StoreStatus.Success, owner.TryPublish(key, value));

        StoreOpenStatus duplicateStatus = Store.TryCreateOrOpen(options, out Store? duplicate);

        duplicate?.Dispose();
        Assert.Equal(StoreOpenStatus.AlreadyExists, duplicateStatus);
        Assert.Null(duplicate);
        AssertPublishedValue(owner, key, value);
    }

    [Theory]
    [InlineData(OpenMode.CreateNew, StoreOpenStatus.AlreadyExists)]
    [InlineData(OpenMode.OpenExisting, StoreOpenStatus.IncompatibleLayout)]
    [InlineData(OpenMode.CreateOrOpen, StoreOpenStatus.StoreBusy)]
    [Trait("Category", "Integration")]
    public void ExistingZeroHeaderIsNeverInitializationAuthority(
        OpenMode openMode,
        StoreOpenStatus expectedStatus)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-zero-header-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions rawCreate = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions requested = CreateOptions(name, openMode, participantRecordCount: 2);
        Assert.Equal(
            StoreOpenStatus.Success,
            SharedStorePlatform.TryOpenRegion(
                rawCreate,
                StoreWaitOptions.Default,
                out MemoryMappedStoreRegion? rawRegion));
        Assert.NotNull(rawRegion);

        using (rawRegion)
        {
            byte[] prefixBefore = ReadPrefix(rawRegion!, 512);
            Assert.All(prefixBefore, static value => Assert.Equal(0, value));

            StoreOpenStatus status = Store.TryCreateOrOpen(
                requested,
                StoreWaitOptions.NoWait,
                out Store? rejected);

            rejected?.Dispose();
            Assert.Equal(expectedStatus, status);
            Assert.Null(rejected);
            Assert.Equal(prefixBefore, ReadPrefix(rawRegion!, prefixBefore.Length));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RetiredSms1HeaderIsRejectedBeforePayloadProjectionOrMutation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        AssertSyntheticHeaderRejected(
            RetiredSms1Magic,
            requiredFeatures: 0,
            $"sms-retired-header-{Guid.NewGuid():N}");
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(3UL)]
    [InlineData(15UL)]
    [Trait("Category", "Integration")]
    public void MissingOrUnknownRequiredFeaturesAreRejectedBeforePayloadProjection(ulong requiredFeatures)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        AssertSyntheticHeaderRejected(
            CanonicalSms2Magic,
            requiredFeatures,
            $"sms-feature-mask-{requiredFeatures}-{Guid.NewGuid():N}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ParticipantCapacityIsPerHandleAndAClosedRecordCanBeReused()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-participant-reuse-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions open = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: 2);
        Store? anchor = null;
        Store? second = null;
        Store? rejected = null;
        Store? replacement = null;

        try
        {
            StoreOpenStatus anchorStatus = Store.TryCreateOrOpen(create, out anchor);
            StoreOpenStatus secondStatus = Store.TryCreateOrOpen(open, out second);
            StoreOpenStatus exhaustedStatus = Store.TryCreateOrOpen(open, out rejected);

            second?.Dispose();
            second = null;
            StoreOpenStatus replacementStatus = Store.TryCreateOrOpen(open, out replacement);

            Assert.Equal(StoreOpenStatus.Success, anchorStatus);
            Assert.NotNull(anchor);
            Assert.Equal(StoreOpenStatus.Success, secondStatus);
            Assert.Equal(StoreOpenStatus.ParticipantTableFull, exhaustedStatus);
            Assert.Null(rejected);
            Assert.Equal(StoreOpenStatus.Success, replacementStatus);
            Assert.NotNull(replacement);
            AssertCanonicalProtocol(anchor!);
            AssertCanonicalProtocol(replacement!);
        }
        finally
        {
            replacement?.Dispose();
            rejected?.Dispose();
            second?.Dispose();
            anchor?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void OpenExistingCanReopenWhileTheCanonicalMappingRemainsLive()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-reopen-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 3);
        SharedMemoryStoreOptions open = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: 3);
        using Store anchor = CreateStore(create);

        using (Store second = CreateStore(open))
        {
            AssertCanonicalProtocol(second);
        }

        using Store reopened = CreateStore(open);
        AssertCanonicalProtocol(anchor);
        AssertCanonicalProtocol(reopened);
    }

    [Theory]
    [InlineData(OpenMode.CreateNew)]
    [InlineData(OpenMode.OpenExisting)]
    [InlineData(OpenMode.CreateOrOpen)]
    [Trait("Category", "Integration")]
    public void ColdGateIsAcquiredBeforeAnyPhysicalMappingProbe(OpenMode contenderMode)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-cold-gate-before-map-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions creator = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions contender = CreateOptions(name, contenderMode, participantRecordCount: 2);
        long started = Stopwatch.GetTimestamp();
        Assert.Equal(
            StoreOpenStatus.Success,
            SharedStorePlatform.TryBeginOpen(
                creator,
                StoreWaitOptions.Default,
                started,
                out SharedStoreOpenScope? heldScope));
        Assert.NotNull(heldScope);

        using (heldScope)
        {
            (StoreOpenStatus Status, Store? Store) result = default;
            Exception? failure = null;
            using var finished = new ManualResetEventSlim(initialState: false);
            var contenderThread = new Thread(() =>
            {
                try
                {
                    StoreOpenStatus status = Store.TryCreateOrOpen(
                        contender,
                        StoreWaitOptions.NoWait,
                        out Store? opened);
                    result = (status, opened);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    finished.Set();
                }
            })
            {
                IsBackground = true,
                Name = "SharedMemoryStore cold gate-before-map contender"
            };
            contenderThread.Start();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);

            result.Store?.Dispose();
            Assert.Equal(StoreOpenStatus.StoreBusy, result.Status);
            Assert.Null(result.Store);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LinuxOpenRejectsActualMappedCapacityBelowTheFixedSms2Header()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string name = $"sms-truncated-header-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions rawCreate = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions open = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: 2);
        PlatformResourceName resource = PlatformResourceName.Create(name);
        Assert.Equal(
            StoreOpenStatus.Success,
            SharedStorePlatform.TryOpenRegion(
                rawCreate,
                StoreWaitOptions.Default,
                out MemoryMappedStoreRegion? rawRegion));
        Assert.NotNull(rawRegion);

        using (rawRegion)
        {
            WriteSyntheticHeader(rawRegion!, CanonicalSms2Magic, requiredFeatures: 7);
            TruncateLinuxRegion(resource.LinuxRegionPath, 511);

            StoreOpenStatus status = Store.TryCreateOrOpen(open, out Store? rejected);

            rejected?.Dispose();
            Assert.Equal(StoreOpenStatus.IncompatibleLayout, status);
            Assert.Null(rejected);
        }
    }

    private static void AssertSyntheticHeaderRejected(uint magic, ulong requiredFeatures, string name)
    {
        SharedMemoryStoreOptions rawCreate = CreateOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions open = CreateOptions(name, OpenMode.OpenExisting, participantRecordCount: 2);
        Assert.Equal(
            StoreOpenStatus.Success,
            SharedStorePlatform.TryOpenRegion(
                rawCreate,
                StoreWaitOptions.Default,
                out MemoryMappedStoreRegion? rawRegion));
        Assert.NotNull(rawRegion);

        using (rawRegion)
        {
            WriteSyntheticHeader(rawRegion!, magic, requiredFeatures);
            byte[] before = ReadPrefix(rawRegion!, 528);

            StoreOpenStatus status = Store.TryCreateOrOpen(open, out Store? rejected);

            rejected?.Dispose();
            Assert.Equal(StoreOpenStatus.IncompatibleLayout, status);
            Assert.Null(rejected);
            Assert.Equal(before, ReadPrefix(rawRegion!, before.Length));
        }
    }

    private static SharedMemoryStoreOptions CreateOptions(
        string name,
        OpenMode openMode,
        int participantRecordCount)
    {
        const int slotCount = 4;
        const int maxValueBytes = 64;
        const int maxDescriptorBytes = 8;
        const int maxKeyBytes = 8;
        const int leaseRecordCount = 8;

        return new SharedMemoryStoreOptions
        {
            Name = name,
            OpenMode = openMode,
            SlotCount = slotCount,
            MaxValueBytes = maxValueBytes,
            MaxDescriptorBytes = maxDescriptorBytes,
            MaxKeyBytes = maxKeyBytes,
            LeaseRecordCount = leaseRecordCount,
            ParticipantRecordCount = participantRecordCount,
            TotalBytes = StoreLayoutV2.CalculateRequiredBytes(
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount,
                participantRecordCount)
        };
    }

    private static Store CreateStore(SharedMemoryStoreOptions options)
    {
        StoreOpenStatus status = Store.TryCreateOrOpen(options, out Store? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }

    private static void AssertCanonicalProtocol(Store store)
    {
        Assert.Equal(2, store.ProtocolInfo.LayoutMajorVersion);
        Assert.Equal(0, store.ProtocolInfo.LayoutMinorVersion);
        Assert.Equal(2, store.ProtocolInfo.ResourceProtocolVersion);
        Assert.Equal(7UL, store.ProtocolInfo.RequiredFeatures);
        Assert.Equal(0UL, store.ProtocolInfo.OptionalFeatures);
    }

    private static void AssertPublishedValue(Store store, byte[] key, byte[] expectedValue)
    {
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        try
        {
            Assert.Equal(expectedValue, lease.ValueSpan.ToArray());
        }
        finally
        {
            Assert.Equal(StoreStatus.Success, lease.Release());
        }
    }

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static unsafe void WriteSyntheticHeader(
        MemoryMappedStoreRegion region,
        uint magic,
        ulong requiredFeatures)
    {
        Assert.True(region.Capacity >= 528);
        Span<byte> bytes = new(region.Pointer, 528);
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0..4], magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..6], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..8], 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..12], 512);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..16], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..24], requiredFeatures);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..32], 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..40], region.Capacity);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..48], 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..56], 2);
        bytes[512..528].Fill(0xa5);
    }

    private static void TruncateLinuxRegion(string path, long length)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(length);
        stream.Flush(flushToDisk: true);
        Assert.Equal(length, stream.Length);
    }

    private static unsafe byte[] ReadPrefix(MemoryMappedStoreRegion region, int requestedLength)
    {
        int length = checked((int)Math.Min(region.Capacity, requestedLength));
        var result = new byte[length];
        new ReadOnlySpan<byte>(region.Pointer, length).CopyTo(result);
        return result;
    }
}
