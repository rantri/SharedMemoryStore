using SharedMemoryStore.InteropAgent;

namespace SharedMemoryStore.InteropTests;

public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task DotNetAgentExecutesValueAndReservationLifecycles()
    {
        var name = $"sms-interop-agent-{Guid.NewGuid():N}";
        var requests = new[]
        {
            Request("1", "open", new
            {
                storeId = "store",
                name,
                openMode = 0,
                slotCount = 3,
                maxValueBytes = 32,
                maxDescriptorBytes = 8,
                maxKeyBytes = 8,
                leaseRecordCount = 4,
                enableLeaseRecovery = true
            }),
            Request("2", "publish", new
            {
                storeId = "store",
                key = AgentProtocol.EncodeBytes([1, 0]),
                value = AgentProtocol.EncodeBytes([7, 0, 9]),
                descriptor = AgentProtocol.EncodeBytes([4])
            }),
            Request("3", "acquire", new
            {
                storeId = "store",
                leaseId = "lease",
                key = AgentProtocol.EncodeBytes([1, 0])
            }),
            Request("4", "remove", new
            {
                storeId = "store",
                key = AgentProtocol.EncodeBytes([1, 0])
            }),
            Request("5", "release", new { leaseId = "lease" }),
            Request("6", "reserve", new
            {
                storeId = "store",
                reservationId = "reservation",
                key = AgentProtocol.EncodeBytes([2]),
                payloadLength = 3,
                descriptor = AgentProtocol.EncodeBytes([5])
            }),
            Request("7", "reservationWrite", new
            {
                reservationId = "reservation",
                data = AgentProtocol.EncodeBytes([8, 0, 6])
            }),
            Request("8", "advance", new { reservationId = "reservation", byteCount = 3 }),
            Request("9", "commit", new { reservationId = "reservation" }),
            Request("10", "acquire", new
            {
                storeId = "store",
                leaseId = "reservation-lease",
                key = AgentProtocol.EncodeBytes([2])
            }),
            Request("11", "diagnostics", new { storeId = "store" }),
            Request("12", "close", new { storeId = "store" })
        };
        using var input = new StringReader(string.Concat(requests.Select(AgentProtocol.SerializeRequestLine)));
        using var output = new StringWriter();

        Assert.Equal(0, await AgentHost.RunAsync(input, output));

        var responses = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(AgentProtocol.ParseResponse)
            .ToArray();
        Assert.Equal(requests.Length, responses.Length);
        Assert.All(responses, response => Assert.True(response.Ok));
        Assert.Equal("Success", responses[0].Status.Name);
        Assert.Equal("Success", responses[1].Status.Name);
        Assert.Equal(new byte[] { 7, 0, 9 }, DecodeResult(responses[2], "value"));
        Assert.Equal("RemovePending", responses[3].Status.Name);
        Assert.Equal("Success", responses[4].Status.Name);
        Assert.Equal(new byte[] { 8, 0, 6 }, DecodeResult(responses[9], "value"));
        Assert.Equal(1, responses[10].Result!.Value.GetProperty("publishedSlotCount").GetInt32());
    }

    private static AgentRequest Request<T>(string id, string command, T arguments) =>
        new()
        {
            Id = id,
            Command = command,
            Arguments = AgentProtocol.ToJsonElement(arguments)
        };

    private static byte[] DecodeResult(AgentResponse response, string property) =>
        AgentProtocol.DecodeBytes(response.Result!.Value.GetProperty(property).GetString()!);
}
