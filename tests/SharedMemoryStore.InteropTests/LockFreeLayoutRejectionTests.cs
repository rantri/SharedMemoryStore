using System.Runtime.InteropServices;
using System.Text.Json;
using SharedMemoryStore.InteropTests.TestSupport;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.InteropTests;

public sealed class LockFreeLayoutRejectionTests
{
    public static TheoryData<string> V12OnlyRuntimes => new()
    {
        "cpp",
        "python"
    };

    [Fact]
    public void CompatibilityManifestSeparatesLayoutSupportFromHeaderRecognition()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "protocol", "compatibility.json")));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        var layouts = root.GetProperty("shared_protocol").GetProperty("layouts");
        _ = AssertLayout(layouts, "1.2", major: 1, minor: 2, resourceProtocol: 1, magic: "SMS1");
        JsonElement v2 = AssertLayout(
            layouts,
            "2.0",
            major: 2,
            minor: 0,
            resourceProtocol: 2,
            magic: "SMS2");
        Assert.Equal(7UL, v2.GetProperty("required_features_mask").GetUInt64());
        Assert.Equal(
            ["versioned_empty_spill_summary", "publication_intent", "pid_namespace_identity"],
            v2.GetProperty("required_features")
                .EnumerateArray()
                .Select(static feature => feature.GetString()
                    ?? throw new InvalidDataException("A required feature name cannot be null."))
                .ToArray());

        var distributions = root.GetProperty("distributions");
        var managed = FindDistribution(distributions, "NuGet");
        Assert.Equal("2.0.0", managed.GetProperty("version").GetString());
        AssertVersions(managed.GetProperty("creates_layouts"), "1.2", "2.0");
        AssertVersions(managed.GetProperty("reads_layouts"), "1.2", "2.0");
        AssertResourceProtocol(managed, "1.2", 1);
        AssertResourceProtocol(managed, "2.0", 2);

        AssertV12OnlyDistribution(FindDistribution(distributions, "CMake"), "c_abi");
        AssertV12OnlyDistribution(FindDistribution(distributions, "Python"), "requires_c_abi");
    }

    [Theory]
    [MemberData(nameof(V12OnlyRuntimes))]
    [Trait("Category", "Integration")]
    public async Task V12OnlyClientRejectsSms2BeforePayloadAndLeavesStoreUsable(string runtime)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        var name = $"sms-v2-reject-{runtime}-{Guid.NewGuid():N}";
        var options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 1,
            maxValueBytes: 8,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        var oversizedV12Bytes = SharedMemoryStoreOptions.CalculateRequiredBytes(
            slotCount: 64,
            maxValueBytes: 1024,
            maxDescriptorBytes: 32,
            maxKeyBytes: 32,
            leaseRecordCount: 64);
        Assert.True(oversizedV12Bytes > options.TotalBytes);

        Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(options, out var opened));
        Assert.NotNull(opened);
        using var store = opened!;

        byte[] key = [0x71];
        byte[] payload = [0xA5, 0x5A, 0xC3];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, payload));

        await using var agent = await AgentProcess.StartAsync(definition);
        foreach (var (openMode, expectedCode, expectedName) in new[]
                 {
                     (openMode: (int)OpenMode.OpenExisting, expectedCode: 4, expectedName: "IncompatibleLayout"),
                     (openMode: (int)OpenMode.CreateOrOpen, expectedCode: 4, expectedName: "IncompatibleLayout"),
                     (openMode: (int)OpenMode.CreateNew, expectedCode: 1, expectedName: "AlreadyExists")
                 })
        {
            var rejection = await agent.SendAsync(
                "open",
                InteropAssertions.OpenArguments(
                    $"rejected-{openMode}",
                    name,
                    openMode,
                    slotCount: 64,
                    maxValueBytes: 1024,
                    maxDescriptorBytes: 32,
                    maxKeyBytes: 32,
                    leaseRecordCount: 64));

            InteropAssertions.Status(rejection, expectedCode, expectedName);
            AssertPublishedValue(store, key, payload);
        }

        var secondOptions = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 1,
            maxValueBytes: 8,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.OpenExisting);
        Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(secondOptions, out var second));
        using (second)
        {
            Assert.NotNull(second);
            AssertPublishedValue(second!, key, payload);
        }
    }

    private static void AssertV12OnlyDistribution(JsonElement distribution, string abiProperty)
    {
        Assert.Equal("1.0", distribution.GetProperty(abiProperty).GetString());
        AssertVersions(distribution.GetProperty("creates_layouts"), "1.2");
        AssertVersions(distribution.GetProperty("reads_layouts"), "1.2");
        AssertVersions(distribution.GetProperty("recognizes_layout_headers"), "1.2", "2.0");
        AssertVersions(distribution.GetProperty("rejects_layouts"), "2.0");
        Assert.Equal("IncompatibleLayout", distribution.GetProperty("rejection_status").GetString());
        Assert.False(distribution.GetProperty("payload_access_before_rejection").GetBoolean());
        AssertResourceProtocol(distribution, "1.2", 1);
    }

    private static void AssertPublishedValue(Store store, byte[] key, byte[] expected)
    {
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var lease));
        using (lease)
        {
            Assert.True(lease.ValueSpan.SequenceEqual(expected));
        }
    }

    private static JsonElement AssertLayout(
        JsonElement layouts,
        string version,
        int major,
        int minor,
        int resourceProtocol,
        string magic)
    {
        var layout = layouts.EnumerateArray().Single(
            item => item.GetProperty("version").GetString() == version);
        Assert.Equal(major, layout.GetProperty("major").GetInt32());
        Assert.Equal(minor, layout.GetProperty("minor").GetInt32());
        Assert.Equal(resourceProtocol, layout.GetProperty("resource_protocol").GetInt32());
        Assert.Equal(magic, layout.GetProperty("magic").GetString());
        return layout;
    }

    private static JsonElement FindDistribution(JsonElement distributions, string ecosystem) =>
        distributions.EnumerateArray().Single(
            item => item.GetProperty("ecosystem").GetString() == ecosystem);

    private static void AssertResourceProtocol(JsonElement distribution, string layout, int expected) =>
        Assert.Equal(
            expected,
            distribution.GetProperty("layout_resource_protocols").GetProperty(layout).GetInt32());

    private static void AssertVersions(JsonElement actual, params string[] expected) =>
        Assert.Equal(expected, actual.EnumerateArray().Select(static value => value.GetString()).ToArray());

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SharedMemoryStore.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the SharedMemoryStore repository root.");
    }
}
