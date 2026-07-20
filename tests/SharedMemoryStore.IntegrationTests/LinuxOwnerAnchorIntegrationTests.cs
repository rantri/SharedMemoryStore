using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharedMemoryStore.Interop;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

[SupportedOSPlatform("linux")]
public sealed class LinuxOwnerAnchorIntegrationTests
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    [Trait("Category", "Integration")]
    public void ColdOpenReclaimsUnlockedOrphanWithoutDisturbingLiveOrUncertainAnchors()
    {
        if (!IsQualifiedLinuxHost())
        {
            return;
        }

        string name = "sms-owner-anchor-sweep-" + Guid.NewGuid().ToString("N");
        PlatformResourceName resource = PlatformResourceName.Create(name);
        SharedMemoryStoreOptions createOptions = Options(name, OpenMode.CreateNew);
        SharedMemoryStoreOptions openOptions = Options(name, OpenMode.OpenExisting);
        Store? sibling = null;
        Store? opener = null;
        LinuxOwnerAnchor? lockedOrphan = null;
        string? ambiguousPath = null;
        string? ambiguousTarget = null;
        string? malformedPath = null;
        string? fifoPath = null;
        try
        {
            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(createOptions, out sibling));
            Assert.NotNull(sibling);
            string siblingOwner = Assert.Single(ReadOwnerLines(resource.LinuxOwnersPath));
            Guid siblingToken = ParseOwnerToken(siblingOwner);
            string siblingAnchorPath = LinuxOwnerAnchor.GetPath(resource.LinuxOwnersPath, siblingToken);

            Guid referencedStaleToken = Guid.NewGuid();
            string referencedStalePath = CreateUnlockedAnchor(
                resource.LinuxOwnersPath,
                referencedStaleToken);
            string referencedStaleOwner = string.Join(
                ':',
                int.MaxValue.ToString(CultureInfo.InvariantCulture),
                "proc-stale-tail",
                referencedStaleToken.ToString("N"));
            File.AppendAllText(
                resource.LinuxOwnersPath,
                referencedStaleOwner + Environment.NewLine);
            File.SetUnixFileMode(
                resource.LinuxOwnersPath,
                LinuxSharedMemoryDirectory.PrivateFileMode);

            Guid staleToken = Guid.NewGuid();
            string stalePath = CreateUnlockedAnchor(resource.LinuxOwnersPath, staleToken);
            Guid lockedToken = Guid.NewGuid();
            lockedOrphan = LinuxOwnerAnchor.Create(resource.LinuxOwnersPath, lockedToken);
            string lockedPath = lockedOrphan.AnchorPath;
            Guid ambiguousToken = Guid.NewGuid();
            ambiguousPath = LinuxOwnerAnchor.GetPath(resource.LinuxOwnersPath, ambiguousToken);
            ambiguousTarget = resource.LinuxOwnersPath + ".anchor-test-target";
            File.WriteAllBytes(ambiguousTarget, []);
            File.CreateSymbolicLink(ambiguousPath, ambiguousTarget);
            malformedPath = Path.Combine(
                LinuxOwnerArtifactStore.GetDirectory(resource.LinuxOwnersPath),
                "anchor.not-a-valid-owner-token");
            File.WriteAllBytes(malformedPath, []);
            fifoPath = CreateFifoAnchor(resource.LinuxOwnersPath, Guid.NewGuid());

            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(openOptions, out opener));
            Assert.NotNull(opener);

            Assert.False(File.Exists(stalePath));
            // Once the first authoritative live witness is found, an attach
            // preserves every committed tail record. Consequently the sweep
            // may remove the truly unreferenced orphan above, but must defer
            // this referenced stale anchor to a full close/no-live scan.
            Assert.True(File.Exists(referencedStalePath));
            Assert.Contains(
                referencedStaleOwner,
                ReadOwnerLines(resource.LinuxOwnersPath),
                StringComparer.Ordinal);
            Assert.True(File.Exists(siblingAnchorPath));
            Assert.Contains(siblingOwner, ReadOwnerLines(resource.LinuxOwnersPath), StringComparer.Ordinal);
            Assert.True(File.Exists(lockedPath));
            Assert.True(File.Exists(ambiguousPath));
            Assert.True(File.Exists(ambiguousTarget));
            Assert.True(File.Exists(malformedPath));
            Assert.True(File.Exists(fifoPath));
            Assert.Equal(
                LinuxOwnerAnchorState.Locked,
                LinuxOwnerAnchor.Probe(
                    resource.LinuxOwnersPath,
                    lockedToken,
                    honorLocalRegistry: false));
            Assert.Equal(
                LinuxOwnerAnchorState.Ambiguous,
                LinuxOwnerAnchor.Probe(
                    resource.LinuxOwnersPath,
                    ambiguousToken,
                    honorLocalRegistry: false));

            Assert.Equal(StoreStatus.Success, sibling!.TryPublish([0x55], [0x7A]));
            Assert.Equal(StoreStatus.Success, sibling.TryAcquire([0x55], out ValueLease lease));
            Assert.Equal(0x7A, lease.ValueSpan[0]);
            Assert.Equal(StoreStatus.Success, lease.Release());
            Assert.Equal(StoreStatus.Success, sibling.TryRemove([0x55]));
        }
        finally
        {
            opener?.Dispose();
            sibling?.Dispose();
            lockedOrphan?.Dispose();
            DeleteIfExists(ambiguousPath);
            DeleteIfExists(malformedPath);
            DeleteIfExists(fifoPath);
            DeleteIfExists(ambiguousTarget);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForeignPidViewCannotDeleteLiveMappingAndCrashUnlocksAnchorForRecreation()
    {
        if (!IsQualifiedLinuxHost())
        {
            return;
        }

        string name = "sms-owner-anchor-pidns-" + Guid.NewGuid().ToString("N");
        PlatformResourceName resource = PlatformResourceName.Create(name);
        SharedMemoryStoreOptions createOptions = Options(name, OpenMode.CreateNew);
        SharedMemoryStoreOptions openOptions = Options(name, OpenMode.OpenExisting);
        Store? original = null;
        Store? survivor = null;
        Store? recreated = null;
        using Process child = StartOwner(name, keyValue: 77);
        try
        {
            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(createOptions, out original));
            Assert.NotNull(original);

            // The helper starts only after the mapping exists.
            child.Start();
            int childPid = await ReadReadyProcessIdAsync(child);
            string[] owners = ReadOwnerLines(resource.LinuxOwnersPath);
            Assert.Equal(2, owners.Length);
            string childOwner = Assert.Single(owners, owner => ParseOwnerProcessId(owner) == childPid);
            string[] childParts = childOwner.Split(':', 3);
            Assert.Equal(3, childParts.Length);
            Assert.True(Guid.TryParseExact(childParts[2], "N", out Guid childToken));
            string anchorPath = LinuxOwnerAnchor.GetPath(resource.LinuxOwnersPath, childToken);
            Assert.Equal(
                LinuxOwnerAnchorState.Locked,
                LinuxOwnerAnchor.Probe(
                    resource.LinuxOwnersPath,
                    childToken,
                    honorLocalRegistry: false));

            string foreignView = string.Join(':', int.MaxValue, childParts[1], childParts[2]);
            RewriteOwner(resource, childOwner, foreignView);

            original.Dispose();
            original = null;
            Assert.True(File.Exists(resource.LinuxRegionPath));
            Assert.True(File.Exists(anchorPath));

            // PID/start-token probing alone sees no such process. The locked
            // anchor is authoritative and preserves the live child's mapping.
            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(openOptions, out survivor));
            Assert.NotNull(survivor);
            Assert.Equal(StoreStatus.Success, survivor.TryAcquire(BitConverter.GetBytes(77), out ValueLease lease));
            Assert.Equal(77, lease.ValueSpan[0]);
            Assert.Equal(StoreStatus.Success, lease.Release());
            survivor.Dispose();
            survivor = null;

            Kill(child);
            Assert.Equal(
                LinuxOwnerAnchorState.Unlocked,
                LinuxOwnerAnchor.Probe(
                    resource.LinuxOwnersPath,
                    childToken,
                    honorLocalRegistry: false));

            // flock is released by process teardown. The next cold lifecycle
            // operation drops the stale line and anchor before recreating.
            Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(createOptions, out recreated));
            Assert.NotNull(recreated);
            Assert.False(File.Exists(anchorPath));
            Assert.Single(ReadOwnerLines(resource.LinuxOwnersPath));
        }
        finally
        {
            Kill(child);
            recreated?.Dispose();
            survivor?.Dispose();
            original?.Dispose();
        }
    }

    private static Process StartOwner(string name, int keyValue)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(LocateOwnerToolAssembly());
        foreach (string argument in new[]
        {
            "live",
            name,
            "4",
            "64",
            "8",
            "8",
            "8",
            keyValue.ToString(CultureInfo.InvariantCulture)
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static async Task<int> ReadReadyProcessIdAsync(Process process)
    {
        string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(AgentTimeout);
        if (line is null || !line.StartsWith("READY ", StringComparison.Ordinal))
        {
            string error = await process.StandardError.ReadToEndAsync();
            throw new Xunit.Sdk.XunitException(
                "Owner helper did not become ready. stdout=" + line + " stderr=" + error);
        }

        return int.Parse(line.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static void RewriteOwner(
        PlatformResourceName resource,
        string expectedOwner,
        string replacementOwner)
    {
        Assert.Equal(
            StoreStatus.Success,
            LinuxFileLock.TryAcquire(
                resource.LinuxLifecycleLockPath,
                StoreWaitOptions.Infinite,
                out LinuxFileLock? lifecycleLock));
        using (Assert.IsType<LinuxFileLock>(lifecycleLock))
        {
            string[] owners = ReadOwnerLines(resource.LinuxOwnersPath);
            Assert.Contains(expectedOwner, owners, StringComparer.Ordinal);
            string temporaryPath = resource.LinuxOwnersPath + ".test.tmp";
            try
            {
                File.WriteAllLines(
                    temporaryPath,
                    owners.Select(owner => string.Equals(owner, expectedOwner, StringComparison.Ordinal)
                        ? replacementOwner
                        : owner));
                File.SetUnixFileMode(temporaryPath, LinuxSharedMemoryDirectory.PrivateFileMode);
                File.Move(temporaryPath, resource.LinuxOwnersPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) => new()
    {
        Name = name,
        OpenMode = openMode,
        SlotCount = 4,
        MaxValueBytes = 64,
        MaxDescriptorBytes = 8,
        MaxKeyBytes = 8,
        LeaseRecordCount = 8,
        EnableLeaseRecovery = true,
        TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(4, 64, 8, 8, 8)
    };

    private static string[] ReadOwnerLines(string path)
    {
        Assert.True(File.Exists(path));
        return File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .ToArray();
    }

    private static int ParseOwnerProcessId(string owner) =>
        int.Parse(
            owner.AsSpan(0, owner.IndexOf(':')),
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private static Guid ParseOwnerToken(string owner)
    {
        string[] parts = owner.Split(':', 3);
        Assert.Equal(3, parts.Length);
        Assert.True(Guid.TryParseExact(parts[2], "N", out Guid token));
        return token;
    }

    private static string CreateUnlockedAnchor(string ownersPath, Guid token)
    {
        LinuxOwnerArtifactStore.EnsureDirectory(ownersPath);
        string path = LinuxOwnerAnchor.GetPath(ownersPath, token);
        File.WriteAllBytes(path, []);
        File.SetUnixFileMode(path, LinuxSharedMemoryDirectory.PrivateFileMode);
        return path;
    }

    private static string CreateFifoAnchor(string ownersPath, Guid token)
    {
        LinuxOwnerArtifactStore.EnsureDirectory(ownersPath);
        string path = LinuxOwnerAnchor.GetPath(ownersPath, token);
        int result = MkFifo(path, 0x180); // 0600
        Assert.True(result == 0, $"mkfifo failed with errno {Marshal.GetLastPInvokeError()}.");
        return path;
    }

    private static void DeleteIfExists(string? path)
    {
        if (path is not null)
        {
            File.Delete(path);
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    private static string LocateOwnerToolAssembly()
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
            "SharedMemoryStore.LeaseOwnerTool",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LeaseOwnerTool.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Lease owner helper was not built.", path);
    }

    private static bool IsQualifiedLinuxHost() =>
        OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
        }
    }
}
