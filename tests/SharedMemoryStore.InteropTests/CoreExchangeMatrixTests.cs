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
    public async Task ProducerAndConsumerCompleteTheFullBinaryLifecycle(
        string producerRuntime,
        string consumerRuntime)
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
        InteropAssertions.Success(await producer.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "producer",
                name,
                openMode: 0,
                slotCount: 8,
                maxValueBytes: 64,
                maxDescriptorBytes: 16,
                maxKeyBytes: 16,
                leaseRecordCount: 8,
                participantRecordCount: 4)));
        InteropAssertions.Success(await consumer.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "consumer",
                name,
                openMode: 1,
                slotCount: 8,
                maxValueBytes: 64,
                maxDescriptorBytes: 16,
                maxKeyBytes: 16,
                leaseRecordCount: 8,
                participantRecordCount: 4)));

        // Contiguous publication preserves arbitrary binary key, value, and descriptor bytes.
        var key = new byte[] { 0, 1, 0xff };
        var firstValue = new byte[] { 9, 0, 8, 0xff };
        var firstDescriptor = new byte[] { 4, 0 };
        InteropAssertions.Success(await producer.SendAsync("publish", new
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
        InteropAssertions.Success(acquired);
        Assert.Equal(firstValue, InteropAssertions.Decode(acquired, "value"));
        Assert.Equal(firstDescriptor, InteropAssertions.Decode(acquired, "descriptor"));
        InteropAssertions.Success(await consumer.SendAsync("release", new { leaseId = "consumer-lease" }));
        InteropAssertions.Success(await consumer.SendAsync("remove", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(key)
        }));

        var secondValue = new byte[] { 7, 6, 0, 5 };
        InteropAssertions.Success(await consumer.SendAsync("publish", new
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
        InteropAssertions.Success(reverse);
        Assert.Equal(secondValue, InteropAssertions.Decode(reverse, "value"));
        InteropAssertions.Success(await producer.SendAsync("release", new { leaseId = "producer-lease" }));
        InteropAssertions.Success(await producer.SendAsync("remove", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(key)
        }));

        // Segments, including an empty segment, are published as one exact logical value.
        var segmentedKey = new byte[] { 1, 0, 1 };
        var segmentedValue = new byte[] { 1, 0, 2, 0xff };
        var segmented = await producer.SendAsync("publishSegments", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(segmentedKey),
            segments = new[]
            {
                AgentProtocol.EncodeBytes(new byte[] { 1, 0 }),
                AgentProtocol.EncodeBytes(Array.Empty<byte>()),
                AgentProtocol.EncodeBytes(new byte[] { 2, 0xff })
            },
            descriptor = AgentProtocol.EncodeBytes(new byte[] { 0xfe, 0 })
        });
        InteropAssertions.Success(segmented);
        Assert.Equal(segmentedValue.Length, segmented.Result!.Value.GetProperty("copiedBytes").GetInt64());
        var segmentedLease = await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "segmented-lease",
            key = AgentProtocol.EncodeBytes(segmentedKey)
        });
        InteropAssertions.Success(segmentedLease);
        Assert.Equal(segmentedValue, InteropAssertions.Decode(segmentedLease, "value"));
        Assert.Equal(new byte[] { 0xfe, 0 }, InteropAssertions.Decode(segmentedLease, "descriptor"));
        InteropAssertions.Success(await consumer.SendAsync("release", new { leaseId = "segmented-lease" }));
        InteropAssertions.Success(await consumer.SendAsync("remove", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(segmentedKey)
        }));

        // A partially advanced reservation remains invisible until its exact commit.
        var committedKey = new byte[] { 2, 0, 2 };
        var committedValue = new byte[] { 5, 0, 4, 0, 3, 0xff };
        InteropAssertions.Success(await producer.SendAsync("reserve", new
        {
            storeId = "producer",
            reservationId = "commit-reservation",
            key = AgentProtocol.EncodeBytes(committedKey),
            payloadLength = committedValue.Length,
            descriptor = AgentProtocol.EncodeBytes(new byte[] { 6, 0 })
        }));
        InteropAssertions.Success(await producer.SendAsync("reservationWrite", new
        {
            reservationId = "commit-reservation",
            data = AgentProtocol.EncodeBytes(committedValue.AsSpan(0, 3))
        }));
        InteropAssertions.Success(await producer.SendAsync(
            "advance",
            new { reservationId = "commit-reservation", byteCount = 3 }));
        InteropAssertions.Status(await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "premature-lease",
            key = AgentProtocol.EncodeBytes(committedKey)
        }), 2, "NotFound");
        InteropAssertions.Success(await producer.SendAsync("reservationWrite", new
        {
            reservationId = "commit-reservation",
            data = AgentProtocol.EncodeBytes(committedValue.AsSpan(3))
        }));
        InteropAssertions.Success(await producer.SendAsync(
            "advance",
            new { reservationId = "commit-reservation", byteCount = committedValue.Length - 3 }));
        InteropAssertions.Success(await producer.SendAsync(
            "commit",
            new { reservationId = "commit-reservation" }));
        var committedLease = await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "committed-lease",
            key = AgentProtocol.EncodeBytes(committedKey)
        });
        InteropAssertions.Success(committedLease);
        Assert.Equal(committedValue, InteropAssertions.Decode(committedLease, "value"));
        Assert.Equal(new byte[] { 6, 0 }, InteropAssertions.Decode(committedLease, "descriptor"));
        InteropAssertions.Success(await consumer.SendAsync("release", new { leaseId = "committed-lease" }));
        InteropAssertions.Success(await consumer.SendAsync("remove", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(committedKey)
        }));

        // Abort makes the exact key reusable and never exposes the strict prefix.
        var abortedKey = new byte[] { 3, 0, 3 };
        InteropAssertions.Success(await producer.SendAsync("reserve", new
        {
            storeId = "producer",
            reservationId = "abort-reservation",
            key = AgentProtocol.EncodeBytes(abortedKey),
            payloadLength = 4,
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await producer.SendAsync("reservationWrite", new
        {
            reservationId = "abort-reservation",
            data = AgentProtocol.EncodeBytes(new byte[] { 8, 7 })
        }));
        InteropAssertions.Success(await producer.SendAsync(
            "advance",
            new { reservationId = "abort-reservation", byteCount = 2 }));
        InteropAssertions.Success(await producer.SendAsync(
            "abort",
            new { reservationId = "abort-reservation" }));
        InteropAssertions.Status(await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "aborted-lease",
            key = AgentProtocol.EncodeBytes(abortedKey)
        }), 2, "NotFound");
        InteropAssertions.Success(await consumer.SendAsync("publish", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(abortedKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 1, 2, 0xff }),
            descriptor = string.Empty
        }));
        var replacement = await producer.SendAsync("acquire", new
        {
            storeId = "producer",
            leaseId = "abort-replacement-lease",
            key = AgentProtocol.EncodeBytes(abortedKey)
        });
        InteropAssertions.Success(replacement);
        Assert.Equal(new byte[] { 1, 2, 0xff }, InteropAssertions.Decode(replacement, "value"));
        InteropAssertions.Success(await producer.SendAsync("release", new { leaseId = "abort-replacement-lease" }));
        InteropAssertions.Success(await producer.SendAsync("remove", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(abortedKey)
        }));

        // Both processes retain the removed generation until the exact final release.
        var lifetimeKey = new byte[] { 4, 0, 4 };
        var lifetimeValue = new byte[] { 0xff, 0, 0x80, 0x7f };
        var lifetimeDescriptor = new byte[] { 0, 0xfe };
        InteropAssertions.Success(await producer.SendAsync("publish", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(lifetimeKey),
            value = AgentProtocol.EncodeBytes(lifetimeValue),
            descriptor = AgentProtocol.EncodeBytes(lifetimeDescriptor)
        }));
        foreach (var (agent, storeId, leaseId) in new[]
                 {
                     (producer, "producer", "producer-held-lease"),
                     (consumer, "consumer", "consumer-held-lease")
                 })
        {
            var held = await agent.SendAsync("acquire", new
            {
                storeId,
                leaseId,
                key = AgentProtocol.EncodeBytes(lifetimeKey)
            });
            InteropAssertions.Success(held);
            Assert.Equal(lifetimeValue, InteropAssertions.Decode(held, "value"));
            Assert.Equal(lifetimeDescriptor, InteropAssertions.Decode(held, "descriptor"));
        }

        InteropAssertions.Status(await consumer.SendAsync("remove", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(lifetimeKey)
        }), 10, "RemovePending");
        foreach (var (agent, leaseId) in new[]
                 {
                     (producer, "producer-held-lease"),
                     (consumer, "consumer-held-lease")
                 })
        {
            var retained = await agent.SendAsync("read", new { leaseId });
            InteropAssertions.Success(retained);
            Assert.Equal(lifetimeValue, InteropAssertions.Decode(retained, "value"));
            Assert.Equal(lifetimeDescriptor, InteropAssertions.Decode(retained, "descriptor"));
        }

        InteropAssertions.Success(await producer.SendAsync("release", new { leaseId = "producer-held-lease" }));
        InteropAssertions.Status(await producer.SendAsync("publish", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(lifetimeKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 1 }),
            descriptor = string.Empty
        }), 1, "DuplicateKey");
        var lastRetained = await consumer.SendAsync("read", new { leaseId = "consumer-held-lease" });
        InteropAssertions.Success(lastRetained);
        Assert.Equal(lifetimeValue, InteropAssertions.Decode(lastRetained, "value"));
        InteropAssertions.Success(await consumer.SendAsync("release", new { leaseId = "consumer-held-lease" }));

        var republishedValue = new byte[] { 0, 9, 0xff, 8 };
        InteropAssertions.Success(await consumer.SendAsync("publish", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(lifetimeKey),
            value = AgentProtocol.EncodeBytes(republishedValue),
            descriptor = string.Empty
        }));
        var republished = await producer.SendAsync("acquire", new
        {
            storeId = "producer",
            leaseId = "republished-lease",
            key = AgentProtocol.EncodeBytes(lifetimeKey)
        });
        InteropAssertions.Success(republished);
        Assert.Equal(republishedValue, InteropAssertions.Decode(republished, "value"));
        InteropAssertions.Success(await producer.SendAsync("release", new { leaseId = "republished-lease" }));

        InteropAssertions.Success(await consumer.SendAsync("close", new { storeId = "consumer" }));
        InteropAssertions.Success(await producer.SendAsync("close", new { storeId = "producer" }));
    }
}
