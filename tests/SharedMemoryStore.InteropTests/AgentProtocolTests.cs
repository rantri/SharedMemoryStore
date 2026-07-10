using System.Text.Json;
using SharedMemoryStore.InteropAgent;

namespace SharedMemoryStore.InteropTests;

public sealed class AgentProtocolTests
{
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
        Assert.Equal("dotnet", pingResponse.Result!.Value.GetProperty("runtime").GetString());

        var unsupportedResponse = AgentProtocol.ParseResponse(lines[1]);
        Assert.False(unsupportedResponse.Ok);
        Assert.Equal("UnsupportedCommand", unsupportedResponse.Status.Name);
        Assert.Equal("unsupported_command", unsupportedResponse.Error!.Code);
    }
}
