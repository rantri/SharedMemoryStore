using System.Diagnostics;
using SharedMemoryStore.InteropAgent;
using SharedMemoryStore.InteropTests.TestSupport;

namespace SharedMemoryStore.InteropTests;

public sealed class RecoveryAndOwnershipTests
{
    public static TheoryData<string> Runtimes => new()
    {
        "dotnet",
        "cpp",
        "python"
    };

    [Theory]
    [MemberData(
        nameof(CoreExchangeMatrixTests.OrderedRuntimePairs),
        MemberType = typeof(CoreExchangeMatrixTests))]
    public async Task SurvivorRecoversForeignCrashedLeaseAndReservation(
        string survivorRuntime,
        string crashedRuntime)
    {
        var survivorDefinition = AgentDefinition.Resolve(survivorRuntime);
        var crashedDefinition = AgentDefinition.Resolve(crashedRuntime);
        if (!survivorDefinition.IsAvailable() || !crashedDefinition.IsAvailable())
        {
            return;
        }

        var name = $"sms-recovery-{survivorRuntime}-{crashedRuntime}-{Guid.NewGuid():N}";
        await using var survivor = await AgentProcess.StartAsync(survivorDefinition);
        InteropAssertions.Success(await survivor.SendAsync(
            "open",
            InteropAssertions.OpenArguments("survivor", name, openMode: 0)));

        var leasedKey = new byte[] { 1, 0, 8 };
        await using (var crashedLeaseOwner = await AgentProcess.StartAsync(crashedDefinition))
        {
            InteropAssertions.Success(await crashedLeaseOwner.SendAsync(
                "open",
                InteropAssertions.OpenArguments("crashed-lease-owner", name, openMode: 1)));
            InteropAssertions.Success(await crashedLeaseOwner.SendAsync("publish", new
            {
                storeId = "crashed-lease-owner",
                key = AgentProtocol.EncodeBytes(leasedKey),
                value = AgentProtocol.EncodeBytes(new byte[] { 7, 0, 6 }),
                descriptor = string.Empty
            }));
            InteropAssertions.Success(await crashedLeaseOwner.SendAsync("acquire", new
            {
                storeId = "crashed-lease-owner",
                leaseId = "abandoned-lease",
                key = AgentProtocol.EncodeBytes(leasedKey)
            }));
            await crashedLeaseOwner.CrashAsync();
        }

        var recoveredLeases = await survivor.SendAsync("recoverLeases", new
        {
            storeId = "survivor",
            recoverCurrentProcess = false
        });
        InteropAssertions.Success(recoveredLeases);
        Assert.Equal(1, recoveredLeases.Result!.Value.GetProperty("recoveredLeaseCount").GetInt32());
        InteropAssertions.Success(await survivor.SendAsync("remove", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(leasedKey)
        }));

        var reservedKey = new byte[] { 2, 0, 9 };
        await using (var crashedReservationOwner = await AgentProcess.StartAsync(crashedDefinition))
        {
            InteropAssertions.Success(await crashedReservationOwner.SendAsync(
                "open",
                InteropAssertions.OpenArguments("crashed-reservation-owner", name, openMode: 1)));
            InteropAssertions.Success(await crashedReservationOwner.SendAsync("reserve", new
            {
                storeId = "crashed-reservation-owner",
                reservationId = "abandoned-reservation",
                key = AgentProtocol.EncodeBytes(reservedKey),
                payloadLength = 6,
                descriptor = AgentProtocol.EncodeBytes(new byte[] { 3 })
            }));
            InteropAssertions.Success(await crashedReservationOwner.SendAsync("reservationWrite", new
            {
                reservationId = "abandoned-reservation",
                data = AgentProtocol.EncodeBytes(new byte[] { 5, 0, 4 })
            }));
            InteropAssertions.Success(await crashedReservationOwner.SendAsync(
                "advance",
                new { reservationId = "abandoned-reservation", byteCount = 3 }));
            await crashedReservationOwner.CrashAsync();
        }

        var recoveredReservations = await survivor.SendAsync("recoverReservations", new
        {
            storeId = "survivor",
            recoverCurrentProcess = false
        });
        InteropAssertions.Success(recoveredReservations);
        Assert.Equal(
            1,
            recoveredReservations.Result!.Value.GetProperty("recoveredReservationCount").GetInt32());
        InteropAssertions.Success(await survivor.SendAsync("publish", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(reservedKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 1, 0, 2 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await survivor.SendAsync("close", new { storeId = "survivor" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task NoWaitAndBoundedWaitObserveForeignStoreLock(string runtime)
    {
        var definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        await using var agent = await AgentProcess.StartAsync(definition);
        var name = $"sms-contention-{runtime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments("store", name, openMode: 0)));
        var key = AgentProtocol.EncodeBytes(new byte[] { 4, 0, 4 });
        var value = AgentProtocol.EncodeBytes(new byte[] { 8, 0, 8 });

        using (await ForeignStoreLock.AcquireAsync(name))
        {
            InteropAssertions.Status(await agent.SendAsync("publish", new
            {
                storeId = "store",
                key,
                value,
                descriptor = string.Empty,
                timeoutMs = 0
            }), 21, "StoreBusy");

            var stopwatch = Stopwatch.StartNew();
            var bounded = await agent.SendAsync("publish", new
            {
                storeId = "store",
                key,
                value,
                descriptor = string.Empty,
                timeoutMs = 40
            });
            stopwatch.Stop();
            InteropAssertions.Status(bounded, 21, "StoreBusy");
            Assert.InRange(stopwatch.ElapsedMilliseconds, 10, 500);
        }

        InteropAssertions.Success(await agent.SendAsync("publish", new
        {
            storeId = "store",
            key,
            value,
            descriptor = string.Empty,
            timeoutMs = 1000
        }));
        InteropAssertions.Success(await agent.SendAsync("close", new { storeId = "store" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task EveryRuntimeRejectsMismatchedExistingLayout(string runtime)
    {
        var creatorDefinition = AgentDefinition.Resolve("dotnet");
        var openerDefinition = AgentDefinition.Resolve(runtime);
        if (!creatorDefinition.IsAvailable() || !openerDefinition.IsAvailable())
        {
            return;
        }

        await using var creator = await AgentProcess.StartAsync(creatorDefinition);
        await using var opener = await AgentProcess.StartAsync(openerDefinition);
        var name = $"sms-layout-mismatch-{runtime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await creator.SendAsync(
            "open",
            InteropAssertions.OpenArguments("creator", name, openMode: 0, slotCount: 6)));
        var mismatch = await opener.SendAsync(
            "open",
            InteropAssertions.OpenArguments("mismatch", name, openMode: 1, slotCount: 5));
        InteropAssertions.Status(mismatch, 4, "IncompatibleLayout");
        InteropAssertions.Success(await creator.SendAsync("close", new { storeId = "creator" }));
    }

    [Fact]
    public async Task ThreeLinuxOwnersCleanOnlyAfterFinalClose()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var definitions = InteropAssertions.Runtimes.Select(AgentDefinition.Resolve).ToArray();
        if (definitions.Any(definition => !definition.IsAvailable()))
        {
            return;
        }

        await using var dotnet = await AgentProcess.StartAsync(definitions[0]);
        await using var cpp = await AgentProcess.StartAsync(definitions[1]);
        await using var python = await AgentProcess.StartAsync(definitions[2]);
        var name = $"sms-three-owners-{Guid.NewGuid():N}";
        InteropAssertions.Success(await dotnet.SendAsync(
            "open",
            InteropAssertions.OpenArguments("dotnet", name, openMode: 0)));
        InteropAssertions.Success(await cpp.SendAsync(
            "open",
            InteropAssertions.OpenArguments("cpp", name, openMode: 1)));
        InteropAssertions.Success(await python.SendAsync(
            "open",
            InteropAssertions.OpenArguments("python", name, openMode: 1)));

        var regionPath = ForeignStoreLock.LinuxRegionPath(name);
        var synchronizationPath = ForeignStoreLock.LinuxSynchronizationPath(name);
        var ownersPath = ForeignStoreLock.LinuxOwnersPath(name);
        var lifecyclePath = ForeignStoreLock.LinuxLifecyclePath(name);
        Assert.True(File.Exists(regionPath));
        Assert.True(File.Exists(synchronizationPath));
        Assert.Equal(3, File.ReadAllLines(ownersPath).Length);

        InteropAssertions.Success(await dotnet.SendAsync("close", new { storeId = "dotnet" }));
        Assert.True(File.Exists(regionPath));
        Assert.Equal(2, File.ReadAllLines(ownersPath).Length);
        InteropAssertions.Success(await cpp.SendAsync("close", new { storeId = "cpp" }));
        Assert.True(File.Exists(regionPath));
        Assert.Single(File.ReadAllLines(ownersPath));
        InteropAssertions.Success(await python.SendAsync("close", new { storeId = "python" }));

        Assert.False(File.Exists(regionPath));
        Assert.True(File.Exists(synchronizationPath));
        Assert.False(File.Exists(ownersPath));
        Assert.True(File.Exists(lifecyclePath));
    }
}
