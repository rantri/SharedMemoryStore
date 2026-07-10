using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedMemoryStore.InteropAgent;

public sealed record AgentRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

public sealed record AgentResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("status")]
    public required AgentStatus Status { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public AgentError? Error { get; init; }
}

public sealed record AgentStatus
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record AgentError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
