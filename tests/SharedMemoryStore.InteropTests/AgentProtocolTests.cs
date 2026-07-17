using System.Text.Json;
using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;

namespace SharedMemoryStore.InteropTests;

public sealed class AgentProtocolTests
{
    public static TheoryData<string> Runtimes => new()
    {
        "dotnet",
        "cpp",
        "python"
    };

    [Fact]
    public void RequestUsesOneLfDelimitedJsonFrameAndBase64BinaryFields()
    {
        var key = new byte[] { 0, 1, 2, 254, 255 };
        var value = new byte[] { 10, 13, 32, 92 };
        var request = new AgentRequest
        {
            Id = "request-1",
            Command = "publish",
            Arguments = AgentProtocol.ToJsonElement(new
            {
                key = AgentProtocol.EncodeBytes(key),
                value = AgentProtocol.EncodeBytes(value)
            })
        };

        var frame = AgentProtocol.SerializeRequestLine(request);

        Assert.True(frame.EndsWith('\n'));
        Assert.Equal(-1, frame[..^1].IndexOfAny(['\r', '\n']));

        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        Assert.Equal("request-1", root.GetProperty("id").GetString());
        Assert.Equal("publish", root.GetProperty("command").GetString());
        Assert.Equal(key, AgentProtocol.DecodeBytes(root.GetProperty("arguments").GetProperty("key").GetString()!));
        Assert.Equal(value, AgentProtocol.DecodeBytes(root.GetProperty("arguments").GetProperty("value").GetString()!));
    }

    [Fact]
    public void ResponseRoundTripPreservesNumericAndSymbolicStatus()
    {
        var response = new AgentResponse
        {
            Id = "request-2",
            Ok = true,
            Status = new AgentStatus { Code = 0, Name = "Success" },
            Result = AgentProtocol.ToJsonElement(new { value = AgentProtocol.EncodeBytes([1, 2, 3]) })
        };

        var parsed = AgentProtocol.ParseResponse(AgentProtocol.SerializeResponseLine(response).TrimEnd('\n'));

        Assert.Equal("request-2", parsed.Id);
        Assert.True(parsed.Ok);
        Assert.Equal(0, parsed.Status.Code);
        Assert.Equal("Success", parsed.Status.Name);
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            AgentProtocol.DecodeBytes(parsed.Result!.Value.GetProperty("value").GetString()!));
    }

    [Fact]
    public async Task ReaderConsumesExactlyOneRequestAtATimeAndReportsEndOfInput()
    {
        var first = new AgentRequest { Id = "1", Command = "ping" };
        var second = new AgentRequest { Id = "2", Command = "ping" };
        using var reader = new StringReader(
            AgentProtocol.SerializeRequestLine(first) + AgentProtocol.SerializeRequestLine(second));

        var parsedFirst = await AgentProtocol.ReadRequestAsync(reader);
        var parsedSecond = await AgentProtocol.ReadRequestAsync(reader);
        var end = await AgentProtocol.ReadRequestAsync(reader);

        Assert.Equal("1", parsedFirst!.Id);
        Assert.Equal("2", parsedSecond!.Id);
        Assert.Null(end);
    }

    [Fact]
    public async Task WriterFlushesOneCompleteResponseFrame()
    {
        var response = new AgentResponse
        {
            Id = "request-3",
            Ok = false,
            Status = new AgentStatus { Code = 7, Name = "NotFound" },
            Error = new AgentError { Code = "not_found", Message = "The key was not found." }
        };
        using var writer = new StringWriter();

        await AgentProtocol.WriteResponseAsync(writer, response);

        var output = writer.ToString();
        Assert.True(output.EndsWith('\n'));
        Assert.Equal(-1, output[..^1].IndexOfAny(['\r', '\n']));
        Assert.Equal(response, AgentProtocol.ParseResponse(output.TrimEnd('\n')));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"1\",\"command\":\"ping\"}\n{\"id\":\"2\",\"command\":\"ping\"}")]
    public void InvalidRequestFramesAreRejected(string frame)
    {
        Assert.Throws<JsonException>(() => AgentProtocol.ParseRequest(frame));
    }

    [Fact]
    public async Task HostProducesOnlyProtocolResponsesForPingAndUnsupportedCommands()
    {
        var ping = new AgentRequest { Id = "ping-1", Command = "ping" };
        var unsupported = new AgentRequest { Id = "future-1", Command = "future-command" };
        using var input = new StringReader(
            AgentProtocol.SerializeRequestLine(ping) + AgentProtocol.SerializeRequestLine(unsupported));
        using var output = new StringWriter();

        var exitCode = await AgentHost.RunAsync(input, output);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        var pingResponse = AgentProtocol.ParseResponse(lines[0]);
        Assert.True(pingResponse.Ok);
        Assert.Equal("Success", pingResponse.Status.Name);
        JsonElement pingResult = pingResponse.Result!.Value;
        Assert.Equal("dotnet", pingResult.GetProperty("runtime").GetString());
        Assert.Equal(2, pingResult.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1, pingResult.GetProperty("checkpointCatalogVersion").GetInt32());
        Assert.Equal(2, pingResult.GetProperty("layoutMajorVersion").GetInt32());
        Assert.Equal(0, pingResult.GetProperty("layoutMinorVersion").GetInt32());
        Assert.Equal(2, pingResult.GetProperty("resourceProtocolVersion").GetInt32());
        Assert.Equal(7UL, pingResult.GetProperty("requiredFeatures").GetUInt64());
        Assert.Equal(0UL, pingResult.GetProperty("optionalFeatures").GetUInt64());

        var unsupportedResponse = AgentProtocol.ParseResponse(lines[1]);
        Assert.False(unsupportedResponse.Ok);
        Assert.Equal("UnsupportedCommand", unsupportedResponse.Status.Name);
        Assert.Equal("unsupported_command", unsupportedResponse.Error!.Code);
    }

    [Fact]
    public async Task ManagedAgentPublishesTheExactCanonicalCheckpointCatalog()
    {
        var request = new AgentRequest
        {
            Id = "catalog-1",
            Command = AgentProtocolCatalog.Command.CheckpointCatalog
        };
        using var input = new StringReader(AgentProtocol.SerializeRequestLine(request));
        using var output = new StringWriter();

        Assert.Equal(0, await AgentHost.RunAsync(input, output));

        AgentResponse response = AgentProtocol.ParseResponse(output.ToString().TrimEnd('\n'));
        AssertCanonicalCheckpointCatalog(response);
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task EveryRuntimePublishesTheExactCanonicalCheckpointCatalog(string runtime)
    {
        AgentDefinition definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        await using var agent = await AgentProcess.StartAsync(definition);
        AssertCanonicalCheckpointCatalog(await agent.SendAsync<object?>(
            AgentProtocolCatalog.Command.CheckpointCatalog,
            null));
    }

    private static void AssertCanonicalCheckpointCatalog(AgentResponse response)
    {
        InteropAssertions.Success(response);
        JsonElement result = response.Result!.Value;
        Assert.Equal(
            AgentProtocolCatalog.CheckpointCatalogVersion,
            result.GetProperty("checkpointCatalogVersion").GetInt32());
        JsonElement.ArrayEnumerator checkpoints = result.GetProperty("checkpoints").EnumerateArray();
        var entries = checkpoints.ToArray();
        Assert.Equal(AgentProtocolCatalog.CheckpointCount, entries.Length);
        Assert.Equal(
            AgentProtocolCatalog.Checkpoints.Select(checkpoint => (int)checkpoint),
            entries.Select(entry => entry.GetProperty("id").GetInt32()));
        Assert.Equal(
            AgentProtocolCatalog.Checkpoints.Select(checkpoint => checkpoint.ToString()),
            entries.Select(entry => entry.GetProperty("name").GetString()));
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("family").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("position").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()));
        });
    }

    [Fact]
    public async Task ManagedAgentChecksumMatchesTheCanonicalBinaryFnv1a64Shape()
    {
        await using var agent = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        string name = $"sms-managed-checksum-{Guid.NewGuid():N}";
        InteropAssertions.Success(await agent.SendAsync(
            AgentProtocolCatalog.Command.Open,
            InteropAssertions.OpenArguments("store", name, openMode: 0, participantRecordCount: 2)));
        byte[] key = "segments"u8.ToArray();
        InteropAssertions.Success(await agent.SendAsync(
            AgentProtocolCatalog.Command.PublishSegments,
            new
            {
                storeId = "store",
                key = AgentProtocol.EncodeBytes(key),
                segments = new[]
                {
                    AgentProtocol.EncodeBytes(new byte[] { 0x61, 0 }),
                    string.Empty,
                    AgentProtocol.EncodeBytes(new byte[] { 0x62, 0xff })
                },
                descriptor = AgentProtocol.EncodeBytes("meta"u8)
            }));
        InteropAssertions.Success(await agent.SendAsync(
            AgentProtocolCatalog.Command.Acquire,
            new
            {
                storeId = "store",
                leaseId = "held",
                key = AgentProtocol.EncodeBytes(key)
            }));

        AgentResponse checksum = await agent.SendAsync(
            AgentProtocolCatalog.Command.Checksum,
            new { leaseId = "held" });
        InteropAssertions.Success(checksum);
        JsonElement result = checksum.Result!.Value;
        Assert.Equal("held", result.GetProperty("leaseId").GetString());
        Assert.Equal(4, result.GetProperty("valueLength").GetInt32());
        Assert.Equal(4, result.GetProperty("descriptorLength").GetInt32());
        Assert.Equal("ab4072820d3fd4d7", result.GetProperty("valueChecksum").GetString());
        Assert.Equal("4320e9a2e32eac38", result.GetProperty("descriptorChecksum").GetString());

        InteropAssertions.Success(await agent.SendAsync(
            AgentProtocolCatalog.Command.Release,
            new { leaseId = "held" }));
        InteropAssertions.Status(await agent.SendAsync(
            AgentProtocolCatalog.Command.Checksum,
            new { leaseId = "held" }), 8, "InvalidLease");
        InteropAssertions.Success(await agent.SendAsync(
            AgentProtocolCatalog.Command.Close,
            new { storeId = "store" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task EveryRuntimePublishesStableProtocolTwoIdentityAndParticipantCapacity(
        string runtime)
    {
        AgentDefinition definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        await using var agent = await AgentProcess.StartAsync(definition);
        AssertAgentIdentity(await agent.SendAsync<object?>("ping", null), runtime);
        AssertAgentIdentity(await agent.SendAsync<object?>("ping", null), runtime);

        string name = $"sms-agent-contract-{runtime}-{Guid.NewGuid():N}";
        object primaryArguments = InteropAssertions.OpenArguments(
            "primary",
            name,
            openMode: 0,
            slotCount: 2,
            leaseRecordCount: 2,
            participantRecordCount: 2);
        AgentResponse primary = await agent.SendAsync("open", primaryArguments);
        AssertOpenIdentity(primary, participantRecordCount: 2);
        AgentResponse peer = await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "peer",
                name,
                openMode: 1,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2));
        AssertOpenIdentity(peer, participantRecordCount: 2);

        object reusedArguments = InteropAssertions.OpenArguments(
            "reused",
            name,
            openMode: 1,
            slotCount: 2,
            leaseRecordCount: 2,
            participantRecordCount: 2);
        InteropAssertions.Status(
            await agent.SendAsync("open", reusedArguments),
            11,
            "ParticipantTableFull");
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "peer" }));
        AssertOpenIdentity(
            await agent.SendAsync("open", reusedArguments),
            participantRecordCount: 2);

        AssertAgentIdentity(await agent.SendAsync<object?>("ping", null), runtime);
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "reused" }));
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "primary" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task EveryRuntimeUsesCanonicalOpenAndStoreStatusNumbers(string runtime)
    {
        AgentDefinition definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        await using var agent = await AgentProcess.StartAsync(definition);
        InteropAssertions.Status(await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "invalid",
                $"sms-invalid-options-{runtime}-{Guid.NewGuid():N}",
                openMode: 0,
                participantRecordCount: 0)), 3, "InvalidOptions");
        InteropAssertions.Status(await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "missing-store",
                $"sms-not-found-{runtime}-{Guid.NewGuid():N}",
                openMode: 1,
                participantRecordCount: 2)), 2, "NotFound");

        string name = $"sms-agent-status-{runtime}-{Guid.NewGuid():N}";
        AssertOpenIdentity(await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "store",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2)), participantRecordCount: 2);
        InteropAssertions.Status(await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "duplicate-create",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2)), 1, "AlreadyExists");
        InteropAssertions.Status(await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "mismatch",
                name,
                openMode: 1,
                slotCount: 3,
                leaseRecordCount: 2,
                participantRecordCount: 2)), 4, "IncompatibleLayout");

        string key = AgentProtocol.EncodeBytes(new byte[] { 0x31, 0, 0xff });
        InteropAssertions.Status(await agent.SendAsync("acquire", new
        {
            storeId = "store",
            leaseId = "missing",
            key
        }), 2, "NotFound");
        InteropAssertions.Success(await agent.SendAsync("publish", new
        {
            storeId = "store",
            key,
            value = AgentProtocol.EncodeBytes(new byte[] { 0x41, 0, 0x42 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Status(await agent.SendAsync("publish", new
        {
            storeId = "store",
            key,
            value = AgentProtocol.EncodeBytes(new byte[] { 0x51 }),
            descriptor = string.Empty
        }), 1, "DuplicateKey");
        InteropAssertions.Status(
            await agent.SendAsync("release", new { leaseId = "missing" }),
            8,
            "InvalidLease");
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "store" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task EveryRuntimeHonorsTotalBytesAndReplacesThePriorStoreIdBeforeOpen(
        string runtime)
    {
        AgentDefinition definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        const int slotCount = 2;
        const int maxValueBytes = 32;
        const int maxDescriptorBytes = 8;
        const int maxKeyBytes = 8;
        const int leaseRecordCount = 2;
        const int participantRecordCount = 3;
        long requiredBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount);
        string name = $"sms-agent-total-bytes-{runtime}-{Guid.NewGuid():N}";

        object Arguments(string storeId, int openMode, long totalBytes) => new
        {
            storeId,
            name,
            openMode,
            totalBytes,
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount,
            enableLeaseRecovery = true
        };

        await using var agent = await AgentProcess.StartAsync(definition);
        AssertOpenIdentity(
            await agent.SendAsync("open", Arguments("anchor", 0, requiredBytes)),
            participantRecordCount);
        AssertOpenIdentity(
            await agent.SendAsync("open", Arguments("replace", 1, requiredBytes)),
            participantRecordCount);

        InteropAssertions.Status(
            await agent.SendAsync("open", Arguments("replace", 1, 1)),
            6,
            "InsufficientCapacity");

        AssertOpenIdentity(
            await agent.SendAsync("open", Arguments("replace", 1, requiredBytes)),
            participantRecordCount);
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "replace" }));
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "anchor" }));
    }

    private static void AssertAgentIdentity(AgentResponse response, string runtime)
    {
        InteropAssertions.Success(response);
        JsonElement result = response.Result!.Value;
        Assert.Equal(runtime, result.GetProperty("runtime").GetString());
        Assert.Equal(AgentProtocolCatalog.AgentProtocolVersion, result.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(
            AgentProtocolCatalog.CheckpointCatalogVersion,
            result.GetProperty("checkpointCatalogVersion").GetInt32());
        AssertProtocolIdentity(result);
    }

    private static void AssertOpenIdentity(AgentResponse response, int participantRecordCount)
    {
        InteropAssertions.Success(response);
        JsonElement result = response.Result!.Value;
        Assert.Equal(participantRecordCount, result.GetProperty("participantRecordCount").GetInt32());
        AssertProtocolIdentity(result.GetProperty("protocolInfo"));
    }

    private static void AssertProtocolIdentity(JsonElement identity)
    {
        Assert.Equal(
            AgentProtocolCatalog.ProtocolIdentity.LayoutMajorVersion,
            identity.GetProperty("layoutMajorVersion").GetInt32());
        Assert.Equal(
            AgentProtocolCatalog.ProtocolIdentity.LayoutMinorVersion,
            identity.GetProperty("layoutMinorVersion").GetInt32());
        Assert.Equal(
            AgentProtocolCatalog.ProtocolIdentity.ResourceProtocolVersion,
            identity.GetProperty("resourceProtocolVersion").GetInt32());
        Assert.Equal(
            AgentProtocolCatalog.ProtocolIdentity.RequiredFeatures,
            identity.GetProperty("requiredFeatures").GetUInt64());
        Assert.Equal(
            AgentProtocolCatalog.ProtocolIdentity.OptionalFeatures,
            identity.GetProperty("optionalFeatures").GetUInt64());
    }
}
