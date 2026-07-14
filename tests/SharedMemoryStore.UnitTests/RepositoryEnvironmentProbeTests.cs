using System.Diagnostics;

namespace SharedMemoryStore.UnitTests;

public sealed class RepositoryEnvironmentProbeTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Sha1, "clean")]
    [InlineData(Sha256, "dirty")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01", "clean")]
    public void InjectedPairBypassesGit(string commit, string state)
    {
        var processCalls = 0;

        RepositoryProvenanceSnapshot result = RepositoryEnvironmentProbe.Capture(
            ["--repository-commit", commit, "--repository-working-tree-state", state],
            Path.GetTempPath(),
            (_, _) =>
            {
                processCalls++;
                throw new InvalidOperationException("Git must not run for injected provenance.");
            });

        Assert.Equal(commit, result.Commit);
        Assert.Equal(state, result.WorkingTreeState);
        Assert.Equal(0, processCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidPartialDuplicateOrMissingInjectionThrows(string[] args)
    {
        Assert.Throws<ArgumentException>(() => RepositoryEnvironmentProbe.Capture(
            args,
            Path.GetTempPath(),
            static (_, _) => throw new InvalidOperationException("Git must not run.")));
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        { ["--repository-commit", Sha1] },
        { ["--repository-working-tree-state", "clean"] },
        { ["--repository-commit"] },
        { ["--repository-commit", "--repository-working-tree-state", "clean"] },
        { ["--repository-working-tree-state"] },
        { ["--repository-commit", Sha1, "--repository-commit", Sha1,
            "--repository-working-tree-state", "clean"] },
        { ["--repository-commit", Sha1, "--repository-working-tree-state", "clean",
            "--repository-working-tree-state", "clean"] },
        { ["--repository-commit", "abc", "--repository-working-tree-state", "clean"] },
        { ["--repository-commit", new string('g', 40),
            "--repository-working-tree-state", "clean"] },
        { ["--repository-commit", Sha1, "--repository-working-tree-state", "CLEAN"] },
        { ["--repository-commit", Sha1, "--repository-working-tree-state", "unknown"] }
    };

    [Fact]
    public void GitFallbackUsesExplicitSafeConfigurationAndReportsClean()
    {
        var invocations = new List<string[]>();

        RepositoryProvenanceSnapshot result = RepositoryEnvironmentProbe.Capture(
            [],
            AppContext.BaseDirectory,
            (startInfo, timeout) =>
            {
                invocations.Add(startInfo.ArgumentList.ToArray());
                Assert.Equal(TimeSpan.FromSeconds(30), timeout);
                Assert.Equal("0", startInfo.Environment["GIT_OPTIONAL_LOCKS"]);
                return invocations.Count == 1
                    ? Successful(Sha1 + Environment.NewLine)
                    : Successful(string.Empty);
            });

        Assert.Equal(Sha1, result.Commit);
        Assert.Equal("clean", result.WorkingTreeState);
        Assert.Equal(2, invocations.Count);
        AssertGitPrefix(invocations[0]);
        Assert.Equal(["rev-parse", "HEAD"], invocations[0][6..]);
        AssertGitPrefix(invocations[1]);
        Assert.Equal(
            ["status", "--porcelain=v2", "--untracked-files=normal"],
            invocations[1][6..]);
    }

    [Fact]
    public void GitFallbackReportsDirtyForPorcelainV2Output()
    {
        var invocation = 0;
        RepositoryProvenanceSnapshot result = RepositoryEnvironmentProbe.Capture(
            [],
            AppContext.BaseDirectory,
            (_, _) => ++invocation == 1
                ? Successful(Sha256)
                : Successful("? untracked.txt"));

        Assert.Equal(Sha256, result.Commit);
        Assert.Equal("dirty", result.WorkingTreeState);
    }

    [Fact]
    public void GitFailureMakesThePairUnknown()
    {
        RepositoryProvenanceSnapshot result = RepositoryEnvironmentProbe.Capture(
            [],
            AppContext.BaseDirectory,
            static (_, _) => new BoundedProcessResult(
                Started: true,
                TimedOut: false,
                ExitCode: 128,
                StandardOutput: string.Empty,
                StandardError: "not a repository"));

        Assert.Equal("unknown", result.Commit);
        Assert.Equal("unknown", result.WorkingTreeState);
    }

    [Fact]
    public void BoundedRunnerDrainsBothStreamsAndReturnsExitCode()
    {
        ProcessStartInfo startInfo = CreateShellStartInfo(
            OperatingSystem.IsWindows()
                ? "echo standard-output & echo standard-error 1>&2 & exit /b 7"
                : "printf standard-output; printf standard-error >&2; exit 7");

        BoundedProcessResult result = BoundedProcessRunner.Run(startInfo, TimeSpan.FromSeconds(5));

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("standard-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("standard-error", result.StandardError, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void BoundedRunnerKillsAndReapsTimedOutProcessTree()
    {
        ProcessStartInfo startInfo = CreateShellStartInfo(
            OperatingSystem.IsWindows()
                ? "ping 127.0.0.1 -n 30 > nul"
                : "sleep 30");
        var stopwatch = Stopwatch.StartNew();

        BoundedProcessResult result = BoundedProcessRunner.Run(
            startInfo,
            TimeSpan.FromMilliseconds(200));

        stopwatch.Stop();
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.NotNull(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void BoundedRunnerDoesNotHangWhenExitedParentLeavesInheritedPipesOpen()
    {
        ProcessStartInfo startInfo = CreateExitedParentWithPipeHoldingChildStartInfo();
        var stopwatch = Stopwatch.StartNew();

        BoundedProcessResult result = BoundedProcessRunner.Run(
            startInfo,
            TimeSpan.FromSeconds(2));

        stopwatch.Stop();
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), stopwatch.Elapsed.ToString());
    }

    private static BoundedProcessResult Successful(string output) => new(
        Started: true,
        TimedOut: false,
        ExitCode: 0,
        StandardOutput: output,
        StandardError: string.Empty);

    private static ProcessStartInfo CreateShellStartInfo(string command)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe")
            : new ProcessStartInfo("/bin/sh");
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
        }

        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static ProcessStartInfo CreateExitedParentWithPipeHoldingChildStartInfo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CreateShellStartInfo("sleep 10 &");
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$start = [Diagnostics.ProcessStartInfo]::new('cmd.exe', "
            + "'/d /c ping 127.0.0.1 -n 12'); "
            + "$start.UseShellExecute = $false; "
            + "[Diagnostics.Process]::Start($start) | Out-Null");
        return startInfo;
    }

    private static void AssertGitPrefix(string[] arguments)
    {
        Assert.Equal("-c", arguments[0]);
        Assert.Equal("core.autocrlf=true", arguments[1]);
        Assert.Equal("-c", arguments[2]);
        Assert.Equal("core.safecrlf=false", arguments[3]);
        Assert.Equal("-C", arguments[4]);
        Assert.Equal(RepositoryEnvironmentProbe.FindRepositoryRoot(AppContext.BaseDirectory), arguments[5]);
    }
}
