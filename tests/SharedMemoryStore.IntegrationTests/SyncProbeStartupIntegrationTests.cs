using System.Diagnostics;
using System.Globalization;
using SharedMemoryStore.IntegrationTests.TestSupport;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

public sealed class SyncProbeStartupIntegrationTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WorkerWaitsThroughTransientColdOpenContention()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        string name = $"sms-sync-probe-open-{Guid.NewGuid():N}";
        StoreOpenStatus ownerStatus = Store.TryCreateOrOpen(
            Options(name, OpenMode.CreateNew),
            out Store? owner);
        Assert.Equal(StoreOpenStatus.Success, ownerStatus);
        Assert.NotNull(owner);

        using (owner)
        {
            using var heldColdGate = new CrossThreadColdGateHolder(name);
            using Process worker = StartWorker(name);
            try
            {
                Task<string?> readiness = worker.StandardOutput.ReadLineAsync();
                await Task.Delay(TimeSpan.FromSeconds(5));

                Assert.False(worker.HasExited, await FailureMessage(worker, "Worker exited during transient cold contention."));
                Assert.False(readiness.IsCompleted, "Worker became ready while the cold gate was still held.");

                heldColdGate.Dispose();

                Assert.Equal("READY", await readiness.WaitAsync(ProcessTimeout));
                await worker.StandardInput.WriteLineAsync("GO");
                await worker.StandardInput.FlushAsync();
                string? result = await worker.StandardOutput.ReadLineAsync().WaitAsync(ProcessTimeout);
                await worker.WaitForExitAsync().WaitAsync(ProcessTimeout);

                Assert.Equal(0, worker.ExitCode);
                Assert.False(string.IsNullOrWhiteSpace(result));
            }
            finally
            {
                heldColdGate.Dispose();
                if (!worker.HasExited)
                {
                    worker.Kill(entireProcessTree: true);
                    await worker.WaitForExitAsync().WaitAsync(ProcessTimeout);
                }
            }
        }
    }

    private static Process StartWorker(string name)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(LocateSyncProbeAssembly());
        foreach (string argument in new[]
        {
            "worker",
            "same-key-read",
            "Sms2",
            name,
            "0",
            "0",
            "0",
            "-1",
            "0",
            "0"
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start the sync-probe worker.");
    }

    private static async Task<string> FailureMessage(Process worker, string message)
    {
        string error = worker.HasExited
            ? await worker.StandardError.ReadToEndAsync()
            : "<still-running>";
        return message + " exit="
            + (worker.HasExited ? worker.ExitCode.ToString(CultureInfo.InvariantCulture) : "running")
            + Environment.NewLine + "stderr=" + error;
    }

    private static string LocateSyncProbeAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        string path = Path.Combine(
            root,
            "benchmarks",
            "SharedMemoryStore.SyncProbe",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.SyncProbe.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Sync-probe worker was not built.", path);
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount: 256,
            maxValueBytes: 256,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: 64,
            participantRecordCount: 64,
            openMode,
            enableLeaseRecovery: true);

    private sealed class CrossThreadColdGateHolder : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Thread _thread;
        private Exception? _failure;
        private int _disposed;

        internal CrossThreadColdGateHolder(string storeName)
        {
            using var ready = new ManualResetEventSlim(initialState: false);
            _thread = new Thread(() =>
            {
                try
                {
                    using IDisposable held = PlatformCapabilityProbe.HoldStoreSynchronization(storeName);
                    ready.Set();
                    _release.Wait();
                }
                catch (Exception exception)
                {
                    _failure = exception;
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "Sync-probe regression cold-gate holder"
            };
            _thread.Start();
            if (!ready.Wait(ProcessTimeout))
            {
                throw new TimeoutException("Cold-gate holder did not become ready.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException("Cold-gate holder failed to acquire the gate.", _failure);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _release.Set();
            if (!_thread.Join(ProcessTimeout))
            {
                throw new TimeoutException("Cold-gate holder did not stop.");
            }

            _release.Dispose();
            if (_failure is not null)
            {
                throw new InvalidOperationException("Cold-gate holder failed.", _failure);
            }
        }
    }
}
