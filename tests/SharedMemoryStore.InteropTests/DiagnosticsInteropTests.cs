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
        "indexEntryCount",
        "occupiedIndexEntryCount",
        "tombstoneIndexEntryCount",
        "emptyIndexEntryCount",
        "usableIndexCapacity"
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
        foreach (var field in SharedFields)
        {
            Assert.Equal(Number(firstResult, field), Number(secondResult, field));
        }

        Assert.Equal(6, Number(firstResult, "slotCount"));
        Assert.Equal(4, Number(firstResult, "freeSlotCount"));
        Assert.Equal(0, Number(firstResult, "publishedSlotCount"));
        Assert.Equal(1, Number(firstResult, "pendingRemovalCount"));
        Assert.Equal(1, Number(firstResult, "activeLeaseCount"));
        Assert.Equal(1, Number(firstResult, "activeReservationCount"));
        Assert.Equal(10, Number(secondResult, "lastFailureStatus"));

        InteropAssertions.Success(await second.SendAsync("abort", new { reservationId = "reservation" }));
        InteropAssertions.Success(await first.SendAsync("release", new { leaseId = "lease" }));
        InteropAssertions.Success(await second.SendAsync("close", new { storeId = "second" }));
        InteropAssertions.Success(await first.SendAsync("close", new { storeId = "first" }));
    }

    private static long Number(JsonElement value, string property) =>
        value.GetProperty(property).GetInt64();
}
