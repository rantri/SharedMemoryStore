using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;

namespace SharedMemoryStore.InteropTests;

public sealed class StressInteropTests
{
    private const string StressOptInVariable = "SMS_RUN_INTEROP_STRESS";

    public static TheoryData<string, string> OrderedRuntimePairs => new()
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

    [Theory]
    [MemberData(nameof(OrderedRuntimePairs))]
    public async Task OrderedPairsExchangeTheConfiguredValueCount(
        string producerRuntime,
        string consumerRuntime)
    {
        if (!StressIsEnabled())
        {
            return;
        }

        var valueCount = ReadBoundedCount("SMS_INTEROP_STRESS_VALUES", 1_000, 100_000);
        var producerDefinition = AgentDefinition.Resolve(producerRuntime);
        var consumerDefinition = AgentDefinition.Resolve(consumerRuntime);
        if (!AgentsAreAvailable(producerDefinition, consumerDefinition))
        {
            return;
        }

        await using var producer = await AgentProcess.StartAsync(producerDefinition);
        await using var consumer = await AgentProcess.StartAsync(consumerDefinition);
        var name = $"sms-stress-values-{producerRuntime}-{consumerRuntime}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var options = new
        {
            name,
            slotCount = 4,
            maxValueBytes = 64,
            maxDescriptorBytes = 16,
            maxKeyBytes = 16,
            leaseRecordCount = 16,
            enableLeaseRecovery = true
        };

        AssertSuccess(await producer.SendAsync("open", new
        {
            storeId = "producer",
            options.name,
            openMode = 0,
            options.slotCount,
            options.maxValueBytes,
            options.maxDescriptorBytes,
            options.maxKeyBytes,
            options.leaseRecordCount,
            options.enableLeaseRecovery
        }));
        AssertSuccess(await consumer.SendAsync("open", new
        {
            storeId = "consumer",
            options.name,
            openMode = 1,
            options.slotCount,
            options.maxValueBytes,
            options.maxDescriptorBytes,
            options.maxKeyBytes,
            options.leaseRecordCount,
            options.enableLeaseRecovery
        }));

        var seed = StableSeed(producerRuntime, consumerRuntime);
        for (var iteration = 0; iteration < valueCount; iteration++)
        {
            var key = IterationBytes(0x4b, iteration, seed, 9);
            var value = IterationBytes(0x56, iteration, seed, 37);
            var descriptor = IterationBytes(0x44, iteration, seed, 7);
            AssertSuccess(await producer.SendAsync("publish", new
            {
                storeId = "producer",
                key = AgentProtocol.EncodeBytes(key),
                value = AgentProtocol.EncodeBytes(value),
                descriptor = AgentProtocol.EncodeBytes(descriptor)
            }));

            var acquired = await consumer.SendAsync("acquire", new
            {
                storeId = "consumer",
                leaseId = "stress-lease",
                key = AgentProtocol.EncodeBytes(key)
            });
            AssertSuccess(acquired);
            Assert.Equal(value, Decode(acquired, "value"));
            Assert.Equal(descriptor, Decode(acquired, "descriptor"));
            AssertSuccess(await consumer.SendAsync("release", new { leaseId = "stress-lease" }));
            AssertSuccess(await consumer.SendAsync("remove", new
            {
                storeId = "consumer",
                key = AgentProtocol.EncodeBytes(key)
            }));
        }

        AssertSuccess(await consumer.SendAsync("close", new { storeId = "consumer" }));
        AssertSuccess(await producer.SendAsync("close", new { storeId = "producer" }));
    }

    [Fact]
    public async Task AllRuntimesCompleteConfiguredMixedLifecycleCycles()
    {
        if (!StressIsEnabled())
        {
            return;
        }

        var cycleCount = ReadBoundedCount("SMS_INTEROP_STRESS_LIFECYCLE_CYCLES", 10_000, 100_000);
        var definitions = new[]
        {
            AgentDefinition.Resolve("dotnet"),
            AgentDefinition.Resolve("cpp"),
            AgentDefinition.Resolve("python")
        };
        if (!AgentsAreAvailable(definitions))
        {
            return;
        }

        await using var dotnet = await AgentProcess.StartAsync(definitions[0]);
        await using var cpp = await AgentProcess.StartAsync(definitions[1]);
        await using var python = await AgentProcess.StartAsync(definitions[2]);
        var participants = new[]
        {
            new Participant("dotnet-store", dotnet),
            new Participant("cpp-store", cpp),
            new Participant("python-store", python)
        };
        var name = $"sms-stress-lifecycle-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var options = new
        {
            name,
            slotCount = 4,
            maxValueBytes = 32,
            maxDescriptorBytes = 8,
            maxKeyBytes = 8,
            leaseRecordCount = 16,
            enableLeaseRecovery = true
        };

        for (var index = 0; index < participants.Length; index++)
        {
            var participant = participants[index];
            AssertSuccess(await participant.Agent.SendAsync("open", new
            {
                storeId = participant.StoreId,
                options.name,
                openMode = index == 0 ? 0 : 1,
                options.slotCount,
                options.maxValueBytes,
                options.maxDescriptorBytes,
                options.maxKeyBytes,
                options.leaseRecordCount,
                options.enableLeaseRecovery
            }));
        }

        for (var cycle = 0; cycle < cycleCount; cycle++)
        {
            var publisher = participants[cycle % participants.Length];
            var reader = participants[(cycle + 1) % participants.Length];
            var reservationOwner = participants[(cycle + 2) % participants.Length];
            var leaseKey = IterationKey(0x4c, cycle);
            var reservationKey = IterationKey(0x52, cycle);
            var value = IterationBytes(0x50, cycle, 0x13579bdfu, 12);
            var descriptor = IterationBytes(0x44, cycle, 0x2468ace0u, 3);

            AssertSuccess(await publisher.Agent.SendAsync("publish", new
            {
                storeId = publisher.StoreId,
                key = AgentProtocol.EncodeBytes(leaseKey),
                value = AgentProtocol.EncodeBytes(value),
                descriptor = AgentProtocol.EncodeBytes(descriptor)
            }));
            AssertSuccess(await reader.Agent.SendAsync("acquire", new
            {
                storeId = reader.StoreId,
                leaseId = "stress-lifecycle-lease",
                key = AgentProtocol.EncodeBytes(leaseKey)
            }));
            AssertStatus(await publisher.Agent.SendAsync("remove", new
            {
                storeId = publisher.StoreId,
                key = AgentProtocol.EncodeBytes(leaseKey)
            }), 10, "RemovePending");
            AssertSuccess(await reader.Agent.SendAsync("release", new { leaseId = "stress-lifecycle-lease" }));

            AssertSuccess(await reservationOwner.Agent.SendAsync("reserve", new
            {
                storeId = reservationOwner.StoreId,
                reservationId = "stress-reservation",
                key = AgentProtocol.EncodeBytes(reservationKey),
                payloadLength = value.Length,
                descriptor = AgentProtocol.EncodeBytes(descriptor)
            }));
            AssertSuccess(await reservationOwner.Agent.SendAsync("reservationWrite", new
            {
                reservationId = "stress-reservation",
                data = AgentProtocol.EncodeBytes(value)
            }));
            AssertSuccess(await reservationOwner.Agent.SendAsync("advance", new
            {
                reservationId = "stress-reservation",
                byteCount = value.Length
            }));

            if ((cycle & 1) == 0)
            {
                AssertSuccess(await reservationOwner.Agent.SendAsync("abort", new
                {
                    reservationId = "stress-reservation"
                }));
            }
            else
            {
                AssertSuccess(await reservationOwner.Agent.SendAsync("commit", new
                {
                    reservationId = "stress-reservation"
                }));
                var committed = await publisher.Agent.SendAsync("acquire", new
                {
                    storeId = publisher.StoreId,
                    leaseId = "stress-reservation-lease",
                    key = AgentProtocol.EncodeBytes(reservationKey)
                });
                AssertSuccess(committed);
                Assert.Equal(value, Decode(committed, "value"));
                Assert.Equal(descriptor, Decode(committed, "descriptor"));
                AssertSuccess(await publisher.Agent.SendAsync("release", new
                {
                    leaseId = "stress-reservation-lease"
                }));
                AssertSuccess(await publisher.Agent.SendAsync("remove", new
                {
                    storeId = publisher.StoreId,
                    key = AgentProtocol.EncodeBytes(reservationKey)
                }));
            }
        }

        for (var index = participants.Length - 1; index >= 0; index--)
        {
            var participant = participants[index];
            AssertSuccess(await participant.Agent.SendAsync("close", new { storeId = participant.StoreId }));
        }
    }

    private static bool StressIsEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable(StressOptInVariable), "1", StringComparison.Ordinal);

    private static int ReadBoundedCount(string variable, int defaultValue, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 1 || parsed > maximum)
        {
            throw new InvalidOperationException($"{variable} must be between 1 and {maximum}.");
        }

        return parsed;
    }

    private static bool AgentsAreAvailable(params AgentDefinition[] definitions) =>
        definitions.All(definition => definition.IsAvailable() && PythonNativeLibraryIsAvailable(definition));

    private static bool PythonNativeLibraryIsAvailable(AgentDefinition definition)
    {
        if (definition.Runtime != "python")
        {
            return true;
        }

        var agentScript = definition.Arguments.FirstOrDefault();
        var testsDirectory = agentScript is null ? null : Directory.GetParent(Path.GetDirectoryName(agentScript)!);
        var repository = testsDirectory?.Parent;
        if (repository is null)
        {
            return false;
        }

        var libraryName = OperatingSystem.IsWindows() ? "shared_memory_store.dll" : "libshared_memory_store.so";
        return File.Exists(Path.Combine(
            repository.FullName,
            "src",
            "python",
            "shared_memory_store",
            libraryName));
    }

    private static uint StableSeed(string first, string second)
    {
        var seed = 2166136261u;
        foreach (var current in $"{first}>{second}")
        {
            seed = unchecked((seed ^ current) * 16777619u);
        }

        return seed;
    }

    private static byte[] IterationKey(byte prefix, int iteration)
    {
        return
        [
            prefix,
            (byte)iteration,
            (byte)(iteration >> 8),
            (byte)(iteration >> 16),
            (byte)(iteration >> 24)
        ];
    }

    private static byte[] IterationBytes(byte prefix, int iteration, uint seed, int length)
    {
        var bytes = new byte[length];
        var state = unchecked(seed ^ ((uint)iteration * 0x9e3779b9u) ^ prefix);
        for (var index = 0; index < bytes.Length; index++)
        {
            state = unchecked((state * 1664525u) + 1013904223u);
            bytes[index] = (byte)(state >> 24);
        }

        bytes[0] = prefix;
        if (bytes.Length > 1)
        {
            bytes[1] = 0;
        }

        return bytes;
    }

    private static void AssertSuccess(AgentResponse response) => AssertStatus(response, 0, "Success");

    private static void AssertStatus(AgentResponse response, int code, string name)
    {
        Assert.True(response.Ok, response.Error?.Message);
        Assert.Equal(code, response.Status.Code);
        Assert.Equal(name, response.Status.Name);
    }

    private static byte[] Decode(AgentResponse response, string property) =>
        AgentProtocol.DecodeBytes(response.Result!.Value.GetProperty(property).GetString()!);

    private sealed record Participant(string StoreId, AgentProcess Agent);
}
