using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;

namespace SharedMemoryStore.InteropTests;

public sealed class MixedLifecycleTests
{
    private const int CollisionSlotCount = 20;

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

    [Fact]
    public async Task ThreeRuntimesRaceOneExactKeyWithOneWinner()
    {
        AgentDefinition[] definitions = ResolveTriad();
        if (definitions.Any(definition => !definition.IsAvailable()))
        {
            return;
        }

        List<AgentProcess> agents = await StartAgentsAsync(definitions);
        try
        {
            string name = $"sms-triad-publish-race-{Guid.NewGuid():N}";
            for (var index = 0; index < agents.Count; index++)
            {
                InteropAssertions.Success(await agents[index].SendAsync(
                    "open",
                    InteropAssertions.OpenArguments(
                        InteropAssertions.Runtimes[index],
                        name,
                        openMode: index == 0 ? 0 : 1,
                        slotCount: 4,
                        participantRecordCount: 4)));
            }

            string key = AgentProtocol.EncodeBytes(new byte[] { 0xff, 0, 0x80, 0 });
            Task<AgentResponse>[] publishes = agents.Select((agent, index) => agent.SendAsync("publish", new
            {
                storeId = InteropAssertions.Runtimes[index],
                key,
                value = AgentProtocol.EncodeBytes(new byte[] { (byte)(index + 1), 0, 0xff }),
                descriptor = string.Empty
            })).ToArray();
            AgentResponse[] responses = await Task.WhenAll(publishes);

            Assert.All(responses, response => Assert.True(response.Ok, response.Error?.Message));
            Assert.Single(responses, response => response.Status is { Code: 0, Name: "Success" });
            Assert.Equal(2, responses.Count(response => response.Status is { Code: 1, Name: "DuplicateKey" }));
            InteropAssertions.Success(await agents[1].SendAsync("remove", new
            {
                storeId = InteropAssertions.Runtimes[1],
                key
            }));

            await CloseTriadAsync(agents);
        }
        finally
        {
            await DisposeAgentsAsync(agents);
        }
    }

    [Fact]
    public async Task ThreeRuntimesChurnKeysThatShareOnePrimaryPairAndOverflow()
    {
        AgentDefinition[] definitions = ResolveTriad();
        if (definitions.Any(definition => !definition.IsAvailable()))
        {
            return;
        }

        List<AgentProcess> agents = await StartAgentsAsync(definitions);
        try
        {
            string name = $"sms-triad-collision-churn-{Guid.NewGuid():N}";
            for (var index = 0; index < agents.Count; index++)
            {
                InteropAssertions.Success(await agents[index].SendAsync(
                    "open",
                    InteropAssertions.OpenArguments(
                        InteropAssertions.Runtimes[index],
                        name,
                        openMode: index == 0 ? 0 : 1,
                        slotCount: CollisionSlotCount,
                        maxValueBytes: 32,
                        maxDescriptorBytes: 8,
                        maxKeyBytes: 16,
                        leaseRecordCount: 24,
                        participantRecordCount: 4)));
            }

            byte[][] keys = GenerateBucketPairCollisions(count: 18, CollisionSlotCount);
            var expectedValues = new byte[keys.Length][];
            for (var index = 0; index < keys.Length; index++)
            {
                expectedValues[index] = [(byte)index, 0, 0xff];
                InteropAssertions.Success(await agents[index % agents.Count].SendAsync("publish", new
                {
                    storeId = InteropAssertions.Runtimes[index % agents.Count],
                    key = AgentProtocol.EncodeBytes(keys[index]),
                    value = AgentProtocol.EncodeBytes(expectedValues[index]),
                    descriptor = AgentProtocol.EncodeBytes(new byte[] { 0xfe, (byte)index })
                }));
            }

            for (var index = 0; index < keys.Length; index++)
            {
                int readerIndex = (index + 1) % agents.Count;
                string leaseId = $"collision-read-{index}";
                AgentResponse acquired = await agents[readerIndex].SendAsync("acquire", new
                {
                    storeId = InteropAssertions.Runtimes[readerIndex],
                    leaseId,
                    key = AgentProtocol.EncodeBytes(keys[index])
                });
                InteropAssertions.Success(acquired);
                Assert.Equal(expectedValues[index], InteropAssertions.Decode(acquired, "value"));
                InteropAssertions.Success(await agents[readerIndex].SendAsync("release", new { leaseId }));
            }

            AgentResponse spilled = await agents[2].SendAsync(
                "diagnostics",
                new { storeId = InteropAssertions.Runtimes[2] });
            InteropAssertions.Success(spilled);
            Assert.True(
                spilled.Result!.Value.GetProperty("overflowDirectoryOccupancy").GetInt32() > 0,
                "The collision corpus did not force a live overflow-directory entry.");

            for (var cycle = 0; cycle < 3; cycle++)
            {
                for (var index = 12; index < keys.Length; index++)
                {
                    int removerIndex = (index + cycle + 1) % agents.Count;
                    int publisherIndex = (index + cycle + 2) % agents.Count;
                    int readerIndex = (index + cycle) % agents.Count;
                    InteropAssertions.Success(await agents[removerIndex].SendAsync("remove", new
                    {
                        storeId = InteropAssertions.Runtimes[removerIndex],
                        key = AgentProtocol.EncodeBytes(keys[index])
                    }));

                    expectedValues[index] = [(byte)index, (byte)(cycle + 1), 0xff];
                    InteropAssertions.Success(await agents[publisherIndex].SendAsync("publish", new
                    {
                        storeId = InteropAssertions.Runtimes[publisherIndex],
                        key = AgentProtocol.EncodeBytes(keys[index]),
                        value = AgentProtocol.EncodeBytes(expectedValues[index]),
                        descriptor = string.Empty
                    }));

                    string leaseId = $"collision-cycle-{cycle}-{index}";
                    AgentResponse acquired = await agents[readerIndex].SendAsync("acquire", new
                    {
                        storeId = InteropAssertions.Runtimes[readerIndex],
                        leaseId,
                        key = AgentProtocol.EncodeBytes(keys[index])
                    });
                    InteropAssertions.Success(acquired);
                    Assert.Equal(expectedValues[index], InteropAssertions.Decode(acquired, "value"));
                    InteropAssertions.Success(await agents[readerIndex].SendAsync("release", new { leaseId }));
                }
            }

            await CloseTriadAsync(agents);
        }
        finally
        {
            await DisposeAgentsAsync(agents);
        }
    }

    [Fact]
    public async Task ParticipantCapacityExhaustionAndReuseIncludesEveryRuntime()
    {
        AgentDefinition[] triad = ResolveTriad();
        if (triad.Any(definition => !definition.IsAvailable()))
        {
            return;
        }

        AgentDefinition[] definitions = [.. triad, AgentDefinition.Resolve("dotnet")];
        List<AgentProcess> agents = await StartAgentsAsync(definitions);
        try
        {
            string name = $"sms-participant-capacity-{Guid.NewGuid():N}";
            for (var index = 0; index < 3; index++)
            {
                InteropAssertions.Success(await agents[index].SendAsync(
                    "open",
                    InteropAssertions.OpenArguments(
                        InteropAssertions.Runtimes[index],
                        name,
                        openMode: index == 0 ? 0 : 1,
                        slotCount: 4,
                        participantRecordCount: 3)));
            }

            InteropAssertions.Status(await agents[3].SendAsync(
                "open",
                InteropAssertions.OpenArguments(
                    "replacement",
                    name,
                    openMode: 1,
                    slotCount: 4,
                    participantRecordCount: 3)), 11, "ParticipantTableFull");

            InteropAssertions.Success(await agents[1].SendAsync("close", new { storeId = "cpp" }));
            InteropAssertions.Success(await agents[3].SendAsync(
                "open",
                InteropAssertions.OpenArguments(
                    "replacement",
                    name,
                    openMode: 1,
                    slotCount: 4,
                    participantRecordCount: 3)));
            InteropAssertions.Success(await agents[3].SendAsync("publish", new
            {
                storeId = "replacement",
                key = AgentProtocol.EncodeBytes(new byte[] { 1, 0, 1 }),
                value = AgentProtocol.EncodeBytes(new byte[] { 9, 0, 9 }),
                descriptor = string.Empty
            }));

            InteropAssertions.Success(await agents[3].SendAsync("close", new { storeId = "replacement" }));
            InteropAssertions.Success(await agents[2].SendAsync("close", new { storeId = "python" }));
            InteropAssertions.Success(await agents[0].SendAsync("close", new { storeId = "dotnet" }));
        }
        finally
        {
            await DisposeAgentsAsync(agents);
        }
    }

    [Fact]
    public async Task TwelveReadersRetainBytesUntilOneExactFinalReleaseReclaims()
    {
        AgentDefinition[] triad = ResolveTriad();
        if (triad.Any(definition => !definition.IsAvailable()))
        {
            return;
        }

        var definitions = new List<AgentDefinition> { triad[0] };
        for (var index = 0; index < 12; index++)
        {
            definitions.Add(triad[index % triad.Length]);
        }

        List<AgentProcess> agents = await StartAgentsAsync(definitions);
        try
        {
            AgentProcess creator = agents[0];
            string name = $"sms-twelve-readers-{Guid.NewGuid():N}";
            InteropAssertions.Success(await creator.SendAsync(
                "open",
                InteropAssertions.OpenArguments(
                    "creator",
                    name,
                    openMode: 0,
                    slotCount: 2,
                    leaseRecordCount: 16,
                    participantRecordCount: 16)));
            for (var index = 0; index < 12; index++)
            {
                InteropAssertions.Success(await agents[index + 1].SendAsync(
                    "open",
                    InteropAssertions.OpenArguments(
                        $"reader-{index}",
                        name,
                        openMode: 1,
                        slotCount: 2,
                        leaseRecordCount: 16,
                        participantRecordCount: 16)));
            }

            byte[] keyBytes = [7, 0, 7, 0xff];
            byte[] value = [0xff, 0, 1, 0x80, 0];
            byte[] descriptor = [0, 0xfe, 0];
            string key = AgentProtocol.EncodeBytes(keyBytes);
            InteropAssertions.Success(await creator.SendAsync("publish", new
            {
                storeId = "creator",
                key,
                value = AgentProtocol.EncodeBytes(value),
                descriptor = AgentProtocol.EncodeBytes(descriptor)
            }));

            for (var index = 0; index < 12; index++)
            {
                AgentResponse acquired = await agents[index + 1].SendAsync("acquire", new
                {
                    storeId = $"reader-{index}",
                    leaseId = $"lease-{index}",
                    key
                });
                InteropAssertions.Success(acquired);
                Assert.Equal(value, InteropAssertions.Decode(acquired, "value"));
                Assert.Equal(descriptor, InteropAssertions.Decode(acquired, "descriptor"));
            }

            InteropAssertions.Status(await creator.SendAsync(
                "remove",
                new { storeId = "creator", key }), 10, "RemovePending");
            InteropAssertions.Status(await creator.SendAsync("acquire", new
            {
                storeId = "creator",
                leaseId = "new-reader-after-remove",
                key
            }), 2, "NotFound");

            for (var index = 0; index < 12; index++)
            {
                AgentResponse retained = await agents[index + 1].SendAsync(
                    "read",
                    new { leaseId = $"lease-{index}" });
                InteropAssertions.Success(retained);
                Assert.Equal(value, InteropAssertions.Decode(retained, "value"));
                Assert.Equal(descriptor, InteropAssertions.Decode(retained, "descriptor"));
            }

            int[] releaseOrder = [5, 0, 11, 3, 8, 1, 10, 6, 2, 9, 4, 7];
            foreach (int index in releaseOrder[..^1])
            {
                InteropAssertions.Success(await agents[index + 1].SendAsync(
                    "release",
                    new { leaseId = $"lease-{index}" }));
            }

            InteropAssertions.Status(await creator.SendAsync("publish", new
            {
                storeId = "creator",
                key,
                value = AgentProtocol.EncodeBytes(new byte[] { 1 }),
                descriptor = string.Empty
            }), 1, "DuplicateKey");
            int finalIndex = releaseOrder[^1];
            InteropAssertions.Success(await agents[finalIndex + 1].SendAsync(
                "release",
                new { leaseId = $"lease-{finalIndex}" }));

            byte[] replacement = [4, 0, 4, 0xff];
            InteropAssertions.Success(await creator.SendAsync("publish", new
            {
                storeId = "creator",
                key,
                value = AgentProtocol.EncodeBytes(replacement),
                descriptor = string.Empty
            }));
            AgentResponse replacementLease = await agents[1].SendAsync("acquire", new
            {
                storeId = "reader-0",
                leaseId = "replacement-lease",
                key
            });
            InteropAssertions.Success(replacementLease);
            Assert.Equal(replacement, InteropAssertions.Decode(replacementLease, "value"));
            InteropAssertions.Success(await agents[1].SendAsync(
                "release",
                new { leaseId = "replacement-lease" }));

            for (var index = 11; index >= 0; index--)
            {
                InteropAssertions.Success(await agents[index + 1].SendAsync(
                    "close",
                    new { storeId = $"reader-{index}" }));
            }
            InteropAssertions.Success(await creator.SendAsync("close", new { storeId = "creator" }));
        }
        finally
        {
            await DisposeAgentsAsync(agents);
        }
    }

    [Theory]
    [MemberData(
        nameof(CoreExchangeMatrixTests.OrderedRuntimePairs),
        MemberType = typeof(CoreExchangeMatrixTests))]
    public async Task RecreatedMappingRejectsRetainedTokensFromPriorIncarnation(
        string priorRuntime,
        string currentRuntime)
    {
        AgentDefinition priorDefinition = AgentDefinition.Resolve(priorRuntime);
        AgentDefinition currentDefinition = AgentDefinition.Resolve(currentRuntime);
        if (!priorDefinition.IsAvailable() || !currentDefinition.IsAvailable())
        {
            return;
        }

        await using var prior = await AgentProcess.StartAsync(priorDefinition);
        await using var current = await AgentProcess.StartAsync(currentDefinition);
        string name = $"sms-mapping-incarnation-{priorRuntime}-{currentRuntime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await prior.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "prior",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2)));

        string leaseKey = AgentProtocol.EncodeBytes(new byte[] { 1, 0, 1 });
        string reservationKey = AgentProtocol.EncodeBytes(new byte[] { 2, 0, 2 });
        InteropAssertions.Success(await prior.SendAsync("publish", new
        {
            storeId = "prior",
            key = leaseKey,
            value = AgentProtocol.EncodeBytes(new byte[] { 1, 0, 0xff }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await prior.SendAsync("acquire", new
        {
            storeId = "prior",
            leaseId = "prior-lease",
            key = leaseKey
        }));
        InteropAssertions.Success(await prior.SendAsync("release", new { leaseId = "prior-lease" }));
        InteropAssertions.Success(await prior.SendAsync("reserve", new
        {
            storeId = "prior",
            reservationId = "prior-reservation",
            key = reservationKey,
            payloadLength = 3,
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await prior.SendAsync(
            "abort",
            new { reservationId = "prior-reservation" }));
        InteropAssertions.Success(await prior.SendAsync("close", new { storeId = "prior" }));

        InteropAssertions.Success(await current.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "current",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 2,
                participantRecordCount: 2)));
        byte[] currentValue = [9, 0, 9, 0xff];
        InteropAssertions.Success(await current.SendAsync("publish", new
        {
            storeId = "current",
            key = leaseKey,
            value = AgentProtocol.EncodeBytes(currentValue),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await current.SendAsync("acquire", new
        {
            storeId = "current",
            leaseId = "current-lease",
            key = leaseKey
        }));
        InteropAssertions.Success(await current.SendAsync("reserve", new
        {
            storeId = "current",
            reservationId = "current-reservation",
            key = reservationKey,
            payloadLength = 3,
            descriptor = string.Empty
        }));

        AssertTokenFence(
            await prior.SendAsync("release", new { leaseId = "prior-lease" }),
            (8, "InvalidLease"),
            (9, "LeaseAlreadyReleased"),
            (12, "StoreDisposed"));
        AssertTokenFence(
            await prior.SendAsync("abort", new { reservationId = "prior-reservation" }),
            (16, "InvalidReservation"),
            (18, "ReservationAlreadyCompleted"),
            (12, "StoreDisposed"));

        AgentResponse retainedCurrent = await current.SendAsync(
            "read",
            new { leaseId = "current-lease" });
        InteropAssertions.Success(retainedCurrent);
        Assert.Equal(currentValue, InteropAssertions.Decode(retainedCurrent, "value"));
        InteropAssertions.Success(await current.SendAsync("reservationWrite", new
        {
            reservationId = "current-reservation",
            data = AgentProtocol.EncodeBytes(new byte[] { 4, 0, 4 })
        }));
        InteropAssertions.Success(await current.SendAsync(
            "advance",
            new { reservationId = "current-reservation", byteCount = 3 }));
        InteropAssertions.Success(await current.SendAsync(
            "commit",
            new { reservationId = "current-reservation" }));
        InteropAssertions.Success(await current.SendAsync("release", new { leaseId = "current-lease" }));
        InteropAssertions.Success(await current.SendAsync("close", new { storeId = "current" }));
    }

    private static AgentDefinition[] ResolveTriad() =>
        InteropAssertions.Runtimes.Select(AgentDefinition.Resolve).ToArray();

    private static async Task<List<AgentProcess>> StartAgentsAsync(IEnumerable<AgentDefinition> definitions)
    {
        var agents = new List<AgentProcess>();
        try
        {
            foreach (AgentDefinition definition in definitions)
            {
                agents.Add(await AgentProcess.StartAsync(definition));
            }

            return agents;
        }
        catch
        {
            await DisposeAgentsAsync(agents);
            throw;
        }
    }

    private static async Task CloseTriadAsync(IReadOnlyList<AgentProcess> agents)
    {
        for (var index = agents.Count - 1; index >= 0; index--)
        {
            InteropAssertions.Success(await agents[index].SendAsync(
                "close",
                new { storeId = InteropAssertions.Runtimes[index] }));
        }
    }

    private static async Task DisposeAgentsAsync(IReadOnlyList<AgentProcess> agents)
    {
        for (var index = agents.Count - 1; index >= 0; index--)
        {
            await agents[index].DisposeAsync();
        }
    }

    private static void AssertTokenFence(AgentResponse response, params (int Code, string Name)[] allowed)
    {
        Assert.True(response.Ok, response.Error?.Message);
        Assert.Contains(allowed, expected =>
            response.Status.Code == expected.Code && response.Status.Name == expected.Name);
    }

    private static byte[][] GenerateBucketPairCollisions(int count, int slotCount)
    {
        var keys = new List<byte[]>(count);
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / 8) - 1));
        for (long candidate = 1; keys.Count < count; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            ulong hash = Hash(key);
            int first = (int)(Mix(hash) & bucketMask);
            int second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & bucketMask);
            if (second == first)
            {
                second = (first + 1) & (int)bucketMask;
            }

            if (first == 0 && second == 1)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static ulong Hash(ReadOnlySpan<byte> key)
    {
        ulong hash = 14_695_981_039_346_656_037UL;
        foreach (byte value in key)
        {
            hash ^= value;
            hash *= 1_099_511_628_211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }
}
