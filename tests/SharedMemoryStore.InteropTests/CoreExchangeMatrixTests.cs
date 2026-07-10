using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;

namespace SharedMemoryStore.InteropTests;

public sealed class CoreExchangeMatrixTests
{
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
    public async Task ProducerAndConsumerExchangeExactBytesBothWays(string producerRuntime, string consumerRuntime)
    {
        var producerDefinition = AgentDefinition.Resolve(producerRuntime);
        var consumerDefinition = AgentDefinition.Resolve(consumerRuntime);
        if (!producerDefinition.IsAvailable() || !consumerDefinition.IsAvailable())
        {
            return;
        }

        await using var producer = await AgentProcess.StartAsync(producerDefinition);
        await using var consumer = await AgentProcess.StartAsync(consumerDefinition);
        var name = $"sms-3x3-{producerRuntime}-{consumerRuntime}-{Guid.NewGuid():N}";
        var common = new
        {
            name,
            slotCount = 3,
            maxValueBytes = 64,
            maxDescriptorBytes = 16,
            maxKeyBytes = 16,
            leaseRecordCount = 8,
            enableLeaseRecovery = true
        };
        var producerOpen = await producer.SendAsync("open", new
        {
            storeId = "producer",
            common.name,
            openMode = 0,
            common.slotCount,
            common.maxValueBytes,
            common.maxDescriptorBytes,
            common.maxKeyBytes,
            common.leaseRecordCount,
            common.enableLeaseRecovery
        });
        AssertSuccess(producerOpen);
        var consumerOpen = await consumer.SendAsync("open", new
        {
            storeId = "consumer",
            common.name,
            openMode = 1,
            common.slotCount,
            common.maxValueBytes,
            common.maxDescriptorBytes,
            common.maxKeyBytes,
            common.leaseRecordCount,
            common.enableLeaseRecovery
        });
        AssertSuccess(consumerOpen);

        var key = new byte[] { 0, 1, 0xff };
        var firstValue = new byte[] { 9, 0, 8, 0xff };
        var firstDescriptor = new byte[] { 4, 0 };
        AssertSuccess(await producer.SendAsync("publish", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(key),
            value = AgentProtocol.EncodeBytes(firstValue),
            descriptor = AgentProtocol.EncodeBytes(firstDescriptor)
        }));
        var acquired = await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "consumer-lease",
            key = AgentProtocol.EncodeBytes(key)
        });
        AssertSuccess(acquired);
        Assert.Equal(firstValue, Decode(acquired, "value"));
        Assert.Equal(firstDescriptor, Decode(acquired, "descriptor"));
        AssertSuccess(await consumer.SendAsync("release", new { leaseId = "consumer-lease" }));
        AssertSuccess(await consumer.SendAsync("remove", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(key)
        }));

        var secondValue = new byte[] { 7, 6, 0, 5 };
        AssertSuccess(await consumer.SendAsync("publish", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(key),
            value = AgentProtocol.EncodeBytes(secondValue),
            descriptor = string.Empty
        }));
        var reverse = await producer.SendAsync("acquire", new
        {
            storeId = "producer",
            leaseId = "producer-lease",
            key = AgentProtocol.EncodeBytes(key)
        });
        AssertSuccess(reverse);
        Assert.Equal(secondValue, Decode(reverse, "value"));
        AssertSuccess(await producer.SendAsync("release", new { leaseId = "producer-lease" }));
        AssertSuccess(await consumer.SendAsync("close", new { storeId = "consumer" }));
        AssertSuccess(await producer.SendAsync("close", new { storeId = "producer" }));
    }

    private static void AssertSuccess(AgentResponse response)
    {
        Assert.True(response.Ok, response.Error?.Message);
        Assert.Equal(0, response.Status.Code);
        Assert.Equal("Success", response.Status.Name);
    }

    private static byte[] Decode(AgentResponse response, string property) =>
        AgentProtocol.DecodeBytes(response.Result!.Value.GetProperty(property).GetString()!);
}
