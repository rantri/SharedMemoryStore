using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.IntegrationTests;

[SupportedOSPlatform("linux")]
public sealed class LinuxFileLockIntegrationTests
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SharedWrapperHandoffsRemainExclusiveAndReusable()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        PlatformResourceName resource = PlatformResourceName.Create(
            $"sms-linux-file-lock-handoff-{Guid.NewGuid():N}");
        string path = resource.LinuxLifecycleLockPath;
        LinuxFileLock? shared = null;
        try
        {
            Assert.Equal(StoreStatus.Success, LinuxFileLock.TryOpen(path, out shared));
            Assert.NotNull(shared);
            LinuxFileLock fileLock = shared;
            var start = new ManualResetEventSlim(false);
            int active = 0;
            int maximumActive = 0;
            int successfulHandoffs = 0;
            Task[] workers = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    for (var iteration = 0; iteration < 1_000; iteration++)
                    {
                        Assert.Equal(
                            StoreStatus.Success,
                            fileLock.TryAcquire(new StoreWaitOptions(TimeSpan.FromSeconds(5))));
                        int entered = Interlocked.Increment(ref active);
                        UpdateMaximum(ref maximumActive, entered);
                        Thread.Yield();
                        Interlocked.Decrement(ref active);
                        fileLock.Release();
                        Interlocked.Increment(ref successfulHandoffs);
                    }
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(4_000, Volatile.Read(ref successfulHandoffs));
            Assert.Equal(1, Volatile.Read(ref maximumActive));

            Assert.Equal(StoreStatus.Success, fileLock.TryAcquire(StoreWaitOptions.NoWait));
            fileLock.Release();
        }
        finally
        {
            shared?.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void OpenFileDescriptionLocksContendAcrossAssemblyLoadContexts()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        PlatformResourceName resource = PlatformResourceName.Create(
            $"sms-linux-file-lock-alc-{Guid.NewGuid():N}");
        string path = resource.LinuxSynchronizationPath;
        var firstContext = new AssemblyLoadContext("sms-lock-first", isCollectible: true);
        var secondContext = new AssemblyLoadContext("sms-lock-second", isCollectible: true);
        IDisposable? first = null;
        IDisposable? second = null;
        try
        {
            (string firstStatus, IDisposable? firstLock) =
                TryAcquireInContext(firstContext, path, "Infinite");
            first = firstLock;
            Assert.Equal(nameof(StoreStatus.Success), firstStatus);
            Assert.NotNull(first);

            (string secondStatus, IDisposable? secondLock) =
                TryAcquireInContext(secondContext, path, "NoWait");
            second = secondLock;
            Assert.Equal(nameof(StoreStatus.StoreBusy), secondStatus);
            Assert.Null(second);
            Assert.Equal("RESULT StoreBusy", RunForeignProbe(path));

            first.Dispose();
            first = null;

            (secondStatus, second) = TryAcquireInContext(secondContext, path, "NoWait");
            Assert.Equal(nameof(StoreStatus.Success), secondStatus);
            Assert.NotNull(second);
            second.Dispose();
            second = null;

            Assert.Equal("RESULT Success", RunForeignProbe(path));
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
            firstContext.Unload();
            secondContext.Unload();
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ManagedAndNativeOfdDescriptorsExcludeEachOtherInsideOneProcess()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        PlatformResourceName resource = PlatformResourceName.Create(
            $"sms-linux-file-lock-native-{Guid.NewGuid():N}");
        string path = resource.LinuxSynchronizationPath;
        NativeOfdLock? nativeHolder = null;
        LinuxFileLock? managedHolder = null;
        try
        {
            Assert.True(NativeOfdLock.TryAcquire(path, out nativeHolder));
            Assert.NotNull(nativeHolder);
            Assert.Equal(
                StoreStatus.StoreBusy,
                LinuxFileLock.TryAcquire(path, StoreWaitOptions.NoWait, out managedHolder));
            Assert.Null(managedHolder);
            Assert.Equal("RESULT StoreBusy", RunForeignProbe(path));

            nativeHolder.Dispose();
            nativeHolder = null;

            Assert.Equal(
                StoreStatus.Success,
                LinuxFileLock.TryAcquire(path, StoreWaitOptions.NoWait, out managedHolder));
            Assert.NotNull(managedHolder);
            Assert.False(NativeOfdLock.TryAcquire(path, out nativeHolder));
            Assert.Null(nativeHolder);
            Assert.Equal("RESULT StoreBusy", RunForeignProbe(path));

            managedHolder.Dispose();
            managedHolder = null;
            Assert.Equal("RESULT Success", RunForeignProbe(path));
        }
        finally
        {
            nativeHolder?.Dispose();
            managedHolder?.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentFinalCloseAndReopenKeepOnePersistentLockRendezvous()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string name = $"sms-linux-lock-rendezvous-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 4,
            maxValueBytes: 64,
            maxDescriptorBytes: 16,
            maxKeyBytes: 16,
            leaseRecordCount: 8);
        PlatformResourceName resource = PlatformResourceName.Create(name);
        MemoryStore? current = null;
        try
        {
            Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, out current));
            Assert.NotNull(current);

            for (var iteration = 0; iteration < 3; iteration++)
            {
                MemoryStore closing = current;
                current = null;
                using var start = new ManualResetEventSlim(false);
                MemoryStore? reopened = null;
                StoreOpenStatus reopenStatus = StoreOpenStatus.MappingFailed;
                Task close = Task.Run(() =>
                {
                    start.Wait();
                    closing.Dispose();
                });
                Task reopen = Task.Run(() =>
                {
                    start.Wait();
                    reopenStatus = MemoryStore.TryCreateOrOpen(options, out reopened);
                });

                start.Set();
                await Task.WhenAll(close, reopen).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(StoreOpenStatus.Success, reopenStatus);
                current = Assert.IsType<MemoryStore>(reopened);
                Assert.True(File.Exists(resource.LinuxSynchronizationPath));
                byte[] key = [(byte)(iteration + 1)];

                Assert.Equal(
                    StoreStatus.Success,
                    LinuxFileLock.TryAcquire(
                        resource.LinuxSynchronizationPath,
                        StoreWaitOptions.NoWait,
                        out LinuxFileLock? holder));
                using (Assert.IsType<LinuxFileLock>(holder))
                {
                    Assert.Equal("RESULT StoreBusy", RunForeignProbe(resource.LinuxSynchronizationPath));
                    Assert.Equal(
                        StoreStatus.StoreBusy,
                        current.TryPublish(key, [42], default, StoreWaitOptions.NoWait));
                }

                Assert.Equal(
                    StoreStatus.Success,
                    current.TryPublish(key, [42], default, StoreWaitOptions.NoWait));
                Assert.Equal(
                    StoreStatus.Success,
                    current.TryRemove(key, StoreWaitOptions.NoWait));
            }

            current.Dispose();
            current = null;
            Assert.True(File.Exists(resource.LinuxSynchronizationPath));
            Assert.Equal("RESULT Success", RunForeignProbe(resource.LinuxSynchronizationPath));
        }
        finally
        {
            current?.Dispose();
            File.Delete(resource.LinuxSynchronizationPath);
        }
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("lifecycle")]
    [Trait("Category", "Integration")]
    public void DisposingLocalContenderDoesNotReleaseForeignProcessExclusion(string lockKind)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        PlatformResourceName resource = PlatformResourceName.Create(
            $"sms-linux-file-lock-{lockKind}-{Guid.NewGuid():N}");
        string path = lockKind switch
        {
            "operation" => resource.LinuxSynchronizationPath,
            "lifecycle" => resource.LinuxLifecycleLockPath,
            _ => throw new ArgumentOutOfRangeException(nameof(lockKind))
        };

        LinuxFileLock? holder = null;
        LinuxFileLock? contender = null;
        LinuxFileLock? sameThreadContender = null;
        try
        {
            Assert.Equal(
                StoreStatus.Success,
                LinuxFileLock.TryAcquire(path, StoreWaitOptions.Infinite, out holder));
            Assert.NotNull(holder);

            Assert.Equal(
                StoreStatus.StoreBusy,
                LinuxFileLock.TryAcquire(path, StoreWaitOptions.NoWait, out sameThreadContender));
            Assert.Null(sameThreadContender);

            StoreStatus contenderStatus = StoreStatus.UnknownFailure;
            Exception? contenderFailure = null;
            var contenderThread = new Thread(() =>
            {
                try
                {
                    contenderStatus = LinuxFileLock.TryAcquire(
                        path,
                        new StoreWaitOptions(TimeSpan.FromMilliseconds(100)),
                        out contender);
                }
                catch (Exception exception)
                {
                    contenderFailure = exception;
                }
            })
            {
                IsBackground = true,
                Name = "SharedMemoryStore local file-lock contender"
            };

            contenderThread.Start();
            Assert.True(contenderThread.Join(TimeSpan.FromSeconds(5)), "The local contender did not finish.");
            Assert.Null(contenderFailure);
            Assert.Equal(StoreStatus.StoreBusy, contenderStatus);
            Assert.Null(contender);

            Assert.Equal("RESULT StoreBusy", RunForeignProbe(path));

            holder.Dispose();
            holder = null;

            Assert.Equal("RESULT Success", RunForeignProbe(path));
        }
        finally
        {
            sameThreadContender?.Dispose();
            contender?.Dispose();
            holder?.Dispose();
            File.Delete(path);
        }
    }

    private static string RunForeignProbe(string path)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(LocateAgentAssembly());
        startInfo.ArgumentList.Add("linux-file-lock-probe");
        startInfo.ArgumentList.Add(path);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the Linux file-lock probe.");
        if (!process.WaitForExit((int)AgentTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Linux file-lock probe did not finish.");
        }

        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();
        Assert.True(
            process.ExitCode == 0,
            $"Linux file-lock probe exited {process.ExitCode}. stdout={output} stderr={error}");
        return output;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int observed;
        while (candidate > (observed = Volatile.Read(ref target))
            && Interlocked.CompareExchange(ref target, candidate, observed) != observed)
        {
        }
    }

    private static (string Status, IDisposable? FileLock) TryAcquireInContext(
        AssemblyLoadContext context,
        string path,
        string waitProperty)
    {
        Assembly assembly = context.LoadFromAssemblyPath(typeof(MemoryStore).Assembly.Location);
        Type lockType = assembly.GetType(
            "SharedMemoryStore.Interop.LinuxFileLock",
            throwOnError: true)!;
        Type waitType = assembly.GetType(
            "SharedMemoryStore.StoreWaitOptions",
            throwOnError: true)!;
        object wait = waitType.GetProperty(
            waitProperty,
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        MethodInfo method = lockType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate =>
                candidate.Name == "TryAcquire"
                && candidate.GetParameters().Length == 3);
        object?[] arguments = [path, wait, null];
        object status = method.Invoke(null, arguments)!;
        return (status.ToString()!, arguments[2] as IDisposable);
    }

    private static string LocateAgentAssembly()
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
            "tests",
            "SharedMemoryStore.LockFreeAgent",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LockFreeAgent.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Lock-free agent was not built.", path);
    }

    private sealed class NativeOfdLock : IDisposable
    {
        private const int OpenFileDescriptionSetLock = 37;
        private readonly FileStream _stream;
        private bool _disposed;

        private NativeOfdLock(FileStream stream)
        {
            _stream = stream;
        }

        internal static bool TryAcquire(string path, out NativeOfdLock? holder)
        {
            holder = null;
            LinuxSharedMemoryDirectory.EnsureExists(Path.GetDirectoryName(path) ?? ".");
            var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete,
                UnixCreateMode = LinuxSharedMemoryDirectory.PrivateFileMode
            });
            var request = NativeFlock.Create(type: 1);
            if (Fcntl(stream.SafeFileHandle, OpenFileDescriptionSetLock, ref request) != 0)
            {
                stream.Dispose();
                return false;
            }

            holder = new NativeOfdLock(stream);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var request = NativeFlock.Create(type: 2);
            _ = Fcntl(_stream.SafeFileHandle, OpenFileDescriptionSetLock, ref request);
            _stream.Dispose();
        }

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int Fcntl(
            SafeFileHandle fileDescriptor,
            int command,
            ref NativeFlock request);

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct NativeFlock
        {
            [FieldOffset(0)]
            internal short Type;

            [FieldOffset(2)]
            internal short Whence;

            [FieldOffset(8)]
            internal long Start;

            [FieldOffset(16)]
            internal long Length;

            [FieldOffset(24)]
            internal int ProcessId;

            internal static NativeFlock Create(short type) => new()
            {
                Type = type,
                Whence = 0,
                Start = 0,
                Length = 1,
                ProcessId = 0
            };
        }
    }
}
