using System.Runtime.InteropServices;
using System.Text.Json;
using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.InteropTests;

public sealed class SingleProtocolLayoutInteropTests
{
    private static readonly string[] RuntimeNames = ["dotnet", "cpp", "python"];

    public static TheoryData<string> Runtimes => new()
    {
        "dotnet",
        "cpp",
        "python"
    };

    public static TheoryData<string, string> HeaderNamespaceFaultCells => new()
    {
        { "dotnet", "dotnet" },
        { "dotnet", "cpp" },
        { "dotnet", "python" },
        { "cpp", "dotnet" },
        { "cpp", "cpp" },
        { "cpp", "python" },
        { "python", "dotnet" },
        { "python", "cpp" },
        { "python", "python" }
    };

    public static TheoryData<string, string, string> HeaderCompatibilityFaultCells => new()
    {
        { "layoutMajorVersion", "dotnet", "dotnet" },
        { "layoutMajorVersion", "dotnet", "cpp" },
        { "layoutMajorVersion", "dotnet", "python" },
        { "layoutMajorVersion", "cpp", "dotnet" },
        { "layoutMajorVersion", "cpp", "cpp" },
        { "layoutMajorVersion", "cpp", "python" },
        { "layoutMajorVersion", "python", "dotnet" },
        { "layoutMajorVersion", "python", "cpp" },
        { "layoutMajorVersion", "python", "python" },
        { "requiredFeatures", "dotnet", "dotnet" },
        { "requiredFeatures", "dotnet", "cpp" },
        { "requiredFeatures", "dotnet", "python" },
        { "requiredFeatures", "cpp", "dotnet" },
        { "requiredFeatures", "cpp", "cpp" },
        { "requiredFeatures", "cpp", "python" },
        { "requiredFeatures", "python", "dotnet" },
        { "requiredFeatures", "python", "cpp" },
        { "requiredFeatures", "python", "python" }
    };

    [Fact]
    public void CompatibilityManifestPublishesOneSms2ProtocolForEveryDistribution()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "protocol", "compatibility.json")));
        JsonElement root = document.RootElement;

        Assert.Equal(3, root.GetProperty("schema_version").GetInt32());
        JsonElement shared = root.GetProperty("shared_protocol");
        JsonElement layout = shared.GetProperty("layout");
        Assert.Equal("2.0", layout.GetProperty("version").GetString());
        Assert.Equal(2, layout.GetProperty("major").GetInt32());
        Assert.Equal(0, layout.GetProperty("minor").GetInt32());
        Assert.Equal("SMS2", layout.GetProperty("magic").GetString());
        Assert.Equal(2, layout.GetProperty("resource_protocol").GetInt32());
        Assert.Equal(7UL, layout.GetProperty("required_features_mask").GetUInt64());
        Assert.Equal(0UL, layout.GetProperty("optional_features_mask").GetUInt64());
        Assert.Equal(
            new[] { "versioned_empty_spill_summary", "publication_intent", "pid_namespace_identity" },
            layout.GetProperty("required_features")
                .EnumerateArray()
                .Select(static feature => feature.GetString()
                    ?? throw new InvalidDataException("A required feature name cannot be null."))
                .ToArray());
        Assert.Equal(
            "reject-before-payload-access",
            shared.GetProperty("noncurrent_mapping_policy").GetString());
        Assert.False(shared.GetProperty("in_place_conversion").GetBoolean());
        Assert.False(shared.TryGetProperty("layouts", out _));

        JsonElement distributions = root.GetProperty("distributions");
        AssertDistribution(FindDistribution(distributions, "NuGet"), "3.0.0");
        JsonElement native = FindDistribution(distributions, "CMake");
        AssertDistribution(native, "1.0.0");
        Assert.Equal("2.0", native.GetProperty("c_abi").GetString());
        JsonElement python = FindDistribution(distributions, "Python");
        AssertDistribution(python, "1.0.0");
        Assert.Equal("2.0", python.GetProperty("requires_c_abi").GetString());
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    [Trait("Category", "Integration")]
    public async Task EveryRuntimeReadsSms2AndRejectsMismatchedOpenWithoutPayloadMutation(string runtime)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        AgentDefinition definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        string name = $"sms-single-protocol-{runtime}-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 1,
            maxValueBytes: 8,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(options, out Store? opened));
        using Store store = Assert.IsType<Store>(opened);
        byte[] key = [0x71];
        byte[] payload = [0xA5, 0x5A, 0xC3];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, payload));

        await using var agent = await AgentProcess.StartAsync(definition);
        AgentResponse accepted = await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "current",
                name,
                openMode: (int)OpenMode.OpenExisting,
                slotCount: 1,
                maxValueBytes: 8,
                maxDescriptorBytes: 4,
                maxKeyBytes: 8,
                leaseRecordCount: 2,
                participantRecordCount: 2));
        InteropAssertions.Success(accepted);
        JsonElement openResult = accepted.Result!.Value;
        Assert.Equal(2, openResult.GetProperty("participantRecordCount").GetInt32());
        AssertProtocolIdentity(openResult.GetProperty("protocolInfo"));

        AgentResponse acquired = await agent.SendAsync("acquire", new
        {
            storeId = "current",
            leaseId = "lease",
            key = AgentProtocol.EncodeBytes(key)
        });
        InteropAssertions.Success(acquired);
        Assert.Equal(payload, InteropAssertions.Decode(acquired, "value"));
        InteropAssertions.Success(await agent.SendAsync("release", new { leaseId = "lease" }));
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "current" }));

        AgentResponse mismatch = await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "mismatch",
                name,
                openMode: (int)OpenMode.OpenExisting,
                slotCount: 2,
                maxValueBytes: 8,
                maxDescriptorBytes: 4,
                maxKeyBytes: 8,
                leaseRecordCount: 2,
                participantRecordCount: 2));
        InteropAssertions.Status(mismatch, 4, "IncompatibleLayout");
        AssertPublishedValue(store, key, payload);
    }

    [Theory]
    [MemberData(
        nameof(CoreExchangeMatrixTests.OrderedRuntimePairs),
        MemberType = typeof(CoreExchangeMatrixTests))]
    public async Task ThreeCreatorDirectionsCoverAllNineOpenAndIdentityCells(
        string creatorRuntime,
        string openerRuntime)
    {
        AgentDefinition creatorDefinition = AgentDefinition.Resolve(creatorRuntime);
        AgentDefinition openerDefinition = AgentDefinition.Resolve(openerRuntime);
        if (!creatorDefinition.IsAvailable() || !openerDefinition.IsAvailable())
        {
            return;
        }

        await using var creator = await AgentProcess.StartAsync(creatorDefinition);
        await using var opener = await AgentProcess.StartAsync(openerDefinition);
        string name = $"sms-open-identity-{creatorRuntime}-{openerRuntime}-{Guid.NewGuid():N}";
        AgentResponse created = await creator.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "creator",
                name,
                openMode: 0,
                slotCount: 3,
                leaseRecordCount: 3,
                participantRecordCount: 3));
        AssertOpenIdentity(created, participantRecordCount: 3);
        AgentResponse opened = await opener.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "opener",
                name,
                openMode: 1,
                slotCount: 3,
                leaseRecordCount: 3,
                participantRecordCount: 3));
        AssertOpenIdentity(opened, participantRecordCount: 3);
        AgentResponse attached = await opener.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "attached",
                name,
                openMode: 2,
                slotCount: 3,
                leaseRecordCount: 3,
                participantRecordCount: 3));
        AssertOpenIdentity(attached, participantRecordCount: 3);
        InteropAssertions.Status(await opener.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "forbidden-second-creator",
                name,
                openMode: 0,
                slotCount: 3,
                leaseRecordCount: 3,
                participantRecordCount: 3)), 1, "AlreadyExists");

        AssertAgentIdentity(await creator.SendAsync<object?>("ping", null), creatorRuntime);
        AssertAgentIdentity(await opener.SendAsync<object?>("ping", null), openerRuntime);
        InteropAssertions.Success(await opener.SendAsync("close", new { storeId = "attached" }));
        InteropAssertions.Success(await opener.SendAsync("close", new { storeId = "opener" }));
        InteropAssertions.Success(await creator.SendAsync("close", new { storeId = "creator" }));
    }

    [Fact]
    public async Task ThreeRuntimeCreateNewRaceHasOneAuthorityAndParticipantCapacityIsReusable()
    {
        AgentDefinition[] definitions = RuntimeNames.Select(AgentDefinition.Resolve).ToArray();
        if (definitions.Any(definition => !definition.IsAvailable()))
        {
            return;
        }

        var agents = new List<AgentProcess>();
        try
        {
            foreach (AgentDefinition definition in definitions)
            {
                agents.Add(await AgentProcess.StartAsync(definition));
            }

            string name = $"sms-create-authority-{Guid.NewGuid():N}";
            object[] createArguments = RuntimeNames
                .Select(runtime => InteropAssertions.OpenArguments(
                    $"candidate-{runtime}",
                    name,
                    openMode: 0,
                    slotCount: 3,
                    leaseRecordCount: 3,
                    participantRecordCount: 3))
                .ToArray();
            AgentResponse[] raced = await Task.WhenAll(
                agents.Select((agent, index) => agent.SendAsync("open", createArguments[index])));
            Assert.Single(raced, response => response.Status.Code == 0);
            Assert.Equal(2, raced.Count(response => response.Status.Code == 1));
            Assert.All(
                raced.Where(response => response.Status.Code == 1),
                response => InteropAssertions.Status(response, 1, "AlreadyExists"));

            for (var index = 0; index < agents.Count; index++)
            {
                if (raced[index].Status.Code == 0)
                {
                    AssertOpenIdentity(raced[index], participantRecordCount: 3);
                    continue;
                }

                AssertOpenIdentity(await agents[index].SendAsync(
                    "open",
                    InteropAssertions.OpenArguments(
                        $"candidate-{RuntimeNames[index]}",
                        name,
                        openMode: 1,
                        slotCount: 3,
                        leaseRecordCount: 3,
                        participantRecordCount: 3)), participantRecordCount: 3);
            }

            await using var replacement = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
            object replacementArguments = InteropAssertions.OpenArguments(
                "replacement",
                name,
                openMode: 1,
                slotCount: 3,
                leaseRecordCount: 3,
                participantRecordCount: 3);
            InteropAssertions.Status(
                await replacement.SendAsync("open", replacementArguments),
                11,
                "ParticipantTableFull");
            InteropAssertions.Success(await agents[1].SendAsync(
                "close",
                new { storeId = $"candidate-{RuntimeNames[1]}" }));
            AssertOpenIdentity(
                await replacement.SendAsync("open", replacementArguments),
                participantRecordCount: 3);
            InteropAssertions.Success(await replacement.SendAsync("close", new { storeId = "replacement" }));

            for (var index = agents.Count - 1; index >= 0; index--)
            {
                if (index != 1)
                {
                    InteropAssertions.Success(await agents[index].SendAsync(
                        "close",
                        new { storeId = $"candidate-{RuntimeNames[index]}" }));
                }
            }
        }
        finally
        {
            for (var index = agents.Count - 1; index >= 0; index--)
            {
                await agents[index].DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HeaderNamespaceFaultCells))]
    public async Task EveryRuntimeRawFaultInjectorAppliesCanonicalPidNamespaceAdmissionWithoutParallelCreation(
        string injectorRuntime,
        string openerRuntime)
    {
        AgentDefinition injectorDefinition = AgentDefinition.Resolve(injectorRuntime);
        AgentDefinition openerDefinition = AgentDefinition.Resolve(openerRuntime);
        if (!injectorDefinition.IsAvailable() || !openerDefinition.IsAvailable())
        {
            return;
        }

        await using var injector = await AgentProcess.StartAsync(injectorDefinition);
        await using var opener = await AgentProcess.StartAsync(openerDefinition);
        string name = $"sms-header-namespace-{injectorRuntime}-{openerRuntime}-{Guid.NewGuid():N}";
        object options = InteropAssertions.OpenArguments(
            "injector",
            name,
            openMode: 0,
            slotCount: 2,
            leaseRecordCount: 2,
            participantRecordCount: 2);
        AssertOpenIdentity(await injector.SendAsync("open", options), participantRecordCount: 2);
        AgentResponse mutation = await injector.SendAsync(
            AgentProtocolCatalog.Command.InjectRawFault,
            new
            {
                storeId = "injector",
                target = "headerNamespace",
                replacementPidNamespaceId = ulong.MaxValue
            });
        InteropAssertions.Success(mutation);
        Assert.NotEqual(
            mutation.Result!.Value.GetProperty("originalPidNamespaceId").GetUInt64(),
            mutation.Result!.Value.GetProperty("replacementPidNamespaceId").GetUInt64());

        AgentResponse admitted = await opener.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "rejected",
                name,
                openMode: 1,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2));
        if (OperatingSystem.IsLinux())
        {
            // Linux admits a different or unproven namespace conservatively by
            // irreversibly publishing Mixed mode before participant registration.
            // Ordinary KV access remains available, while Registering-owner
            // recovery is disabled for the now-ambiguous namespace evidence.
            AssertOpenIdentity(admitted, participantRecordCount: 2);
        }
        else
        {
            // Windows has no PID-namespace identity and therefore requires the
            // canonical zero header value.
            InteropAssertions.Status(admitted, 4, "IncompatibleLayout");
        }
        InteropAssertions.Status(await opener.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "no-parallel-creator",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2)), 1, "AlreadyExists");
        if (OperatingSystem.IsLinux())
        {
            InteropAssertions.Success(await opener.SendAsync(
                "close",
                new { storeId = "rejected" }));
        }
        InteropAssertions.Success(await injector.SendAsync("close", new { storeId = "injector" }));
    }

    [Theory]
    [MemberData(nameof(HeaderCompatibilityFaultCells))]
    public async Task EveryRuntimeRawFaultInjectorProvesRetiredMajorAndRequiredFeatureRejection(
        string target,
        string injectorRuntime,
        string openerRuntime)
    {
        AgentDefinition injectorDefinition = AgentDefinition.Resolve(injectorRuntime);
        AgentDefinition openerDefinition = AgentDefinition.Resolve(openerRuntime);
        if (!injectorDefinition.IsAvailable() || !openerDefinition.IsAvailable())
        {
            return;
        }

        await using var injector = await AgentProcess.StartAsync(injectorDefinition);
        await using var opener = await AgentProcess.StartAsync(openerDefinition);
        string name = $"sms-header-{target}-{injectorRuntime}-{openerRuntime}-{Guid.NewGuid():N}";
        AssertOpenIdentity(await injector.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "injector",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2)), participantRecordCount: 2);

        const ulong unsupportedRequiredFeatures = 7UL | (1UL << 63);
        object faultArguments = target switch
        {
            "layoutMajorVersion" => new
            {
                storeId = "injector",
                target,
                replacementLayoutMajorVersion = 1
            },
            "requiredFeatures" => new
            {
                storeId = "injector",
                target,
                replacementRequiredFeatures = unsupportedRequiredFeatures
            },
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown header fault target.")
        };
        AgentResponse mutation = await injector.SendAsync(
            AgentProtocolCatalog.Command.InjectRawFault,
            faultArguments);
        InteropAssertions.Success(mutation);
        Assert.Equal(target, mutation.Result!.Value.GetProperty("target").GetString());
        Assert.Equal(
            target == "layoutMajorVersion" ? 2L : 7L,
            mutation.Result!.Value.GetProperty("originalRaw").GetInt64());
        Assert.Equal(
            target == "layoutMajorVersion" ? 1L : unchecked((long)unsupportedRequiredFeatures),
            mutation.Result!.Value.GetProperty("replacementRaw").GetInt64());

        foreach (int openMode in new[] { 1, 2 })
        {
            InteropAssertions.Status(await opener.SendAsync(
                "open",
                InteropAssertions.OpenArguments(
                    $"rejected-{openMode}",
                    name,
                    openMode,
                    slotCount: 2,
                    leaseRecordCount: 2,
                    participantRecordCount: 2)), 4, "IncompatibleLayout");
        }

        InteropAssertions.Status(await opener.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "no-parallel-creator",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2)), 1, "AlreadyExists");
        InteropAssertions.Success(await injector.SendAsync("close", new { storeId = "injector" }));
    }

    private static void AssertDistribution(JsonElement distribution, string version)
    {
        Assert.Equal(version, distribution.GetProperty("version").GetString());
        AssertVersions(distribution.GetProperty("creates_layouts"), "2.0");
        AssertVersions(distribution.GetProperty("reads_layouts"), "2.0");
        Assert.Equal(2, distribution.GetProperty("resource_protocol").GetInt32());
        Assert.Equal(7UL, distribution.GetProperty("required_features_mask").GetUInt64());
        Assert.False(distribution.TryGetProperty("recognizes_layout_headers", out _));
        Assert.False(distribution.TryGetProperty("rejects_layouts", out _));
        Assert.False(distribution.TryGetProperty("layout_resource_protocols", out _));
    }

    private static void AssertProtocolIdentity(JsonElement protocol)
    {
        Assert.Equal(2, protocol.GetProperty("layoutMajorVersion").GetInt32());
        Assert.Equal(0, protocol.GetProperty("layoutMinorVersion").GetInt32());
        Assert.Equal(2, protocol.GetProperty("resourceProtocolVersion").GetInt32());
        Assert.Equal(7UL, protocol.GetProperty("requiredFeatures").GetUInt64());
        Assert.Equal(0UL, protocol.GetProperty("optionalFeatures").GetUInt64());
    }

    private static void AssertAgentIdentity(AgentResponse response, string runtime)
    {
        InteropAssertions.Success(response);
        JsonElement result = response.Result!.Value;
        Assert.Equal(runtime, result.GetProperty("runtime").GetString());
        Assert.Equal(2, result.GetProperty("protocolVersion").GetInt32());
        AssertProtocolIdentity(result);
    }

    private static void AssertOpenIdentity(AgentResponse response, int participantRecordCount)
    {
        InteropAssertions.Success(response);
        JsonElement result = response.Result!.Value;
        Assert.Equal(participantRecordCount, result.GetProperty("participantRecordCount").GetInt32());
        AssertProtocolIdentity(result.GetProperty("protocolInfo"));
    }

    private static void AssertPublishedValue(Store store, byte[] key, byte[] expected)
    {
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        using (lease)
        {
            Assert.Equal(expected, lease.ValueSpan.ToArray());
        }
    }

    private static JsonElement FindDistribution(JsonElement distributions, string ecosystem) =>
        distributions.EnumerateArray().Single(
            item => item.GetProperty("ecosystem").GetString() == ecosystem);

    private static void AssertVersions(JsonElement actual, params string[] expected) =>
        Assert.Equal(expected, actual.EnumerateArray().Select(static value => value.GetString()).ToArray());

    private static bool IsSupportedHost() =>
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
