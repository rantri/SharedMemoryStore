using System.Diagnostics;
using SharedMemoryStore.InteropAgent;

namespace SharedMemoryStore.InteropTests.TestSupport;

internal sealed record AgentDefinition(
    string Runtime,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? Environment = null)
{
    public static AgentDefinition Resolve(string runtime)
    {
        var repository = RepositoryRoot();
        return runtime switch
        {
            "dotnet" => new AgentDefinition(
                runtime,
                "dotnet",
                [typeof(AgentHost).Assembly.Location]),
            "cpp" => new AgentDefinition(
                runtime,
                System.Environment.GetEnvironmentVariable("SMS_CPP_AGENT") ?? DefaultCppAgent(repository),
                []),
            "python" => new AgentDefinition(
                runtime,
                System.Environment.GetEnvironmentVariable("SMS_PYTHON_EXECUTABLE") ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
                [Path.Combine(repository, "tests", "python", "interop_agent.py")],
                new Dictionary<string, string?>
                {
                    ["PYTHONPATH"] = Path.Combine(repository, "src", "python")
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, "Unknown agent runtime.")
        };
    }

    public bool IsAvailable()
    {
        if (Path.IsPathFullyQualified(Executable) && !File.Exists(Executable))
        {
            return false;
        }

        if (Runtime == "python" && (Arguments.Count == 0 || !File.Exists(Arguments[0])))
        {
            return false;
        }

        if (Runtime == "python")
        {
            var nativeLibrary = OperatingSystem.IsWindows()
                ? "shared_memory_store.dll"
                : "libshared_memory_store.so";
            if (!File.Exists(Path.Combine(
                    RepositoryRoot(),
                    "src",
                    "python",
                    "shared_memory_store",
                    nativeLibrary)))
            {
                return false;
            }
        }

        return true;
    }

    private static string DefaultCppAgent(string repository) => OperatingSystem.IsWindows()
        ? Path.Combine(repository, "artifacts", "native-win", "sms_cpp_interop_agent.exe")
        : Path.Combine(repository, "artifacts", "cmake-wsl", "tests", "cpp", "sms_cpp_interop_agent");

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SharedMemoryStore.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the SharedMemoryStore repository root.");
    }
}

internal sealed class AgentProcess : IAsyncDisposable
{
    private readonly AgentDefinition _definition;
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private int _requestSequence;

    private AgentProcess(AgentDefinition definition, Process process)
    {
        _definition = definition;
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static async Task<AgentProcess> StartAsync(AgentDefinition definition)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = definition.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };
        foreach (var argument in definition.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (definition.Environment is not null)
        {
            foreach (var pair in definition.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the {definition.Runtime} interoperability agent.");
        var result = new AgentProcess(definition, process);
        var ping = await result.SendAsync<object?>("ping", arguments: null).ConfigureAwait(false);
        if (!ping.Ok || ping.Status.Name != "Success")
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The {definition.Runtime} agent did not answer ping successfully.");
        }

        return result;
    }

    public async Task<AgentResponse> SendAsync<T>(string command, T? arguments, TimeSpan? timeout = null)
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"The {_definition.Runtime} agent exited with code {_process.ExitCode}. stderr: {await _stderr.ConfigureAwait(false)}");
        }

        var request = new AgentRequest
        {
            Id = Interlocked.Increment(ref _requestSequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command = command,
            Arguments = arguments is null ? null : AgentProtocol.ToJsonElement(arguments)
        };
        await _process.StandardInput.WriteAsync(AgentProtocol.SerializeRequestLine(request)).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var line = await _process.StandardOutput.ReadLineAsync(cancellation.Token).ConfigureAwait(false);
        if (line is null)
        {
            throw new InvalidOperationException(
                $"The {_definition.Runtime} agent closed stdout. stderr: {await _stderr.ConfigureAwait(false)}");
        }

        var response = AgentProtocol.ParseResponse(line);
        Assert.Equal(request.Id, response.Id);
        return response;
    }

    public async Task CrashAsync(TimeSpan? timeout = null)
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"The {_definition.Runtime} agent already exited with code {_process.ExitCode}.");
        }

        var request = new AgentRequest
        {
            Id = Interlocked.Increment(ref _requestSequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command = "crash",
            Arguments = null
        };
        await _process.StandardInput.WriteAsync(AgentProtocol.SerializeRequestLine(request)).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);

        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        await _process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        var stderr = await _stderr.ConfigureAwait(false);
        Assert.True(
            _process.ExitCode == 97,
            $"The {_definition.Runtime} crash command exited with code {_process.ExitCode}; expected 97. stderr: {stderr}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
