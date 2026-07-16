using System.Diagnostics;

internal readonly record struct RepositoryProvenanceSnapshot(
    string Commit,
    string WorkingTreeState);

internal static class RepositoryEnvironmentProbe
{
    private const string CommitOption = "--repository-commit";
    private const string WorkingTreeStateOption = "--repository-working-tree-state";
    private const string Unknown = "unknown";
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    internal static RepositoryProvenanceSnapshot Capture(string[] args) =>
        Capture(args, Environment.CurrentDirectory, BoundedProcessRunner.Run);

    internal static RepositoryProvenanceSnapshot Capture(
        IReadOnlyList<string> args,
        string currentDirectory,
        Func<ProcessStartInfo, TimeSpan, BoundedProcessResult> runProcess)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentNullException.ThrowIfNull(runProcess);

        string? injectedCommit = ReadUniqueOption(args, CommitOption);
        string? injectedWorkingTreeState = ReadUniqueOption(args, WorkingTreeStateOption);
        if ((injectedCommit is null) != (injectedWorkingTreeState is null))
        {
            throw new ArgumentException(
                $"{CommitOption} and {WorkingTreeStateOption} must be supplied together.");
        }

        if (injectedCommit is not null)
        {
            ValidateCommit(injectedCommit, CommitOption);
            ValidateWorkingTreeState(injectedWorkingTreeState!, WorkingTreeStateOption);
            return new RepositoryProvenanceSnapshot(injectedCommit, injectedWorkingTreeState!);
        }

        return CaptureFromGit(currentDirectory, runProcess);
    }

    private static RepositoryProvenanceSnapshot CaptureFromGit(
        string currentDirectory,
        Func<ProcessStartInfo, TimeSpan, BoundedProcessResult> runProcess)
    {
        try
        {
            string? repositoryRoot = FindRepositoryRoot(currentDirectory);
            if (repositoryRoot is null)
            {
                return UnknownSnapshot();
            }

            BoundedProcessResult commit = runProcess(
                CreateGitStartInfo(repositoryRoot, "rev-parse", "HEAD"),
                GitTimeout);
            if (!commit.Succeeded)
            {
                return UnknownSnapshot();
            }

            string commitValue = commit.StandardOutput.Trim();
            if (!IsValidCommit(commitValue))
            {
                return UnknownSnapshot();
            }

            BoundedProcessResult status = runProcess(
                CreateGitStartInfo(
                    repositoryRoot,
                    "status",
                    "--porcelain=v2",
                    "--untracked-files=normal"),
                GitTimeout);
            if (!status.Succeeded)
            {
                return UnknownSnapshot();
            }

            return new RepositoryProvenanceSnapshot(
                commitValue,
                string.IsNullOrWhiteSpace(status.StandardOutput) ? "clean" : "dirty");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return UnknownSnapshot();
        }
    }

    internal static string? FindRepositoryRoot(string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var directory = new DirectoryInfo(Path.GetFullPath(currentDirectory));
        while (directory is not null)
        {
            string marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static ProcessStartInfo CreateGitStartInfo(
        string repositoryRoot,
        params string[] commandArguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.autocrlf=true");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.safecrlf=false");
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryRoot);
        foreach (string argument in commandArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string? ReadUniqueOption(IReadOnlyList<string> args, string option)
    {
        string? value = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.Ordinal))
            {
                continue;
            }

            if (value is not null)
            {
                throw new ArgumentException($"{option} may be supplied only once.", option);
            }

            if (index + 1 >= args.Count
                || string.IsNullOrWhiteSpace(args[index + 1])
                || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{option} requires a value.", option);
            }

            value = args[index + 1];
            index++;
        }

        return value;
    }

    private static void ValidateCommit(string commit, string option)
    {
        if (!IsValidCommit(commit))
        {
            throw new ArgumentException(
                $"{option} must be exactly 40 or 64 hexadecimal characters.",
                option);
        }
    }

    private static bool IsValidCommit(string commit) =>
        (commit.Length == 40 || commit.Length == 64)
        && commit.All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static void ValidateWorkingTreeState(string state, string option)
    {
        if (!string.Equals(state, "clean", StringComparison.Ordinal)
            && !string.Equals(state, "dirty", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} must be 'clean' or 'dirty'.", option);
        }
    }

    private static RepositoryProvenanceSnapshot UnknownSnapshot() => new(Unknown, Unknown);
}
