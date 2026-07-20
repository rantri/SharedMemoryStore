using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.IntegrationTests;

[Collection("Linux owner artifact isolation")]
public sealed class LinuxOwnerArtifactIsolationIntegrationTests
{
    private const int UnrelatedRendezvousFileCount = 12_000;
    private const int ConcurrentColdOpenCount = 64;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnrelatedFlatRendezvousGrowthDoesNotConsumeFiniteColdOpenBudgets()
    {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string root = LinuxSharedMemoryDirectory.GetPath();
        LinuxSharedMemoryDirectory.EnsureExists(root);
        string noisePrefix = "sms-owner-artifact-noise-" + Guid.NewGuid().ToString("N");
        var noisePaths = new List<string>(UnrelatedRendezvousFileCount);
        var resources = new List<PlatformResourceName>(ConcurrentColdOpenCount);
        try
        {
            for (var index = 0; index < UnrelatedRendezvousFileCount; index++)
            {
                string path = Path.Combine(root, $"{noisePrefix}-{index:D5}.lifecycle");
                using (File.Create(path))
                {
                }

                noisePaths.Add(path);
            }

            var tasks = Enumerable.Range(0, ConcurrentColdOpenCount).Select(index => Task.Run(() =>
            {
                string name = $"sms-owner-artifact-isolation-{Guid.NewGuid():N}-{index}";
                PlatformResourceName resource = PlatformResourceName.Create(name);
                lock (resources)
                {
                    resources.Add(resource);
                }

                SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
                    name,
                    slotCount: 4,
                    maxValueBytes: 64,
                    maxDescriptorBytes: 8,
                    maxKeyBytes: 8,
                    leaseRecordCount: 8,
                    participantRecordCount: 4,
                    OpenMode.CreateNew);
                StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
                    options,
                    new StoreWaitOptions(TimeSpan.FromMilliseconds(500)),
                    out MemoryStore? store);
                store?.Dispose();
                return status;
            })).ToArray();

            StoreOpenStatus[] statuses = await Task.WhenAll(tasks);
            Assert.All(statuses, status => Assert.Equal(StoreOpenStatus.Success, status));
        }
        finally
        {
            foreach (string path in noisePaths)
            {
                File.Delete(path);
            }

            foreach (PlatformResourceName resource in resources)
            {
                File.Delete(resource.LinuxRegionPath);
                File.Delete(resource.LinuxOwnersPath);
                File.Delete(resource.LinuxOwnersPath + ".tmp");
                File.Delete(resource.LinuxSynchronizationPath);
                File.Delete(resource.LinuxLifecycleLockPath);
                string artifacts = LinuxOwnerArtifactStore.GetDirectory(resource.LinuxOwnersPath);
                if (Directory.Exists(artifacts))
                {
                    Directory.Delete(artifacts, recursive: true);
                }
            }
        }
    }
}

[CollectionDefinition("Linux owner artifact isolation", DisableParallelization = true)]
public sealed class LinuxOwnerArtifactIsolationCollection;
