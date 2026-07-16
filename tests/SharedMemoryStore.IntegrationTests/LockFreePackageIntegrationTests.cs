using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using SharedMemoryStore.IntegrationTests.TestSupport;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreePackageIntegrationTests
{
    private static readonly Lazy<ReleasedV102Client> ReleasedClient =
        new(static () => ReleasedV102Client.Load(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static TheoryData<string, OpenMode> ReleasedOpenMatrix => new()
    {
        { "smaller", OpenMode.CreateNew },
        { "smaller", OpenMode.OpenExisting },
        { "smaller", OpenMode.CreateOrOpen },
        { "equal", OpenMode.CreateNew },
        { "equal", OpenMode.OpenExisting },
        { "equal", OpenMode.CreateOrOpen },
        { "oversized", OpenMode.CreateNew },
        { "oversized", OpenMode.OpenExisting },
        { "oversized", OpenMode.CreateOrOpen }
    };

    [Theory]
    [MemberData(nameof(ReleasedOpenMatrix))]
    [Trait("Category", "PackageConsumption")]
    public void ReleasedV102PackageFailsClosedOnEveryV2ViewAndOpenMode(
        string requestedView,
        OpenMode openMode)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-packed-v102-reject-{requestedView}-{openMode}-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions options = LockFreeOptions(
            name,
            OpenMode.CreateNew,
            participantRecordCount: 2,
            slotCount: 32,
            maxValueBytes: 1024,
            maxDescriptorBytes: 64,
            maxKeyBytes: 32,
            leaseRecordCount: 32);
        using MemoryStore store = IntegrationStoreFactory.Create(options);

        long releasedMinimum = ReleasedClient.Value.MinimumRequiredBytes;
        Assert.True(options.TotalBytes > releasedMinimum + 8);
        long requestedBytes = requestedView switch
        {
            "smaller" => Math.Max(releasedMinimum, options.TotalBytes - 8),
            "equal" => options.TotalBytes,
            "oversized" => checked(options.TotalBytes + 4096),
            _ => throw new ArgumentOutOfRangeException(nameof(requestedView))
        };

        string status = ReleasedClient.Value.TryOpen(name, openMode.ToString(), requestedBytes);

        if (openMode == OpenMode.CreateNew)
        {
            Assert.Equal(nameof(StoreOpenStatus.AlreadyExists), status);
        }
        else if (requestedView == "oversized" && OperatingSystem.IsWindows())
        {
            Assert.Contains(
                status,
                new[]
                {
                    nameof(StoreOpenStatus.AccessDenied),
                    nameof(StoreOpenStatus.IncompatibleLayout)
                });
        }
        else
        {
            Assert.Equal(nameof(StoreOpenStatus.IncompatibleLayout), status);
        }

        Assert.Equal(StoreProfile.LockFree, store.Profile);
        Assert.Equal(2, store.ProtocolInfo.LayoutMajorVersion);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [2, 3, 4]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        using (lease)
        {
            Assert.Equal(new byte[] { 2, 3, 4 }, lease.ValueSpan.ToArray());
        }
    }

    [Fact]
    [Trait("Category", "PackageConsumption")]
    public void ReleasedV102RejectionPreservesLinuxV2LiveOwnerSidecar()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string name = $"sms-packed-v102-linux-owner-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions options = LockFreeOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        PlatformResourceName resourceName = PlatformResourceName.Create(name);
        using MemoryStore store = IntegrationStoreFactory.Create(options);
        string[] before = ReadNonEmptyLines(resourceName.LinuxOwnersPath);

        string status = ReleasedClient.Value.TryOpen(
            name,
            nameof(OpenMode.OpenExisting),
            options.TotalBytes);

        Assert.Equal(nameof(StoreOpenStatus.IncompatibleLayout), status);
        Assert.True(File.Exists(resourceName.LinuxRegionPath));
        Assert.Equal(
            before.OrderBy(static line => line, StringComparer.Ordinal),
            ReadNonEmptyLines(resourceName.LinuxOwnersPath).OrderBy(static line => line, StringComparer.Ordinal));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [9]));
    }

    [Fact]
    [Trait("Category", "PackageConsumption")]
    public void ExplicitV2ParticipantCapacityIsConsumedPerHandleAndReusableAfterClose()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-package-participants-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = LockFreeOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions open = LockFreeOptions(name, OpenMode.OpenExisting, participantRecordCount: 2);

        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(create, out var first));
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(open, out var second));
        Assert.Equal(StoreOpenStatus.ParticipantTableFull, MemoryStore.TryCreateOrOpen(open, out var rejected));
        Assert.Null(rejected);

        try
        {
            second!.Dispose();
            second = null;
            Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(open, out var replacement));
            using (replacement)
            {
                Assert.NotNull(replacement);
                Assert.Equal(StoreProfile.LockFree, replacement.Profile);
            }
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "PackageConsumption")]
    public void SameNameUpgradeAndRollbackRequireCloseRecreateAndRepublish()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-package-upgrade-rollback-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions legacyCreate = LegacyOptions(name, OpenMode.CreateNew);
        SharedMemoryStoreOptions legacyOpen = LegacyOptions(name, OpenMode.OpenExisting);
        SharedMemoryStoreOptions v2Create = LockFreeOptions(name, OpenMode.CreateNew, participantRecordCount: 2);
        SharedMemoryStoreOptions v2Open = LockFreeOptions(name, OpenMode.OpenExisting, participantRecordCount: 2);

        using (MemoryStore legacy = IntegrationStoreFactory.Create(legacyCreate))
        {
            Assert.Equal(StoreProfile.Legacy, legacy.Profile);
            Assert.Equal(StoreStatus.Success, legacy.TryPublish([1], [10]));
            Assert.Equal(StoreOpenStatus.IncompatibleLayout, MemoryStore.TryCreateOrOpen(v2Open, out var incompatible));
            Assert.Null(incompatible);
        }

        using (MemoryStore v2 = IntegrationStoreFactory.Create(v2Create))
        {
            Assert.Equal(StoreProfile.LockFree, v2.Profile);
            Assert.Equal(StoreStatus.NotFound, v2.TryAcquire([1], out _));
            Assert.Equal(StoreStatus.Success, v2.TryPublish([1], [20]));
            Assert.Equal(StoreOpenStatus.IncompatibleLayout, MemoryStore.TryCreateOrOpen(legacyOpen, out var incompatible));
            Assert.Null(incompatible);
        }

        using MemoryStore rolledBack = IntegrationStoreFactory.Create(legacyCreate);
        Assert.Equal(StoreProfile.Legacy, rolledBack.Profile);
        Assert.Equal(StoreStatus.NotFound, rolledBack.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, rolledBack.TryPublish([1], [30]));
    }

    [Fact]
    [Trait("Category", "PackageConsumption")]
    public void DefaultAndLegacyHelpersNeverAutoSelectAnExistingV2Mapping()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        Assert.Equal(StoreProfile.Legacy, new SharedMemoryStoreOptions().Profile);
        Assert.Equal(StoreProfile.Legacy, LegacyOptions("unused", OpenMode.CreateOrOpen).Profile);
        Assert.Equal(StoreProfile.LockFree, LockFreeOptions("unused-v2", OpenMode.CreateOrOpen, 1).Profile);

        string name = $"sms-package-default-profile-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions v2 = LockFreeOptions(
            name,
            OpenMode.CreateNew,
            participantRecordCount: 2,
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 4,
            leaseRecordCount: 2);
        SharedMemoryStoreOptions oversizedDefaultLegacy = LegacyOptions(
            name,
            OpenMode.OpenExisting,
            slotCount: 128,
            maxValueBytes: 32 * 1024,
            maxDescriptorBytes: 256,
            maxKeyBytes: 128,
            leaseRecordCount: 256);
        using MemoryStore store = IntegrationStoreFactory.Create(v2);

        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(oversizedDefaultLegacy, out var incompatible);

        incompatible?.Dispose();
        Assert.Equal(StoreOpenStatus.IncompatibleLayout, status);
        Assert.Null(incompatible);
        Assert.Equal(StoreProfile.LockFree, store.Profile);
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static SharedMemoryStoreOptions LockFreeOptions(
        string name,
        OpenMode openMode,
        int participantRecordCount,
        int slotCount = 4,
        int maxValueBytes = 64,
        int maxDescriptorBytes = 8,
        int maxKeyBytes = 8,
        int leaseRecordCount = 8) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount,
            openMode);

    private static SharedMemoryStoreOptions LegacyOptions(
        string name,
        OpenMode openMode,
        int slotCount = 4,
        int maxValueBytes = 64,
        int maxDescriptorBytes = 8,
        int maxKeyBytes = 8,
        int leaseRecordCount = 8) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            openMode);

    private static string[] ReadNonEmptyLines(string path) =>
        File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .ToArray();

    private sealed class ReleasedV102Client
    {
        private readonly Type _optionsType;
        private readonly Type _openModeType;
        private readonly MethodInfo _tryCreateOrOpen;

        private ReleasedV102Client(Assembly assembly)
        {
            _optionsType = RequireType(assembly, "SharedMemoryStore.SharedMemoryStoreOptions");
            _openModeType = RequireType(assembly, "SharedMemoryStore.OpenMode");
            Type storeType = RequireType(assembly, "SharedMemoryStore.MemoryStore");
            _tryCreateOrOpen = storeType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "TryCreateOrOpen"
                        && parameters.Length == 2
                        && parameters[0].ParameterType == _optionsType.MakeByRefType()
                        && parameters[1].IsOut;
                });

            MethodInfo calculate = _optionsType.GetMethod(
                "CalculateRequiredBytes",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)],
                modifiers: null) ?? throw new MissingMethodException(_optionsType.FullName, "CalculateRequiredBytes");
            MinimumRequiredBytes = (long)calculate.Invoke(null, [1, 1, 0, 1, 1])!;
        }

        public long MinimumRequiredBytes { get; }

        public static ReleasedV102Client Load()
        {
            string packagePath = ResolvePackagePath();
            string extractionDirectory = Path.Combine(
                Path.GetTempPath(),
                $"sms-v102-package-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractionDirectory);
            string assemblyPath = Path.Combine(extractionDirectory, "SharedMemoryStore.dll");

            using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            {
                ZipArchiveEntry entry = archive.GetEntry("lib/net10.0/SharedMemoryStore.dll")
                    ?? throw new InvalidDataException($"Package '{packagePath}' has no net10.0 assembly.");
                entry.ExtractToFile(assemblyPath);
            }

            var loadContext = new AssemblyLoadContext(
                $"SharedMemoryStore-v1.0.2-{Guid.NewGuid():N}",
                isCollectible: false);
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            Assert.Equal(new Version(1, 0, 2, 0), assembly.GetName().Version);
            return new ReleasedV102Client(assembly);
        }

        public string TryOpen(string name, string openMode, long totalBytes)
        {
            object options = Activator.CreateInstance(_optionsType)
                ?? throw new InvalidOperationException("Could not create released options instance.");
            SetProperty(options, "Name", name);
            SetProperty(options, "OpenMode", Enum.Parse(_openModeType, openMode));
            SetProperty(options, "TotalBytes", totalBytes);
            SetProperty(options, "SlotCount", 1);
            SetProperty(options, "MaxValueBytes", 1);
            SetProperty(options, "MaxDescriptorBytes", 0);
            SetProperty(options, "MaxKeyBytes", 1);
            SetProperty(options, "LeaseRecordCount", 1);

            object?[] arguments = [options, null];
            object result = _tryCreateOrOpen.Invoke(null, arguments)
                ?? throw new InvalidOperationException("Released open returned no status.");
            if (arguments[1] is IDisposable accidentallyOpened)
            {
                accidentallyOpened.Dispose();
            }

            return result.ToString() ?? string.Empty;
        }

        private static string ResolvePackagePath()
        {
            var candidates = new List<string?>
            {
                Environment.GetEnvironmentVariable("SMS_V102_PACKAGE_PATH"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget",
                    "packages",
                    "sharedmemorystore",
                    "1.0.2",
                    "sharedmemorystore.1.0.2.nupkg"),
                Path.Combine(FindRepositoryRoot(), "artifacts", "package", "SharedMemoryStore.1.0.2.nupkg"),
                Path.Combine(
                    FindRepositoryRoot(),
                    "artifacts",
                    "docker-consumer",
                    "local-packages",
                    "SharedMemoryStore.1.0.2.nupkg")
            };

            string? existing = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (existing is not null)
            {
                return Path.GetFullPath(existing);
            }

            return RestorePublishedPackage();
        }

        private static string RestorePublishedPackage()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"sms-v102-restore-{Environment.ProcessId}-{Guid.NewGuid():N}");
            string packages = Path.Combine(root, "packages");
            Directory.CreateDirectory(root);
            string project = Path.Combine(root, "Restore.csproj");
            File.WriteAllText(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="SharedMemoryStore" Version="1.0.2" /></ItemGroup>
                </Project>
                """);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(
                    "dotnet",
                    $"restore \"{project}\" --packages \"{packages}\"")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Timed out restoring released package 1.0.2.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0, output + Environment.NewLine + error);

            string package = Path.Combine(
                packages,
                "sharedmemorystore",
                "1.0.2",
                "sharedmemorystore.1.0.2.nupkg");
            Assert.True(File.Exists(package), $"Restore did not produce '{package}'.");
            return package;
        }

        private static Type RequireType(Assembly assembly, string fullName) =>
            assembly.GetType(fullName, throwOnError: true, ignoreCase: false)!;

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(target.GetType().FullName, name);
            property.SetValue(target, value);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
