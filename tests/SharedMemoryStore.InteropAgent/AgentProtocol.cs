using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedMemoryStore.InteropAgent;

public static class AgentProtocol
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string SerializeRequestLine(AgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        return JsonSerializer.Serialize(request, SerializerOptions) + '\n';
    }

    public static string SerializeResponseLine(AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Validate(response);
        return JsonSerializer.Serialize(response, SerializerOptions) + '\n';
    }

    public static AgentRequest ParseRequest(string line)
    {
        var request = ParseLine<AgentRequest>(line);
        Validate(request);
        return request;
    }

    public static AgentResponse ParseResponse(string line)
    {
        var response = ParseLine<AgentResponse>(line);
        Validate(response);
        return response;
    }

    public static async ValueTask<AgentRequest?> ReadRequestAsync(
        TextReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line is null ? null : ParseRequest(line);
    }

    public static async ValueTask WriteResponseAsync(
        TextWriter writer,
        AgentResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        await writer.WriteAsync(SerializeResponseLine(response).AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static JsonElement ToJsonElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, SerializerOptions);

    public static string EncodeBytes(ReadOnlySpan<byte> value) => Convert.ToBase64String(value);

    public static byte[] DecodeBytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.FromBase64String(value);
    }

    private static T ParseLine<T>(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrWhiteSpace(line))
        {
            throw new JsonException("An agent protocol line cannot be empty.");
        }

        if (line.Contains('\r', StringComparison.Ordinal) || line.Contains('\n', StringComparison.Ordinal))
        {
            throw new JsonException("An agent protocol frame must contain exactly one line.");
        }

        return JsonSerializer.Deserialize<T>(line, SerializerOptions)
            ?? throw new JsonException("An agent protocol line must contain a JSON object.");
    }

    private static void Validate(AgentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new JsonException("The request id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new JsonException("The request command is required.");
        }

        if (request.Arguments is { ValueKind: not JsonValueKind.Object })
        {
            throw new JsonException("Request arguments must be a JSON object when present.");
        }
    }

    private static void Validate(AgentResponse response)
    {
        if (response.Id is null)
        {
            throw new JsonException("The response id is required.");
        }

        if (response.Status is null || string.IsNullOrWhiteSpace(response.Status.Name))
        {
            throw new JsonException("A response status code and name are required.");
        }

        if (response.Result is { ValueKind: JsonValueKind.Undefined })
        {
            throw new JsonException("A response result cannot be undefined.");
        }

        if (response.Ok && response.Error is not null)
        {
            throw new JsonException("A successful response cannot contain an error.");
        }

        if (!response.Ok && response.Error is null)
        {
            throw new JsonException("A failed response must contain an error.");
        }

        if (response.Error is not null
            && (string.IsNullOrWhiteSpace(response.Error.Code)
                || string.IsNullOrWhiteSpace(response.Error.Message)))
        {
            throw new JsonException("An error code and message are required.");
        }
    }
}
