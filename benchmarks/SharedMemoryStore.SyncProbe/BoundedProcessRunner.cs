using System.Diagnostics;

internal readonly record struct BoundedProcessResult(
    bool Started,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError)
{
    internal bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

internal static class BoundedProcessRunner
{
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(2);

    internal static BoundedProcessResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The process timeout must be positive and no greater than Int32.MaxValue milliseconds.");
        }

        if (startInfo.UseShellExecute
            || !startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "Bounded processes must disable shell execution and redirect stdout and stderr.",
                nameof(startInfo));
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return NotStarted("Process.Start returned null.");
            }

            // Start both drains before waiting. A child can otherwise block on a
            // full stdout or stderr pipe and never reach process exit.
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            long operationDeadline = CreateDeadline(timeout);
            bool exited = WaitForExit(process, operationDeadline);
            bool drained = exited
                && WaitForDrains(standardOutput, standardError, operationDeadline);
            bool timedOut = !exited || !drained;
            if (timedOut)
            {
                long cleanupDeadline = CreateDeadline(CleanupGrace);
                Terminate(process);
                exited = WaitForExit(process, cleanupDeadline);
                drained = WaitForDrains(standardOutput, standardError, cleanupDeadline);
            }

            string output = ReadCompletedDrain(standardOutput, out bool outputCompleted);
            string error = ReadCompletedDrain(standardError, out bool errorCompleted);
            drained &= outputCompleted && errorCompleted;
            if (!drained)
            {
                CloseIncompleteDrain(process.StandardOutput, standardOutput);
                CloseIncompleteDrain(process.StandardError, standardError);
            }

            int? exitCode = exited ? TryGetExitCode(process) : null;
            return new BoundedProcessResult(
                Started: true,
                TimedOut: timedOut || !exited || !drained,
                ExitCode: exitCode,
                StandardOutput: output,
                StandardError: error);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException)
        {
            return NotStarted(exception.Message);
        }
    }

    private static void Terminate(Process process)
    {
        TryKill(process, entireProcessTree: true);
        TryKill(process, entireProcessTree: false);
    }

    private static void TryKill(Process process, bool entireProcessTree)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout and the kill attempt.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // A whole-tree kill can be unavailable even when terminating the
            // root process remains possible. The caller always attempts both.
        }
    }

    private static bool WaitForExit(Process process, long deadline)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            int remainingMilliseconds = GetRemainingMilliseconds(deadline);
            return remainingMilliseconds > 0
                && process.WaitForExit(remainingMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool WaitForDrains(
        Task<string> standardOutput,
        Task<string> standardError,
        long deadline)
    {
        Task drains = Task.WhenAll(standardOutput, standardError);
        if (!drains.IsCompleted)
        {
            int remainingMilliseconds = GetRemainingMilliseconds(deadline);
            if (remainingMilliseconds <= 0
                || Task.WaitAny([drains], remainingMilliseconds) != 0)
            {
                return false;
            }
        }

        return standardOutput.IsCompletedSuccessfully
            && standardError.IsCompletedSuccessfully;
    }

    private static string ReadCompletedDrain(Task<string> drain, out bool completed)
    {
        completed = drain.IsCompletedSuccessfully;
        return completed ? drain.Result : string.Empty;
    }

    private static void CloseIncompleteDrain(StreamReader reader, Task<string> drain)
    {
        if (drain.IsCompleted)
        {
            return;
        }

        _ = drain.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            reader.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The result is already fail-closed. Disposal is only best-effort
            // cancellation of a descendant-held redirected pipe.
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static long CreateDeadline(TimeSpan duration)
    {
        double timestampTicks = duration.TotalSeconds * Stopwatch.Frequency;
        return checked(Stopwatch.GetTimestamp() + (long)Math.Ceiling(timestampTicks));
    }

    private static int GetRemainingMilliseconds(long deadline)
    {
        long remainingTicks = deadline - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return 0;
        }

        double milliseconds = remainingTicks * 1000d / Stopwatch.Frequency;
        return Math.Max(1, checked((int)Math.Ceiling(milliseconds)));
    }

    private static BoundedProcessResult NotStarted(string error) => new(
        Started: false,
        TimedOut: false,
        ExitCode: null,
        StandardOutput: string.Empty,
        StandardError: error);
}
