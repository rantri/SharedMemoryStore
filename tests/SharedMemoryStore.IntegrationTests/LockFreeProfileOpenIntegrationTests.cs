using System.Globalization;
using System.Runtime.InteropServices;
using SharedMemoryStore.IntegrationTests.TestSupport;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeProfileOpenIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void LinuxLegacyOpenRejectsBackingFileTruncatedBelowTheFixedHeader()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string name = $"sms-v1-truncated-header-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = CreateOptions(
            StoreProfile.Legacy,
            name,
            OpenMode.CreateNew,
            participantRecordCount: 2);
        SharedMemoryStoreOptions open = CreateOptions(
            StoreProfile.Legacy,
            name,
            OpenMode.OpenExisting,
            participantRecordCount: 2);
        PlatformResourceName resource = PlatformResourceName.Create(name);
        using Store owner = IntegrationStoreFactory.Create(create);

        TruncateLinuxRegion(resource.LinuxRegionPath, Marshal.SizeOf<StoreHeader>() - 1L);

        StoreOpenStatus status = Store.TryCreateOrOpen(open, out Store? rejected);

        rejected?.Dispose();
        Assert.Equal(StoreOpenStatus.IncompatibleLayout, status);
        Assert.Null(rejected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LinuxLegacyOpenRejectsValidHeaderWhosePayloadExtentWasTruncated()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string name = $"sms-v1-truncated-payload-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = CreateOptions(
            StoreProfile.Legacy,
            name,
            OpenMode.CreateNew,
            participantRecordCount: 2);
        SharedMemoryStoreOptions open = CreateOptions(
            StoreProfile.Legacy,
            name,
            OpenMode.OpenExisting,
            participantRecordCount: 2);
        PlatformResourceName resource = PlatformResourceName.Create(name);
        using Store owner = IntegrationStoreFactory.Create(create);
        Assert.True(create.TotalBytes > Marshal.SizeOf<StoreHeader>());

        TruncateLinuxRegion(resource.LinuxRegionPath, create.TotalBytes - 1);

        StoreOpenStatus status = Store.TryCreateOrOpen(open, out Store? rejected);

        rejected?.Dispose();
        Assert.Equal(StoreOpenStatus.IncompatibleLayout, status);
        Assert.Null(rejected);
    }

    [Theory]
    [InlineData(OpenMode.OpenExisting)]
    [InlineData(OpenMode.CreateOrOpen)]
    [Trait("Category", "Integration")]
    public void ExistingLegacyHeaderIsRejectedBeforeOversizedLockFreeViewProjection(OpenMode openMode)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var name = $"sms-v1-to-v2-header-first-{Guid.NewGuid():N}";
        var legacyOptions = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 4,
            leaseRecordCount: 2,
            openMode: OpenMode.CreateNew);
        var oversizedLockFreeOptions = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 128,
            maxValueBytes: 32 * 1024,
            maxDescriptorBytes: 256,
            maxKeyBytes: 128,
            leaseRecordCount: 256,
            participantRecordCount: 8,
            openMode: openMode);

        using var legacy = IntegrationStoreFactory.Create(legacyOptions);

        var status = Store.TryCreateOrOpen(oversizedLockFreeOptions, out var incompatible);

        incompatible?.Dispose();
        Assert.Equal(StoreOpenStatus.IncompatibleLayout, status);
        Assert.Null(incompatible);
        Assert.Equal(StoreProfile.Legacy, legacy.Profile);
        Assert.Equal(1, legacy.ProtocolInfo.LayoutMajorVersion);
    }

    [Theory]
    [InlineData(OpenMode.OpenExisting)]
    [InlineData(OpenMode.CreateOrOpen)]
    [Trait("Category", "Integration")]
    public void ExistingLockFreeHeaderIsRejectedBeforeOversizedLegacyViewProjection(OpenMode openMode)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var name = $"sms-v2-to-v1-header-first-{Guid.NewGuid():N}";
        var lockFreeOptions = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 4,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        var oversizedLegacyOptions = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 128,
            maxValueBytes: 32 * 1024,
            maxDescriptorBytes: 256,
            maxKeyBytes: 128,
            leaseRecordCount: 256,
            openMode: openMode);

        using var lockFree = IntegrationStoreFactory.Create(lockFreeOptions);

        var status = Store.TryCreateOrOpen(oversizedLegacyOptions, out var incompatible);

        incompatible?.Dispose();
        Assert.Equal(StoreOpenStatus.IncompatibleLayout, status);
        Assert.Null(incompatible);
        AssertLockFreeProtocol(lockFree);
    }

    [Theory]
    [InlineData(StoreProfile.Legacy, StoreProfile.LockFree)]
    [InlineData(StoreProfile.LockFree, StoreProfile.Legacy)]
    [Trait("Category", "Integration")]
    public void OppositeProfileCreateNewReportsAlreadyExists(
        StoreProfile existingProfile,
        StoreProfile requestedProfile)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var name = $"sms-mixed-create-new-{Guid.NewGuid():N}";
        var existingOptions = CreateOptions(existingProfile, name, OpenMode.CreateNew, participantRecordCount: 2);
        var requestedOptions = CreateOptions(requestedProfile, name, OpenMode.CreateNew, participantRecordCount: 2);

        using var existing = IntegrationStoreFactory.Create(existingOptions);

        var status = Store.TryCreateOrOpen(requestedOptions, out var duplicate);

        duplicate?.Dispose();
        Assert.Equal(StoreOpenStatus.AlreadyExists, status);
        Assert.Null(duplicate);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ParticipantCapacityIsPerHandleAndAClosedRecordCanBeReused()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var name = $"sms-participant-reuse-{Guid.NewGuid():N}";
        var createOptions = CreateOptions(StoreProfile.LockFree, name, OpenMode.CreateNew, participantRecordCount: 2);
        var openOptions = CreateOptions(StoreProfile.LockFree, name, OpenMode.OpenExisting, participantRecordCount: 2);
        Store? anchor = null;
        Store? second = null;
        Store? rejected = null;
        Store? replacement = null;

        try
        {
            var anchorStatus = Store.TryCreateOrOpen(createOptions, out anchor);
            var secondStatus = Store.TryCreateOrOpen(openOptions, out second);
            var exhaustedStatus = Store.TryCreateOrOpen(openOptions, out rejected);

            second?.Dispose();
            second = null;
            var replacementStatus = Store.TryCreateOrOpen(openOptions, out replacement);

            Assert.Equal(StoreOpenStatus.Success, anchorStatus);
            Assert.NotNull(anchor);
            Assert.Equal(StoreOpenStatus.Success, secondStatus);
            Assert.Equal(StoreOpenStatus.ParticipantTableFull, exhaustedStatus);
            Assert.Null(rejected);
            Assert.Equal(StoreOpenStatus.Success, replacementStatus);
            Assert.NotNull(replacement);
            AssertLockFreeProtocol(anchor!);
            AssertLockFreeProtocol(replacement!);
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
    public void ClosingFinalParticipantAllowsASecondLockFreeCreateNewLifecycle()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var name = $"sms-participant-final-close-{Guid.NewGuid():N}";
        var options = CreateOptions(StoreProfile.LockFree, name, OpenMode.CreateNew, participantRecordCount: 1);

        using (var first = IntegrationStoreFactory.Create(options))
        {
            AssertLockFreeProtocol(first);
        }

        using var recreated = IntegrationStoreFactory.Create(options);
        AssertLockFreeProtocol(recreated);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LinuxLockFreeHandlesKeepV1CompatibleOwnerLinesDuringIncompatibleLegacyOpen()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var name = $"sms-v2-owner-sidecar-{Guid.NewGuid():N}";
        var resourceName = PlatformResourceName.Create(name);
        var createOptions = CreateOptions(StoreProfile.LockFree, name, OpenMode.CreateNew, participantRecordCount: 2);
        var openOptions = CreateOptions(StoreProfile.LockFree, name, OpenMode.OpenExisting, participantRecordCount: 2);
        var legacyOptions = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 128,
            maxValueBytes: 32 * 1024,
            maxDescriptorBytes: 256,
            maxKeyBytes: 128,
            leaseRecordCount: 256,
            openMode: OpenMode.OpenExisting);

        using var first = IntegrationStoreFactory.Create(createOptions);
        var secondStatus = Store.TryCreateOrOpen(openOptions, out var second);
        Assert.Equal(StoreOpenStatus.Success, secondStatus);
        Assert.NotNull(second);

        try
        {
            var ownerLinesBefore = ReadOwnerLines(resourceName.LinuxOwnersPath);
            Assert.Equal(2, ownerLinesBefore.Length);
            Assert.All(ownerLinesBefore, AssertV1CompatibleCurrentProcessOwnerLine);

            var incompatibleStatus = Store.TryCreateOrOpen(legacyOptions, out var incompatible);
            incompatible?.Dispose();

            Assert.Equal(StoreOpenStatus.IncompatibleLayout, incompatibleStatus);
            Assert.Null(incompatible);
            Assert.True(File.Exists(resourceName.LinuxRegionPath));
            Assert.Equal(
                ownerLinesBefore.OrderBy(static line => line, StringComparer.Ordinal),
                ReadOwnerLines(resourceName.LinuxOwnersPath).OrderBy(static line => line, StringComparer.Ordinal));

            second!.Dispose();
            second = null;
            var ownerLinesAfterClose = ReadOwnerLines(resourceName.LinuxOwnersPath);
            Assert.Single(ownerLinesAfterClose);
            AssertV1CompatibleCurrentProcessOwnerLine(ownerLinesAfterClose[0]);
            AssertLockFreeProtocol(first);
        }
        finally
        {
            second?.Dispose();
        }
    }

    private static bool IsSupportedLockFreeHost()
    {
        return (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }

    private static SharedMemoryStoreOptions CreateOptions(
        StoreProfile profile,
        string name,
        OpenMode openMode,
        int participantRecordCount)
    {
        const int slotCount = 4;
        const int maxValueBytes = 64;
        const int maxDescriptorBytes = 8;
        const int maxKeyBytes = 8;
        const int leaseRecordCount = 8;

        return profile == StoreProfile.LockFree
            ? SharedMemoryStoreOptions.CreateLockFree(
                name,
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount,
                participantRecordCount,
                openMode)
            : SharedMemoryStoreOptions.Create(
                name,
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount,
                openMode);
    }

    private static void AssertLockFreeProtocol(Store store)
    {
        Assert.Equal(StoreProfile.LockFree, store.Profile);
        Assert.Equal(StoreProfile.LockFree, store.ProtocolInfo.Profile);
        Assert.Equal(2, store.ProtocolInfo.LayoutMajorVersion);
        Assert.Equal(0, store.ProtocolInfo.LayoutMinorVersion);
        Assert.Equal(2, store.ProtocolInfo.ResourceProtocolVersion);
    }

    private static string[] ReadOwnerLines(string path)
    {
        Assert.True(File.Exists(path));
        return File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .ToArray();
    }

    private static void AssertV1CompatibleCurrentProcessOwnerLine(string ownerLine)
    {
        var parts = ownerLine.Split(':', 3);
        Assert.Equal(3, parts.Length);
        Assert.True(int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId));
        Assert.Equal(Environment.ProcessId, processId);
        Assert.True(
            parts[1].StartsWith("proc-", StringComparison.Ordinal)
            || parts[1].StartsWith("utc-", StringComparison.Ordinal));
        Assert.True(Guid.TryParseExact(parts[2], "N", out _));
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
}
