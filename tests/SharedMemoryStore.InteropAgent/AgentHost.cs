using System.Text.Json;

namespace SharedMemoryStore.InteropAgent;

public static class AgentHost
{
    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        using var session = new AgentSession();
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            AgentResponse response;

            try
            {
                response = session.Handle(AgentProtocol.ParseRequest(line));
            }
            catch (JsonException exception)
            {
                response = Failure(
                    id: string.Empty,
                    statusCode: -1,
                    statusName: "ProtocolError",
                    errorCode: "invalid_request",
                    message: exception.Message);
            }

            await AgentProtocol.WriteResponseAsync(output, response, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    internal static AgentResponse Failure(
        string id,
        int statusCode,
        string statusName,
        string errorCode,
        string message) =>
        new()
        {
            Id = id,
            Ok = false,
            Status = new AgentStatus { Code = statusCode, Name = statusName },
            Error = new AgentError { Code = errorCode, Message = message }
        };
}
