using System.Text.Json;
using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;

namespace SharedMemoryStore.InteropTests;

public sealed class DiagnosticsInteropTests
{
    private static readonly string[] SharedFields =
    [
        "totalBytes",
        "slotCount",
        "freeSlotCount",
        "publishedSlotCount",
        "pendingRemovalCount",
        "activeLeaseCount",
        "activeReservationCount",
        "initializingSlotCount",
        "reservedSlotCount",
        "reclaimingSlotCount",
        "retiredSlotCount",
        "claimingLeaseCount",
        "recoveringLeaseCount",
        "freeLeaseCount",
        "retiredLeaseCount",
        "participantRecordCount",
        "freeParticipantCount",
        "registeringParticipantCount",
        "activeParticipantCount",
        "closingParticipantCount",
        "recoveringParticipantCount",
        "reclaimingParticipantCount",
        "retiredParticipantCount",
        "indexEntryCount",
        "occupiedIndexEntryCount",
        "emptyIndexEntryCount",
        "usableIndexCapacity",
        "primaryDirectoryOccupancy",
        "spilledBucketCount",
        "overflowDirectoryOccupancy"
    ];

    private static readonly string[] LocalCounterFields =
    [
        "abortedReservationCount",
        "recoveredLeaseCount",
        "activeLeaseRecoveryCount",
        "unsupportedLeaseRecoveryCount",
        "failedLeaseRecoveryCount",
        "recoveredReservationCount",
        "activeReservationRecoveryCount",
        "unsupportedReservationRecoveryCount",
        "failedReservationRecoveryCount",
        "capacityPressureCount",
        "lastObservedProbeLength",
        "maxObservedProbeLength",
        "overflowScanCount",
        "maxObservedOverflowScanLength",
        "casRetryCount",
        "helpedTransitionCount",
        "contentionBudgetExhaustionCount",
        "invalidTokenCount",
        "staleTokenCount",
        "recoveryAttemptCount",
        "recoveredTransitionCount",
        "currentOwnerClassificationCount",
        "liveOwnerClassificationCount",
        "staleOwnerClassificationCount",
        "unsupportedOwnerClassificationCount",
        "inconsistentOwnerClassificationCount",
        "changingOwnerClassificationCount",
        "lastFailureStatus"
    ];

    [Theory]
    [MemberData(
        nameof(CoreExchangeMatrixTests.OrderedRuntimePairs),
        MemberType = typeof(CoreExchangeMatrixTests))]
    public async Task RuntimesReportEquivalentSharedStateAndCallerLocalFailures(
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
        var name = $"sms-diagnostics-{firstRuntime}-{secondRuntime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await first.SendAsync(
            "open",
            InteropAssertions.OpenArguments("first", name, openMode: 0)));
        InteropAssertions.Success(await second.SendAsync(
            "open",
            InteropAssertions.OpenArguments("second", name, openMode: 1)));

        var leasedKey = AgentProtocol.EncodeBytes(new byte[] { 1, 0, 5 });
        InteropAssertions.Success(await first.SendAsync("publish", new
        {
            storeId = "first",
            key = leasedKey,
            value = AgentProtocol.EncodeBytes(new byte[] { 7, 0, 7 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await first.SendAsync("acquire", new
        {
            storeId = "first",
            leaseId = "lease",
            key = leasedKey
        }));
        InteropAssertions.Status(
            await second.SendAsync("remove", new { storeId = "second", key = leasedKey }),
            10,
            "RemovePending");
        InteropAssertions.Status(await first.SendAsync("publish", new
        {
            storeId = "first",
            key = leasedKey,
            value = AgentProtocol.EncodeBytes(new byte[] { 9, 0, 9 }),
            descriptor = string.Empty
        }), 1, "DuplicateKey");
        InteropAssertions.Success(await second.SendAsync("reserve", new
        {
            storeId = "second",
            reservationId = "reservation",
            key = AgentProtocol.EncodeBytes(new byte[] { 2, 0, 6 }),
            payloadLength = 5,
            descriptor = string.Empty
        }));

        var firstDiagnostics = await first.SendAsync("diagnostics", new { storeId = "first" });
        var secondDiagnostics = await second.SendAsync("diagnostics", new { storeId = "second" });
        InteropAssertions.Success(firstDiagnostics);
        InteropAssertions.Success(secondDiagnostics);
        var firstResult = firstDiagnostics.Result!.Value;
        var secondResult = secondDiagnostics.Result!.Value;
        Assert.Equal("first", firstResult.GetProperty("storeId").GetString());
        Assert.Equal("second", secondResult.GetProperty("storeId").GetString());
        AssertProtocolIdentity(firstResult.GetProperty("protocolInfo"));
        AssertProtocolIdentity(secondResult.GetProperty("protocolInfo"));
        foreach (var field in SharedFields)
        {
            Assert.Equal(Number(firstResult, field), Number(secondResult, field));
        }

        AssertLocalTelemetryShape(firstResult, firstRuntime);
        AssertLocalTelemetryShape(secondResult, secondRuntime);

        Assert.Equal(6, Number(firstResult, "slotCount"));
        Assert.Equal(4, Number(firstResult, "freeSlotCount"));
        Assert.Equal(0, Number(firstResult, "publishedSlotCount"));
        Assert.Equal(1, Number(firstResult, "pendingRemovalCount"));
        Assert.Equal(1, Number(firstResult, "activeLeaseCount"));
        Assert.Equal(1, Number(firstResult, "activeReservationCount"));
        Assert.Equal(64, Number(firstResult, "participantRecordCount"));
        Assert.Equal(2, Number(firstResult, "activeParticipantCount"));
        Assert.Equal(
            Number(firstResult, "indexEntryCount"),
            Number(firstResult, "occupiedIndexEntryCount") + Number(firstResult, "emptyIndexEntryCount"));
        Assert.Equal(Number(firstResult, "emptyIndexEntryCount"), Number(firstResult, "usableIndexCapacity"));
        Assert.Equal(
            Number(firstResult, "occupiedIndexEntryCount"),
            Number(firstResult, "primaryDirectoryOccupancy")
                + Number(firstResult, "overflowDirectoryOccupancy"));
        Assert.Equal(
            Number(firstResult, "slotCount"),
            Number(firstResult, "freeSlotCount")
                + Number(firstResult, "initializingSlotCount")
                + Number(firstResult, "reservedSlotCount")
                + Number(firstResult, "publishedSlotCount")
                + Number(firstResult, "pendingRemovalCount")
                + Number(firstResult, "reclaimingSlotCount")
                + Number(firstResult, "retiredSlotCount"));
        Assert.Equal(
            16,
            Number(firstResult, "freeLeaseCount")
                + Number(firstResult, "claimingLeaseCount")
                + Number(firstResult, "activeLeaseCount")
                + Number(firstResult, "recoveringLeaseCount")
                + Number(firstResult, "retiredLeaseCount"));
        Assert.Equal(
            Number(firstResult, "participantRecordCount"),
            Number(firstResult, "freeParticipantCount")
                + Number(firstResult, "registeringParticipantCount")
                + Number(firstResult, "activeParticipantCount")
                + Number(firstResult, "closingParticipantCount")
                + Number(firstResult, "recoveringParticipantCount")
                + Number(firstResult, "reclaimingParticipantCount")
                + Number(firstResult, "retiredParticipantCount"));

        Assert.Equal(1, Number(firstResult, "lastFailureStatus"));
        Assert.Equal(10, Number(secondResult, "lastFailureStatus"));
        Assert.Equal(1, FailureCount(firstResult, statusCode: 1));
        Assert.Equal(0, FailureCount(firstResult, statusCode: 10));
        Assert.Equal(0, FailureCount(secondResult, statusCode: 1));
        Assert.Equal(1, FailureCount(secondResult, statusCode: 10));

        InteropAssertions.Success(await second.SendAsync("abort", new { reservationId = "reservation" }));
        InteropAssertions.Success(await first.SendAsync("release", new { leaseId = "lease" }));
        InteropAssertions.Success(await second.SendAsync("close", new { storeId = "second" }));
        InteropAssertions.Success(await first.SendAsync("close", new { storeId = "first" }));
    }

    private static long Number(JsonElement value, string property) =>
        value.GetProperty(property).GetInt64();

    private static long FailureCount(JsonElement value, int statusCode) =>
        value.GetProperty("failureCounts")[statusCode].GetInt64();

    private static void AssertLocalTelemetryShape(JsonElement value, string runtime)
    {
        foreach (string field in LocalCounterFields)
        {
            Assert.True(
                value.TryGetProperty(field, out JsonElement counter),
                $"The {runtime} runtime omitted handle-local diagnostic '{field}'.");
            Assert.Equal(JsonValueKind.Number, counter.ValueKind);
            Assert.True(counter.GetInt64() >= 0, $"The {runtime} local counter '{field}' was negative.");
        }

        Assert.True(
            value.TryGetProperty("failureCounts", out JsonElement failures),
            $"The {runtime} runtime omitted handle-local failureCounts.");
        Assert.Equal(JsonValueKind.Array, failures.ValueKind);
        Assert.Equal(23, failures.GetArrayLength());
        Assert.All(failures.EnumerateArray(), failure => Assert.True(failure.GetInt64() >= 0));
    }

    private static void AssertProtocolIdentity(JsonElement protocol)
    {
        Assert.Equal(2, protocol.GetProperty("layoutMajorVersion").GetInt32());
        Assert.Equal(0, protocol.GetProperty("layoutMinorVersion").GetInt32());
        Assert.Equal(2, protocol.GetProperty("resourceProtocolVersion").GetInt32());
        Assert.Equal(7UL, protocol.GetProperty("requiredFeatures").GetUInt64());
        Assert.Equal(0UL, protocol.GetProperty("optionalFeatures").GetUInt64());
    }
}
