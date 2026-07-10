using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;

namespace SharedMemoryStore.InteropTests;

public sealed class MixedLifecycleTests
{
    [Theory]
    [MemberData(
        nameof(CoreExchangeMatrixTests.OrderedRuntimePairs),
        MemberType = typeof(CoreExchangeMatrixTests))]
    public async Task ConcurrentForeignPublishHasExactlyOneWinner(
        string firstRuntime,
        string secondRuntime)
    {
        var firstDefinition = AgentDefinition.Resolve(firstRuntime);
        var secondDefinition = AgentDefinition.Resolve(secondRuntime);
        if (!firstDefinition.IsAvailable() || !secondDefinition.IsAvailable())
        {
            return;
        }

        await using var first = await AgentProcess.StartAsync(firstDefinition);
        await using var second = await AgentProcess.StartAsync(secondDefinition);
        var name = $"sms-publish-race-{firstRuntime}-{secondRuntime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await first.SendAsync(
            "open",
            InteropAssertions.OpenArguments("first", name, openMode: 0)));
        InteropAssertions.Success(await second.SendAsync(
            "open",
            InteropAssertions.OpenArguments("second", name, openMode: 1)));
        var key = AgentProtocol.EncodeBytes(new byte[] { 8, 0, 8 });
        var firstPublish = first.SendAsync("publish", new
        {
            storeId = "first",
            key,
            value = AgentProtocol.EncodeBytes(new byte[] { 1 }),
            descriptor = string.Empty
        });
        var secondPublish = second.SendAsync("publish", new
        {
            storeId = "second",
            key,
            value = AgentProtocol.EncodeBytes(new byte[] { 2 }),
            descriptor = string.Empty
        });
        var responses = await Task.WhenAll(firstPublish, secondPublish);
        Assert.Single(responses, response => response.Status.Name == "Success" && response.Status.Code == 0);
        Assert.Single(responses, response => response.Status.Name == "DuplicateKey" && response.Status.Code == 1);
        Assert.All(responses, response => Assert.True(response.Ok, response.Error?.Message));

        InteropAssertions.Success(await first.SendAsync("remove", new { storeId = "first", key }));
        InteropAssertions.Success(await second.SendAsync("close", new { storeId = "second" }));
        InteropAssertions.Success(await first.SendAsync("close", new { storeId = "first" }));
    }

    [Theory]
    [MemberData(
        nameof(CoreExchangeMatrixTests.OrderedRuntimePairs),
        MemberType = typeof(CoreExchangeMatrixTests))]
    public async Task ForeignLeaseReservationAndSegmentsShareOneLifecycle(
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
        var name = $"sms-lifecycle-{producerRuntime}-{consumerRuntime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await producer.SendAsync(
            "open",
            InteropAssertions.OpenArguments("producer", name, openMode: 0)));
        InteropAssertions.Success(await consumer.SendAsync(
            "open",
            InteropAssertions.OpenArguments("consumer", name, openMode: 1)));

        var leasedKey = new byte[] { 1, 0, 1 };
        InteropAssertions.Success(await producer.SendAsync("publish", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(leasedKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 9, 0, 8 }),
            descriptor = AgentProtocol.EncodeBytes(new byte[] { 7 })
        }));
        InteropAssertions.Success(await producer.SendAsync("acquire", new
        {
            storeId = "producer",
            leaseId = "foreign-lease",
            key = AgentProtocol.EncodeBytes(leasedKey)
        }));
        InteropAssertions.Status(await consumer.SendAsync("remove", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(leasedKey)
        }), 10, "RemovePending");
        InteropAssertions.Success(await producer.SendAsync("release", new { leaseId = "foreign-lease" }));
        InteropAssertions.Status(
            await producer.SendAsync("release", new { leaseId = "foreign-lease" }),
            9,
            "LeaseAlreadyReleased");
        InteropAssertions.Success(await consumer.SendAsync("publish", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(leasedKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 4, 3, 0, 2 }),
            descriptor = string.Empty
        }));

        var committedKey = new byte[] { 2, 0, 2 };
        var committedValue = new byte[] { 5, 0, 4, 0, 3 };
        InteropAssertions.Success(await producer.SendAsync("reserve", new
        {
            storeId = "producer",
            reservationId = "commit-reservation",
            key = AgentProtocol.EncodeBytes(committedKey),
            payloadLength = committedValue.Length,
            descriptor = AgentProtocol.EncodeBytes(new byte[] { 6, 0 })
        }));
        InteropAssertions.Status(await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "invisible-lease",
            key = AgentProtocol.EncodeBytes(committedKey)
        }), 2, "NotFound");
        InteropAssertions.Status(await consumer.SendAsync("publish", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(committedKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 1 }),
            descriptor = string.Empty
        }), 1, "DuplicateKey");
        InteropAssertions.Success(await producer.SendAsync("reservationWrite", new
        {
            reservationId = "commit-reservation",
            data = AgentProtocol.EncodeBytes(committedValue)
        }));
        InteropAssertions.Success(await producer.SendAsync(
            "advance",
            new { reservationId = "commit-reservation", byteCount = committedValue.Length }));
        InteropAssertions.Success(await producer.SendAsync(
            "commit",
            new { reservationId = "commit-reservation" }));
        var committed = await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "committed-lease",
            key = AgentProtocol.EncodeBytes(committedKey)
        });
        InteropAssertions.Success(committed);
        Assert.Equal(committedValue, InteropAssertions.Decode(committed, "value"));
        Assert.Equal(new byte[] { 6, 0 }, InteropAssertions.Decode(committed, "descriptor"));
        InteropAssertions.Success(await consumer.SendAsync("release", new { leaseId = "committed-lease" }));

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
        InteropAssertions.Success(await consumer.SendAsync("publish", new
        {
            storeId = "consumer",
            key = AgentProtocol.EncodeBytes(abortedKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 1, 2 }),
            descriptor = string.Empty
        }));

        var segmentedKey = new byte[] { 4, 0, 4 };
        var segmented = await producer.SendAsync("publishSegments", new
        {
            storeId = "producer",
            key = AgentProtocol.EncodeBytes(segmentedKey),
            segments = new[]
            {
                AgentProtocol.EncodeBytes(new byte[] { 1, 0 }),
                AgentProtocol.EncodeBytes(Array.Empty<byte>()),
                AgentProtocol.EncodeBytes(new byte[] { 2, 3 })
            },
            descriptor = AgentProtocol.EncodeBytes(new byte[] { 9 })
        });
        InteropAssertions.Success(segmented);
        Assert.Equal(4, segmented.Result!.Value.GetProperty("copiedBytes").GetInt64());
        var segmentedLease = await consumer.SendAsync("acquire", new
        {
            storeId = "consumer",
            leaseId = "segmented-lease",
            key = AgentProtocol.EncodeBytes(segmentedKey)
        });
        InteropAssertions.Success(segmentedLease);
        Assert.Equal(new byte[] { 1, 0, 2, 3 }, InteropAssertions.Decode(segmentedLease, "value"));
        InteropAssertions.Success(await consumer.SendAsync("release", new { leaseId = "segmented-lease" }));

        InteropAssertions.Success(await consumer.SendAsync("close", new { storeId = "consumer" }));
        InteropAssertions.Success(await producer.SendAsync("close", new { storeId = "producer" }));
    }
}
