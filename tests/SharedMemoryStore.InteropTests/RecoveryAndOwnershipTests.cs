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

    public static TheoryData<string, string> DistinctRuntimePairs => new()
    {
        { "dotnet", "cpp" },
        { "dotnet", "python" },
        { "cpp", "dotnet" },
        { "cpp", "python" },
        { "python", "dotnet" },
        { "python", "cpp" }
    };

    [Theory]
    [MemberData(nameof(DistinctRuntimePairs))]
    public async Task EveryRuntimeCheckpointPauseAllowsForeignHotProgressAndExactResume(
        string actorRuntime,
        string helperRuntime)
    {
        AgentDefinition actorDefinition = AgentDefinition.ResolveCheckpoint(actorRuntime);
        AgentDefinition helperDefinition = AgentDefinition.Resolve(helperRuntime);
        if (!actorDefinition.IsAvailable() || !helperDefinition.IsAvailable())
        {
            return;
        }

        await using var helper = await AgentProcess.StartAsync(helperDefinition);
        await using var pausedActor = await AgentProcess.StartAsync(actorDefinition);
        string name = $"sms-pause-{actorRuntime}-{helperRuntime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await helper.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "helper",
                name,
                openMode: 0,
                slotCount: 4,
                participantRecordCount: 4)));

        byte[] pausedKey = [0x11, 0, 0xff];
        byte[] pausedValue = [0x21, 0, 0x22];
        AgentResponse paused = await pausedActor.SendAsync(
            AgentProtocolCatalog.Command.PauseAtCheckpoint,
            InteropAssertions.CheckpointArguments(
                name,
                (int)AgentProtocolCatalog.CheckpointId.PublishBeforeSlotClaim,
                operation: "publish",
                key: pausedKey,
                value: pausedValue,
                slotCount: 4,
                participantRecordCount: 4),
            TimeSpan.FromSeconds(15));
        InteropAssertions.Success(paused);
        Assert.Equal(
            (int)AgentProtocolCatalog.CheckpointId.PublishBeforeSlotClaim,
            paused.Result!.Value.GetProperty("checkpointId").GetInt32());

        InteropAssertions.Status(await helper.SendAsync("acquire", new
        {
            storeId = "helper",
            leaseId = "not-yet-published",
            key = AgentProtocol.EncodeBytes(pausedKey)
        }), 2, "NotFound");
        byte[] healthyKey = [0x31, 0, 0xfe];
        InteropAssertions.Success(await helper.SendAsync("publish", new
        {
            storeId = "helper",
            key = AgentProtocol.EncodeBytes(healthyKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 0x41, 0, 0x42 }),
            descriptor = string.Empty,
            timeoutMs = 0
        }));

        InteropAssertions.Success(await pausedActor.SendAsync(
            AgentProtocolCatalog.Command.ResumeCheckpoint,
            new { },
            TimeSpan.FromSeconds(15)));
        AgentResponse acquired = await helper.SendAsync("acquire", new
        {
            storeId = "helper",
            leaseId = "resumed-value",
            key = AgentProtocol.EncodeBytes(pausedKey)
        });
        InteropAssertions.Success(acquired);
        Assert.Equal(pausedValue, InteropAssertions.Decode(acquired, "value"));
        InteropAssertions.Success(await helper.SendAsync("release", new { leaseId = "resumed-value" }));
        InteropAssertions.Success(await helper.SendAsync("close", new { storeId = "helper" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task EveryRuntimeCheckpointCancellationBeforeOrderingLeavesNoOwnership(
        string actorRuntime)
    {
        AgentDefinition actorDefinition = AgentDefinition.ResolveCheckpoint(actorRuntime);
        if (!actorDefinition.IsAvailable())
        {
            return;
        }

        await using var creator = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        await using var canceledActor = await AgentProcess.StartAsync(actorDefinition);
        string name = $"sms-cancel-{actorRuntime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await creator.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "creator",
                name,
                openMode: 0,
                slotCount: 2,
                participantRecordCount: 3)));
        byte[] key = [0x51, 0, 0xff];
        InteropAssertions.Success(await canceledActor.SendAsync(
            AgentProtocolCatalog.Command.PauseAtCheckpoint,
            InteropAssertions.CheckpointArguments(
                name,
                (int)AgentProtocolCatalog.CheckpointId.PublishBeforeSlotClaim,
                operation: "publish",
                key: key,
                value: new byte[] { 0x61, 0, 0x62 },
                slotCount: 2,
                participantRecordCount: 3),
            TimeSpan.FromSeconds(15)));

        InteropAssertions.Status(await canceledActor.SendAsync(
            AgentProtocolCatalog.Command.CancelCheckpoint,
            new { },
            TimeSpan.FromSeconds(15)), 22, "OperationCanceled");
        InteropAssertions.Status(await creator.SendAsync("acquire", new
        {
            storeId = "creator",
            leaseId = "canceled-value",
            key = AgentProtocol.EncodeBytes(key)
        }), 2, "NotFound");
        InteropAssertions.Success(await creator.SendAsync("publish", new
        {
            storeId = "creator",
            key = AgentProtocol.EncodeBytes(key),
            value = AgentProtocol.EncodeBytes(new byte[] { 0x71 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await creator.SendAsync("close", new { storeId = "creator" }));
    }

    [Theory]
    [MemberData(nameof(DistinctRuntimePairs))]
    public async Task EveryRuntimeCheckpointCrashRecoversExactReservationAndLease(
        string crashedRuntime,
        string survivorRuntime)
    {
        AgentDefinition crashedDefinition = AgentDefinition.ResolveCheckpoint(crashedRuntime);
        AgentDefinition survivorDefinition = AgentDefinition.Resolve(survivorRuntime);
        if (!crashedDefinition.IsAvailable() || !survivorDefinition.IsAvailable())
        {
            return;
        }

        const int slotCount = 4;
        const int leaseRecordCount = 4;
        const int participantRecordCount = 4;
        string name = $"sms-crash-{crashedRuntime}-{survivorRuntime}-{Guid.NewGuid():N}";
        await using var survivor = await AgentProcess.StartAsync(survivorDefinition);
        InteropAssertions.Success(await survivor.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "survivor",
                name,
                openMode: 0,
                slotCount: slotCount,
                leaseRecordCount: leaseRecordCount,
                participantRecordCount: participantRecordCount)));

        byte[] reservedKey = [0x81, 0, 0x82];
        await using (var crashedReservationOwner = await AgentProcess.StartAsync(
            crashedDefinition))
        {
            await crashedReservationOwner.CrashAtCheckpointAsync(
                InteropAssertions.CheckpointArguments(
                    name,
                    (int)AgentProtocolCatalog.CheckpointId.ReserveAfterReservationPublication,
                    operation: "reserve",
                    key: reservedKey,
                    value: new byte[] { 0x83, 0, 0x84 },
                    slotCount: slotCount,
                    leaseRecordCount: leaseRecordCount,
                    participantRecordCount: participantRecordCount),
                TimeSpan.FromSeconds(15));
        }

        AgentResponse recoveredReservations = await survivor.SendAsync("recoverReservations", new
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
            value = AgentProtocol.EncodeBytes(new byte[] { 0x85, 0, 0x86 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await survivor.SendAsync("remove", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(reservedKey)
        }));

        byte[] leasedKey = [0x91, 0, 0x92];
        byte[] leasedValue = [0x93, 0, 0x94];
        InteropAssertions.Success(await survivor.SendAsync("publish", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(leasedKey),
            value = AgentProtocol.EncodeBytes(leasedValue),
            descriptor = string.Empty
        }));
        await using (var crashedLeaseOwner = await AgentProcess.StartAsync(
            crashedDefinition))
        {
            await crashedLeaseOwner.CrashAtCheckpointAsync(
                InteropAssertions.CheckpointArguments(
                    name,
                    (int)AgentProtocolCatalog.CheckpointId.AcquireAfterPublishedRevalidation,
                    operation: "acquire",
                    key: leasedKey,
                    slotCount: slotCount,
                    leaseRecordCount: leaseRecordCount,
                    participantRecordCount: participantRecordCount),
                TimeSpan.FromSeconds(15));
        }

        AgentResponse recoveredLeases = await survivor.SendAsync("recoverLeases", new
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
        byte[] replacement = [0xa1, 0, 0xa2];
        InteropAssertions.Success(await survivor.SendAsync("publish", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(leasedKey),
            value = AgentProtocol.EncodeBytes(replacement),
            descriptor = string.Empty
        }));
        AgentResponse replacementLease = await survivor.SendAsync("acquire", new
        {
            storeId = "survivor",
            leaseId = "replacement",
            key = AgentProtocol.EncodeBytes(leasedKey)
        });
        InteropAssertions.Success(replacementLease);
        Assert.Equal(replacement, InteropAssertions.Decode(replacementLease, "value"));
        InteropAssertions.Success(await survivor.SendAsync("release", new { leaseId = "replacement" }));
        InteropAssertions.Success(await survivor.SendAsync("close", new { storeId = "survivor" }));
    }

    [Fact]
    public async Task ManagedRecoveryRejectsPidReuseWithoutMatchingProcessStartIdentity()
    {
        const int participantRecordCount = 4;
        string name = $"sms-managed-pid-reuse-{Guid.NewGuid():N}";
        await using var survivor = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        InteropAssertions.Success(await survivor.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "survivor",
                name,
                openMode: 0,
                participantRecordCount: participantRecordCount)));

        byte[] key = [0xb1, 0, 0xb2];
        int crashedProcessId;
        await using (var crashedOwner = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet")))
        {
            crashedProcessId = crashedOwner.ProcessId;
            await crashedOwner.CrashAtCheckpointAsync(
                InteropAssertions.CheckpointArguments(
                    name,
                    (int)AgentProtocolCatalog.CheckpointId.ReserveAfterReservationPublication,
                    operation: "reserve",
                    key: key,
                    value: new byte[] { 0xb3 },
                    participantRecordCount: participantRecordCount),
                TimeSpan.FromSeconds(15));
        }

        AgentResponse mutation = await survivor.SendAsync(
            AgentProtocolCatalog.Command.InjectRawFault,
            new
            {
                storeId = "survivor",
                target = "participantProcessId",
                targetProcessId = crashedProcessId,
                replacementProcessId = survivor.ProcessId
            });
        InteropAssertions.Success(mutation);
        Assert.Equal(crashedProcessId, mutation.Result!.Value.GetProperty("originalProcessId").GetInt32());
        Assert.Equal(survivor.ProcessId, mutation.Result!.Value.GetProperty("replacementProcessId").GetInt32());

        AgentResponse recovered = await survivor.SendAsync("recoverReservations", new
        {
            storeId = "survivor",
            recoverCurrentProcess = false
        });
        InteropAssertions.Success(recovered);
        Assert.Equal(1, recovered.Result!.Value.GetProperty("recoveredReservationCount").GetInt32());
        InteropAssertions.Success(await survivor.SendAsync("publish", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(key),
            value = AgentProtocol.EncodeBytes(new byte[] { 0xb4 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await survivor.SendAsync("close", new { storeId = "survivor" }));
    }

    [Fact]
    public async Task ManagedRecoveryPreservesForeignNamespaceUntilExactNamespaceIsRestored()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int participantRecordCount = 4;
        string name = $"sms-managed-namespace-{Guid.NewGuid():N}";
        await using var survivor = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        InteropAssertions.Success(await survivor.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "survivor",
                name,
                openMode: 0,
                participantRecordCount: participantRecordCount)));

        byte[] key = [0xc1, 0, 0xc2];
        int crashedProcessId;
        await using (var crashedOwner = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet")))
        {
            crashedProcessId = crashedOwner.ProcessId;
            await crashedOwner.CrashAtCheckpointAsync(
                InteropAssertions.CheckpointArguments(
                    name,
                    (int)AgentProtocolCatalog.CheckpointId.ReserveAfterReservationPublication,
                    operation: "reserve",
                    key: key,
                    value: new byte[] { 0xc3 },
                    participantRecordCount: participantRecordCount),
                TimeSpan.FromSeconds(15));
        }

        AgentResponse mutation = await survivor.SendAsync(
            AgentProtocolCatalog.Command.InjectRawFault,
            new
            {
                storeId = "survivor",
                target = "participantNamespace",
                targetProcessId = crashedProcessId,
                replacementPidNamespaceId = ulong.MaxValue
            });
        InteropAssertions.Success(mutation);
        ulong originalNamespace = mutation.Result!.Value
            .GetProperty("originalPidNamespaceId")
            .GetUInt64();
        Assert.NotEqual(0UL, originalNamespace);

        AgentResponse preserved = await survivor.SendAsync("recoverReservations", new
        {
            storeId = "survivor",
            recoverCurrentProcess = false
        });
        InteropAssertions.Success(preserved);
        Assert.Equal(0, preserved.Result!.Value.GetProperty("recoveredReservationCount").GetInt32());
        Assert.Equal(1, preserved.Result!.Value.GetProperty("unsupportedReservationCount").GetInt32());
        InteropAssertions.Status(await survivor.SendAsync("publish", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(key),
            value = AgentProtocol.EncodeBytes(new byte[] { 0xc4 }),
            descriptor = string.Empty
        }), 1, "DuplicateKey");

        InteropAssertions.Success(await survivor.SendAsync(
            AgentProtocolCatalog.Command.InjectRawFault,
            new
            {
                storeId = "survivor",
                target = "participantNamespace",
                targetProcessId = crashedProcessId,
                replacementPidNamespaceId = originalNamespace
            }));
        AgentResponse recovered = await survivor.SendAsync("recoverReservations", new
        {
            storeId = "survivor",
            recoverCurrentProcess = false
        });
        InteropAssertions.Success(recovered);
        Assert.Equal(1, recovered.Result!.Value.GetProperty("recoveredReservationCount").GetInt32());
        InteropAssertions.Success(await survivor.SendAsync("publish", new
        {
            storeId = "survivor",
            key = AgentProtocol.EncodeBytes(key),
            value = AgentProtocol.EncodeBytes(new byte[] { 0xc5 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await survivor.SendAsync("close", new { storeId = "survivor" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task RecoveredTokensCannotAffectReusedLeaseAndReservationRecords(
        string currentRuntime)
    {
        AgentDefinition currentDefinition = AgentDefinition.Resolve(currentRuntime);
        if (!currentDefinition.IsAvailable())
        {
            return;
        }

        string name = $"sms-recovered-token-{currentRuntime}-{Guid.NewGuid():N}";
        await using var controller = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        await using var staleOwner = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        await using var current = await AgentProcess.StartAsync(currentDefinition);
        InteropAssertions.Success(await controller.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "controller",
                name,
                openMode: 0,
                slotCount: 2,
                leaseRecordCount: 1,
                participantRecordCount: 4)));
        InteropAssertions.Success(await staleOwner.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "stale-owner",
                name,
                openMode: 1,
                slotCount: 2,
                leaseRecordCount: 1,
                participantRecordCount: 4)));

        byte[] leaseKey = [0xd1, 0, 0xd2];
        byte[] reservationKey = [0xd3, 0, 0xd4];
        InteropAssertions.Success(await controller.SendAsync("publish", new
        {
            storeId = "controller",
            key = AgentProtocol.EncodeBytes(leaseKey),
            value = AgentProtocol.EncodeBytes(new byte[] { 0xd5 }),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await staleOwner.SendAsync("acquire", new
        {
            storeId = "stale-owner",
            leaseId = "stale-lease",
            key = AgentProtocol.EncodeBytes(leaseKey)
        }));
        InteropAssertions.Success(await staleOwner.SendAsync("reserve", new
        {
            storeId = "stale-owner",
            reservationId = "stale-reservation",
            key = AgentProtocol.EncodeBytes(reservationKey),
            payloadLength = 1,
            descriptor = string.Empty
        }));

        InteropAssertions.Success(await controller.SendAsync(
            AgentProtocolCatalog.Command.InjectRawFault,
            new
            {
                storeId = "controller",
                target = "participantProcessId",
                targetProcessId = staleOwner.ProcessId,
                replacementProcessId = controller.ProcessId
            }));
        AgentResponse recoveredLeases = await controller.SendAsync("recoverLeases", new
        {
            storeId = "controller",
            recoverCurrentProcess = false
        });
        InteropAssertions.Success(recoveredLeases);
        Assert.Equal(1, recoveredLeases.Result!.Value.GetProperty("recoveredLeaseCount").GetInt32());
        AgentResponse recoveredReservations = await controller.SendAsync("recoverReservations", new
        {
            storeId = "controller",
            recoverCurrentProcess = false
        });
        InteropAssertions.Success(recoveredReservations);
        Assert.Equal(
            1,
            recoveredReservations.Result!.Value.GetProperty("recoveredReservationCount").GetInt32());
        InteropAssertions.Success(await controller.SendAsync("remove", new
        {
            storeId = "controller",
            key = AgentProtocol.EncodeBytes(leaseKey)
        }));

        InteropAssertions.Success(await current.SendAsync(
            "open",
            InteropAssertions.OpenArguments(
                "current",
                name,
                openMode: 1,
                slotCount: 2,
                leaseRecordCount: 1,
                participantRecordCount: 4)));
        byte[] replacement = [0xe1, 0, 0xe2];
        InteropAssertions.Success(await current.SendAsync("publish", new
        {
            storeId = "current",
            key = AgentProtocol.EncodeBytes(leaseKey),
            value = AgentProtocol.EncodeBytes(replacement),
            descriptor = string.Empty
        }));
        InteropAssertions.Success(await current.SendAsync("acquire", new
        {
            storeId = "current",
            leaseId = "current-lease",
            key = AgentProtocol.EncodeBytes(leaseKey)
        }));
        InteropAssertions.Success(await current.SendAsync("reserve", new
        {
            storeId = "current",
            reservationId = "current-reservation",
            key = AgentProtocol.EncodeBytes(reservationKey),
            payloadLength = 1,
            descriptor = string.Empty
        }));

        AgentResponse staleRelease = await staleOwner.SendAsync(
            "release",
            new { leaseId = "stale-lease" });
        Assert.Contains(staleRelease.Status.Code, new[] { 8, 9 });
        AgentResponse staleAbort = await staleOwner.SendAsync(
            "abort",
            new { reservationId = "stale-reservation" });
        Assert.Contains(staleAbort.Status.Code, new[] { 16, 18 });
        AgentResponse retained = await current.SendAsync("read", new { leaseId = "current-lease" });
        InteropAssertions.Success(retained);
        Assert.Equal(replacement, InteropAssertions.Decode(retained, "value"));
        InteropAssertions.Success(await current.SendAsync("reservationWrite", new
        {
            reservationId = "current-reservation",
            data = AgentProtocol.EncodeBytes(new byte[] { 0xe3 })
        }));
        InteropAssertions.Success(await current.SendAsync(
            "advance",
            new { reservationId = "current-reservation", byteCount = 1 }));
        InteropAssertions.Success(await current.SendAsync(
            "commit",
            new { reservationId = "current-reservation" }));
        InteropAssertions.Success(await current.SendAsync("release", new { leaseId = "current-lease" }));
        InteropAssertions.Success(await current.SendAsync("close", new { storeId = "current" }));
        InteropAssertions.Success(await staleOwner.SendAsync("close", new { storeId = "stale-owner" }));
        InteropAssertions.Success(await controller.SendAsync("close", new { storeId = "controller" }));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task RawDirectoryCorruptionPropagatesToEveryOpenHandleAndNewOpen(
        string observerRuntime)
    {
        AgentDefinition observerDefinition = AgentDefinition.Resolve(observerRuntime);
        if (!observerDefinition.IsAvailable())
        {
            return;
        }

        string name = $"sms-corruption-{observerRuntime}-{Guid.NewGuid():N}";
        await using var injector = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        await using var observer = await AgentProcess.StartAsync(observerDefinition);
        await using var newcomer = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        InteropAssertions.Success(await injector.SendAsync(
            "open",
            InteropAssertions.OpenArguments("injector", name, openMode: 0)));
        InteropAssertions.Success(await observer.SendAsync(
            "open",
            InteropAssertions.OpenArguments("observer", name, openMode: 1)));
        InteropAssertions.Success(await injector.SendAsync(
            AgentProtocolCatalog.Command.InjectRawFault,
            new { storeId = "injector", target = "directoryMutation" }));

        InteropAssertions.Status(await observer.SendAsync("diagnostics", new
        {
            storeId = "observer"
        }), 13, "CorruptStore");
        InteropAssertions.Status(await injector.SendAsync("publish", new
        {
            storeId = "injector",
            key = AgentProtocol.EncodeBytes(new byte[] { 0xf1 }),
            value = AgentProtocol.EncodeBytes(new byte[] { 0xf2 }),
            descriptor = string.Empty
        }), 13, "CorruptStore");
        InteropAssertions.Status(await newcomer.SendAsync(
            "open",
            InteropAssertions.OpenArguments("newcomer", name, openMode: 1)),
            4,
            "IncompatibleLayout");
        InteropAssertions.Success(await observer.SendAsync("close", new { storeId = "observer" }));
        InteropAssertions.Success(await injector.SendAsync("close", new { storeId = "injector" }));
    }

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
    public async Task HotPublishesIgnoreForeignColdStoreLock(string runtime)
    {
        var definition = AgentDefinition.Resolve(runtime);
        if (!definition.IsAvailable())
        {
            return;
        }

        await using var agent = await AgentProcess.StartAsync(definition);
        await using var locker = await AgentProcess.StartAsync(AgentDefinition.Resolve("dotnet"));
        var name = $"sms-contention-{runtime}-{Guid.NewGuid():N}";
        InteropAssertions.Success(await agent.SendAsync(
            "open",
            InteropAssertions.OpenArguments("store", name, openMode: 0)));
        var noWaitKey = AgentProtocol.EncodeBytes(new byte[] { 4, 0, 4 });
        var boundedKey = AgentProtocol.EncodeBytes(new byte[] { 5, 0, 5 });
        var value = AgentProtocol.EncodeBytes(new byte[] { 8, 0, 8 });

        var coldLockHeld = false;
        try
        {
            InteropAssertions.Success(await locker.SendAsync(
                AgentProtocolCatalog.Command.HoldColdLock,
                new { name }));
            coldLockHeld = true;
            InteropAssertions.Success(await agent.SendAsync("publish", new
            {
                storeId = "store",
                key = noWaitKey,
                value,
                descriptor = string.Empty,
                timeoutMs = 0
            }));

            var stopwatch = Stopwatch.StartNew();
            var bounded = await agent.SendAsync("publish", new
            {
                storeId = "store",
                key = boundedKey,
                value,
                descriptor = string.Empty,
                timeoutMs = 40
            });
            stopwatch.Stop();
            InteropAssertions.Success(bounded);
            Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 500);
        }
        finally
        {
            if (coldLockHeld)
            {
                InteropAssertions.Success(await locker.SendAsync(
                    AgentProtocolCatalog.Command.ReleaseColdLock,
                    new { }));
            }
        }

        AgentResponse acquired = await agent.SendAsync("acquire", new
        {
            storeId = "store",
            leaseId = "lease",
            key = noWaitKey,
            timeoutMs = 1000
        });
        InteropAssertions.Success(acquired);
        Assert.Equal(AgentProtocol.DecodeBytes(value), InteropAssertions.Decode(acquired, "value"));
        InteropAssertions.Success(await agent.SendAsync("release", new { leaseId = "lease" }));
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
