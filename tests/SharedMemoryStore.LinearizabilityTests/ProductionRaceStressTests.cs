using System.Runtime.InteropServices;
using SharedMemoryStore.LockFree;
using Store = SharedMemoryStore.MemoryStore;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SharedMemoryStore.LinearizabilityTests;

/// <summary>
/// Runs the SC-011 repetition count against the real mapped-store operations.
/// The model checker has a separate repetition budget; none of the counts in
/// this test are permutations of a preconstructed history.
/// </summary>
public sealed class ProductionRaceStressTests
{
    private const int DefaultSeed = 0x5eed_0200;
    private const int DefaultRepetitions = 256;
    private static readonly byte[] Key = [0x41];
    private static readonly byte[] LeftValue = [0x11];
    private static readonly byte[] RightValue = [0x22];
    private static readonly byte[] NextValue = [0x33];
    private readonly ITestOutputHelper _output;

    public ProductionRaceStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> RequiredFamilies => new()
    {
        "publish-publish",
        "publish-reserve",
        "reserve-reserve",
        "commit-acquire",
        "acquire-remove",
        "release-reclaim",
        "recovery-live-lease",
        "disposal-operation"
    };

    [Theory]
    [MemberData(nameof(RequiredFamilies))]
    [Trait("Category", "ProductionRaceStress")]
    public void ConfiguredRepetitionsExecuteProductionRaceFamily(string family)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        int repetitions = ConfiguredRepetitions();
        int rootSeed = ConfiguredSeed();

        switch (family)
        {
            case "publish-publish":
                RunPublishPublish(repetitions, FamilySeed(rootSeed, 1));
                break;
            case "publish-reserve":
                RunPublishReserve(repetitions, FamilySeed(rootSeed, 2));
                break;
            case "reserve-reserve":
                RunReserveReserve(repetitions, FamilySeed(rootSeed, 3));
                break;
            case "commit-acquire":
                RunCommitAcquire(repetitions, FamilySeed(rootSeed, 4));
                break;
            case "acquire-remove":
                RunAcquireRemove(repetitions, FamilySeed(rootSeed, 5));
                break;
            case "release-reclaim":
                RunReleaseReclaim(repetitions, FamilySeed(rootSeed, 6));
                break;
            case "recovery-live-lease":
                RunRecoveryLiveLease(repetitions, FamilySeed(rootSeed, 7));
                break;
            case "disposal-operation":
                RunDisposalOperation(repetitions, FamilySeed(rootSeed, 8));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family), family, null);
        }
    }

    private void RunPublishPublish(int repetitions, int seed)
    {
        const string family = "publish-publish";
        using Store store = CreateStore(family, slotCount: 2, leaseCount: 4);
        StoreStatus first = default;
        StoreStatus second = default;
        using var race = new TwoActorRace(
            () => first = store.TryPublish(Key, LeftValue),
            () => second = store.TryPublish(Key, RightValue));
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            race.Run(random.NextDelay(), random.NextDelay());
            bool firstAllowed = first is StoreStatus.Success
                or StoreStatus.DuplicateKey
                or StoreStatus.StoreFull
                or StoreStatus.StoreBusy;
            bool secondAllowed = second is StoreStatus.Success
                or StoreStatus.DuplicateKey
                or StoreStatus.StoreFull
                or StoreStatus.StoreBusy;
            int successes = (first == StoreStatus.Success ? 1 : 0) + (second == StoreStatus.Success ? 1 : 0);
            if (!firstAllowed || !secondAllowed || successes > 1)
            {
                Fail(family, repetition, seed, $"first={first}, second={second}, successes={successes}");
            }

            if ((first == StoreStatus.DuplicateKey || second == StoreStatus.DuplicateKey) && successes != 1)
            {
                Fail(family, repetition, seed, $"duplicate without exactly one winner: first={first}, second={second}");
            }

            if (successes == 1)
            {
                StoreStatus acquire = store.TryAcquire(Key, out ValueLease lease);
                if (acquire != StoreStatus.Success || !lease.IsValid)
                {
                    Fail(family, repetition, seed, $"winner was not readable: acquire={acquire}");
                }

                byte expected = first == StoreStatus.Success ? LeftValue[0] : RightValue[0];
                if (lease.ValueSpan.Length != 1 || lease.ValueSpan[0] != expected)
                {
                    Fail(family, repetition, seed, "winner payload did not match its publication");
                }

                RequireRelease(family, repetition, seed, lease.Release());
            }

            RemoveForReuse(store, family, repetition, seed);
        }

        Record(family, repetitions, seed);
    }

    private void RunPublishReserve(int repetitions, int seed)
    {
        const string family = "publish-reserve";
        using Store store = CreateStore(family, slotCount: 2, leaseCount: 4);
        StoreStatus publish = default;
        StoreStatus reserve = default;
        ValueReservation reservation = default;
        using var race = new TwoActorRace(
            () => publish = store.TryPublish(Key, LeftValue),
            () => reserve = store.TryReserve(Key, 1, default, out reservation));
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            reservation = default;
            race.Run(random.NextDelay(), random.NextDelay());

            bool publishAllowed = publish is StoreStatus.Success
                or StoreStatus.DuplicateKey
                or StoreStatus.StoreFull
                or StoreStatus.StoreBusy;
            bool reserveAllowed = reserve is StoreStatus.Success
                or StoreStatus.DuplicateKey
                or StoreStatus.StoreFull
                or StoreStatus.StoreBusy;
            int successes = (publish == StoreStatus.Success ? 1 : 0)
                + (reserve == StoreStatus.Success ? 1 : 0);
            if (!publishAllowed || !reserveAllowed || successes > 1)
            {
                Fail(family, repetition, seed, $"publish={publish}, reserve={reserve}, successes={successes}");
            }

            if ((publish == StoreStatus.DuplicateKey || reserve == StoreStatus.DuplicateKey)
                && successes != 1)
            {
                Fail(
                    family,
                    repetition,
                    seed,
                    $"duplicate without exactly one winner: publish={publish}, reserve={reserve}");
            }

            if (reserve == StoreStatus.Success)
            {
                if (!reservation.IsValid)
                {
                    Fail(family, repetition, seed, "successful explicit reservation returned an invalid token");
                }

                StoreStatus abort = reservation.Abort();
                if (abort != StoreStatus.Success)
                {
                    Fail(family, repetition, seed, $"reservation cleanup abort={abort}");
                }
            }
            else if (reservation.IsValid)
            {
                Fail(family, repetition, seed, $"failed reserve returned a valid token: reserve={reserve}");
            }

            if (publish == StoreStatus.Success)
            {
                StoreStatus acquire = store.TryAcquire(Key, out ValueLease lease);
                if (acquire != StoreStatus.Success
                    || !lease.IsValid
                    || lease.ValueSpan.Length != 1
                    || lease.ValueSpan[0] != LeftValue[0])
                {
                    Fail(family, repetition, seed, $"atomic publication was not readable: acquire={acquire}");
                }

                RequireRelease(family, repetition, seed, lease.Release());
            }

            RemoveForReuse(store, family, repetition, seed);
        }

        Record(family, repetitions, seed);
    }

    private void RunReserveReserve(int repetitions, int seed)
    {
        const string family = "reserve-reserve";
        using Store store = CreateStore(family, slotCount: 2, leaseCount: 2);
        StoreStatus first = default;
        StoreStatus second = default;
        ValueReservation firstReservation = default;
        ValueReservation secondReservation = default;
        using var race = new TwoActorRace(
            () => first = store.TryReserve(Key, 1, default, out firstReservation),
            () => second = store.TryReserve(Key, 1, default, out secondReservation));
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            firstReservation = default;
            secondReservation = default;
            race.Run(random.NextDelay(), random.NextDelay());

            bool firstAllowed = first is StoreStatus.Success
                or StoreStatus.DuplicateKey
                or StoreStatus.StoreFull
                or StoreStatus.StoreBusy;
            bool secondAllowed = second is StoreStatus.Success
                or StoreStatus.DuplicateKey
                or StoreStatus.StoreFull
                or StoreStatus.StoreBusy;
            int successes = (first == StoreStatus.Success ? 1 : 0)
                + (second == StoreStatus.Success ? 1 : 0);
            if (!firstAllowed || !secondAllowed || successes > 1)
            {
                Fail(family, repetition, seed, $"first={first}, second={second}, successes={successes}");
            }

            if ((first == StoreStatus.DuplicateKey || second == StoreStatus.DuplicateKey)
                && successes != 1)
            {
                Fail(
                    family,
                    repetition,
                    seed,
                    $"duplicate without exactly one winner: first={first}, second={second}");
            }

            if ((first == StoreStatus.Success) != firstReservation.IsValid
                || (second == StoreStatus.Success) != secondReservation.IsValid)
            {
                Fail(
                    family,
                    repetition,
                    seed,
                    $"reservation validity mismatch: first={first}/{firstReservation.IsValid}, " +
                    $"second={second}/{secondReservation.IsValid}");
            }

            if (first == StoreStatus.Success && firstReservation.Abort() != StoreStatus.Success)
            {
                Fail(family, repetition, seed, "first reservation cleanup did not abort");
            }

            if (second == StoreStatus.Success && secondReservation.Abort() != StoreStatus.Success)
            {
                Fail(family, repetition, seed, "second reservation cleanup did not abort");
            }

            RemoveForReuse(store, family, repetition, seed);
        }

        Record(family, repetitions, seed);
    }

    private void RunCommitAcquire(int repetitions, int seed)
    {
        const string family = "commit-acquire";
        using Store store = CreateStore(family, slotCount: 1, leaseCount: 2);
        ValueReservation reservation = default;
        ValueLease lease = default;
        StoreStatus commit = default;
        StoreStatus acquire = default;
        using var race = new TwoActorRace(
            () => commit = reservation.Commit(),
            () => acquire = store.TryAcquire(Key, out lease));
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            StoreStatus reserve = ReservePrepared(store, Key, NextValue[0], out reservation);
            if (reserve != StoreStatus.Success)
            {
                Fail(family, repetition, seed, $"setup reserve={reserve}");
            }

            lease = default;
            race.Run(random.NextDelay(), random.NextDelay());
            bool commitAllowed = commit is StoreStatus.Success or StoreStatus.StoreBusy;
            bool acquireAllowed = acquire is StoreStatus.Success or StoreStatus.NotFound or StoreStatus.StoreBusy;
            if (!commitAllowed || !acquireAllowed || (commit != StoreStatus.Success && acquire == StoreStatus.Success))
            {
                Fail(family, repetition, seed, $"commit={commit}, acquire={acquire}");
            }

            if (acquire == StoreStatus.Success)
            {
                if (!lease.IsValid || lease.ValueSpan.Length != 1 || lease.ValueSpan[0] != NextValue[0])
                {
                    Fail(family, repetition, seed, "successful acquire exposed incomplete or wrong bytes");
                }

                RequireRelease(family, repetition, seed, lease.Release());
            }

            if (commit == StoreStatus.StoreBusy)
            {
                StoreStatus abort = reservation.Abort();
                if (abort is not (StoreStatus.Success or StoreStatus.ReservationAlreadyCompleted))
                {
                    Fail(family, repetition, seed, $"cleanup abort={abort}");
                }
            }

            RemoveForReuse(store, family, repetition, seed);
        }

        Record(family, repetitions, seed);
    }

    private void RunAcquireRemove(int repetitions, int seed)
    {
        const string family = "acquire-remove";
        using Store store = CreateStore(family, slotCount: 1, leaseCount: 2);
        ValueLease lease = default;
        StoreStatus acquire = default;
        StoreStatus remove = default;
        using var race = new TwoActorRace(
            () => acquire = store.TryAcquire(Key, out lease),
            () => remove = store.TryRemove(Key));
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            PublishForReuse(store, NextValue, family, repetition, seed);
            lease = default;
            race.Run(random.NextDelay(), random.NextDelay());

            bool acquireAllowed = acquire is StoreStatus.Success or StoreStatus.NotFound or StoreStatus.StoreBusy;
            bool removeAllowed = remove is StoreStatus.Success or StoreStatus.RemovePending or StoreStatus.StoreBusy;
            bool orderingAllowed = acquire switch
            {
                StoreStatus.Success => remove is StoreStatus.RemovePending or StoreStatus.StoreBusy,
                StoreStatus.NotFound => remove is StoreStatus.Success or StoreStatus.RemovePending,
                StoreStatus.StoreBusy => removeAllowed,
                _ => false
            };
            if (!acquireAllowed || !removeAllowed || !orderingAllowed)
            {
                Fail(family, repetition, seed, $"acquire={acquire}, remove={remove}");
            }

            if (acquire == StoreStatus.Success)
            {
                if (!lease.IsValid || lease.ValueSpan.Length != 1 || lease.ValueSpan[0] != NextValue[0])
                {
                    Fail(family, repetition, seed, "successful acquire exposed wrong bytes");
                }

                RequireRelease(family, repetition, seed, lease.Release());
            }

            RemoveForReuse(store, family, repetition, seed);
        }

        Record(family, repetitions, seed);
    }

    private void RunReleaseReclaim(int repetitions, int seed)
    {
        const string family = "release-reclaim";
        using Store store = CreateStore(family, slotCount: 1, leaseCount: 2);
        ValueLease lease = default;
        StoreStatus release = default;
        StoreStatus publish = default;
        using var race = new TwoActorRace(
            () => release = lease.Release(),
            () => publish = store.TryPublish(Key, NextValue));
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            PublishForReuse(store, LeftValue, family, repetition, seed);
            StoreStatus acquire = store.TryAcquire(Key, out lease);
            if (acquire != StoreStatus.Success)
            {
                Fail(family, repetition, seed, $"setup acquire={acquire}");
            }

            StoreStatus remove = store.TryRemove(Key);
            if (remove != StoreStatus.RemovePending)
            {
                Fail(family, repetition, seed, $"setup remove={remove}");
            }

            race.Run(random.NextDelay(), random.NextDelay());
            if (release != StoreStatus.Success
                || publish is not (StoreStatus.Success or StoreStatus.DuplicateKey or StoreStatus.StoreBusy))
            {
                Fail(family, repetition, seed, $"release={release}, publish={publish}");
            }

            if (publish != StoreStatus.Success)
            {
                PublishForReuse(store, NextValue, family, repetition, seed);
            }

            StoreStatus verify = store.TryAcquire(Key, out ValueLease current);
            if (verify != StoreStatus.Success
                || current.ValueSpan.Length != 1
                || current.ValueSpan[0] != NextValue[0])
            {
                Fail(family, repetition, seed, $"reused generation verify={verify}");
            }

            RequireRelease(family, repetition, seed, current.Release());
            RemoveForReuse(store, family, repetition, seed);
        }

        Record(family, repetitions, seed);
    }

    private void RunRecoveryLiveLease(int repetitions, int seed)
    {
        const string family = "recovery-live-lease";
        using Store store = CreateStore(family, slotCount: 1, leaseCount: 4, enableLeaseRecovery: true);
        ValueLease lease = default;
        ValueLease guardLease = default;
        StoreStatus recovery = default;
        LeaseRecoveryReport report = default;
        StoreStatus release = default;
        long recoverySuccesses = 0;
        long recoveryBusy = 0;
        long liveActiveWitnesses = 0;
        using var race = new TwoActorRace(
            () => recovery = store.TryRecoverLeases(new LeaseRecoveryOptions(false), out report),
            () => release = lease.Release());
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            PublishForReuse(store, LeftValue, family, repetition, seed);
            StoreStatus acquire = store.TryAcquire(Key, out lease);
            if (acquire != StoreStatus.Success)
            {
                Fail(family, repetition, seed, $"setup acquire={acquire}");
            }

            StoreStatus guardAcquire = store.TryAcquire(Key, out guardLease);
            if (guardAcquire != StoreStatus.Success)
            {
                Fail(family, repetition, seed, $"setup guard acquire={guardAcquire}");
            }

            StoreStatus remove = store.TryRemove(Key);
            if (remove != StoreStatus.RemovePending)
            {
                Fail(family, repetition, seed, $"setup remove={remove}");
            }

            report = default;
            race.Run(random.NextDelay(), random.NextDelay());
            if (recovery is not (StoreStatus.Success or StoreStatus.StoreBusy)
                || release != StoreStatus.Success)
            {
                Fail(family, repetition, seed, $"recovery={recovery}, release={release}");
            }

            if (recovery == StoreStatus.Success)
            {
                recoverySuccesses++;
                if (report.RecoveredLeaseCount != 0
                    || report.UnsupportedLeaseCount != 0
                    || report.FailedRecoveryCount != 0
                    || report.ActiveLeaseCount < 1)
                {
                    Fail(
                        family,
                        repetition,
                        seed,
                        $"recovered={report.RecoveredLeaseCount}, active={report.ActiveLeaseCount}, " +
                        $"unsupported={report.UnsupportedLeaseCount}, failed={report.FailedRecoveryCount}, release={release}");
                }

                liveActiveWitnesses++;
            }
            else
            {
                recoveryBusy++;
            }

            RequireRelease(family, repetition, seed, guardLease.Release());
            PublishForReuse(store, NextValue, family, repetition, seed);
            RemoveForReuse(store, family, repetition, seed);
        }

        if (recoverySuccesses == 0)
        {
            throw new XunitException("Normal recovery returned StoreBusy for every configured live-lease race.");
        }

        Record(
            family,
            repetitions,
            seed,
            $"recoverySuccesses={recoverySuccesses} recoveryBusy={recoveryBusy} " +
            $"liveActiveWitnesses={liveActiveWitnesses}");
    }

    private void RunDisposalOperation(int repetitions, int seed)
    {
        const string family = "disposal-operation";
        SharedMemoryStoreOptions create = CreateOptions(family, slotCount: 1, leaseCount: 2, OpenMode.CreateNew);
        using Store keeper = OpenStore(create, OpenMode.CreateNew);
        if (keeper.TryPublish(Key, LeftValue) != StoreStatus.Success)
        {
            throw new XunitException("Could not publish the disposal race fixture.");
        }

        Store? participant = null;
        StoreStatus acquire = default;
        ValueLease lease = default;
        int racedDisposeCompleted = 0;
        long operationWins = 0;
        long disposalWins = 0;
        using var race = new TwoActorRace(
            () => acquire = participant!.TryAcquire(Key, out lease),
            () =>
            {
                participant!.Dispose();
                Volatile.Write(ref racedDisposeCompleted, 1);
            });
        var random = new DeterministicRandom(seed);

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            participant = OpenStore(create, OpenMode.OpenExisting);
            acquire = default;
            lease = default;
            Volatile.Write(ref racedDisposeCompleted, 0);
            try
            {
                race.Run(random.NextDelay(), random.NextDelay());
                if (acquire == StoreStatus.Success)
                {
                    operationWins++;
                    if (lease.HandleForEngine.IsDefault)
                    {
                        Fail(family, repetition, seed, "successful acquire returned a default lease token");
                    }

                    if (lease.IsValid)
                    {
                        Fail(family, repetition, seed, "completed disposal did not invalidate its successful lease");
                    }

                    // A successful lease may already have been invalidated and
                    // reclaimed by this exact handle's completed Dispose. Its
                    // successful return is the operation-before-disposal outcome.
                }
                else if (acquire == StoreStatus.StoreDisposed)
                {
                    disposalWins++;
                    if (lease.IsValid)
                    {
                        Fail(family, repetition, seed, "disposed-first acquire returned a valid lease");
                    }
                }
                else
                {
                    Fail(family, repetition, seed, $"acquire={acquire}");
                }

                StoreStatus keeperAcquire = keeper.TryAcquire(Key, out ValueLease keeperLease);
                if (keeperAcquire != StoreStatus.Success
                    || !keeperLease.IsValid
                    || keeperLease.ValueSpan.Length != 1
                    || keeperLease.ValueSpan[0] != LeftValue[0])
                {
                    Fail(
                        family,
                        repetition,
                        seed,
                        $"disposing one handle affected the keeper: acquire={keeperAcquire}");
                }

                RequireRelease(family, repetition, seed, keeperLease.Release());
            }
            finally
            {
                // A passing repetition executes exactly the one Dispose call in
                // the raced actor. Cleanup invokes Dispose only when the actor
                // failed before completing, in which case no completion marker
                // is emitted or credited.
                if (Volatile.Read(ref racedDisposeCompleted) == 0)
                {
                    participant!.Dispose();
                }

                participant = null;
            }
        }

        if (repetitions >= 1_000 && (operationWins == 0 || disposalWins == 0))
        {
            throw new XunitException(
                $"Seeded disposal race did not reach both documented orderings: " +
                $"operationWins={operationWins}, disposalWins={disposalWins}.");
        }

        _output.WriteLine(
            $"family={family} seed={seed} completed={repetitions} " +
            $"productionOperationRaces={repetitions} disposeCalls={repetitions} " +
            $"freshHandles={repetitions} operationWins={operationWins} disposalWins={disposalWins} " +
            "control=persistent-two-phase-barrier");
    }

    private static StoreStatus ReservePrepared(
        Store store,
        byte[] key,
        byte value,
        out ValueReservation reservation)
    {
        StoreStatus status = store.TryReserve(key, payloadLength: 1, descriptor: default, out reservation);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        Span<byte> destination = reservation.GetSpan(1);
        if (destination.Length < 1)
        {
            return StoreStatus.UnknownFailure;
        }

        destination[0] = value;
        return reservation.Advance(1);
    }

    private static void PublishForReuse(
        Store store,
        byte[] value,
        string family,
        int repetition,
        int seed)
    {
        for (var attempt = 0; attempt < 1_024; attempt++)
        {
            StoreStatus status = store.TryPublish(Key, value);
            if (status == StoreStatus.Success)
            {
                return;
            }

            if (status is not (StoreStatus.DuplicateKey or StoreStatus.StoreBusy))
            {
                Fail(family, repetition, seed, $"reuse publish={status}, attempt={attempt}");
            }

            Thread.SpinWait(16 + (attempt & 31));
        }

        Fail(family, repetition, seed, "reuse publication did not converge in 1024 attempts");
    }

    private static void RemoveForReuse(Store store, string family, int repetition, int seed)
    {
        for (var attempt = 0; attempt < 1_024; attempt++)
        {
            StoreStatus status = store.TryRemove(Key);
            if (status is StoreStatus.Success or StoreStatus.NotFound)
            {
                return;
            }

            if (status is not (StoreStatus.RemovePending or StoreStatus.StoreBusy))
            {
                Fail(family, repetition, seed, $"cleanup remove={status}, attempt={attempt}");
            }

            Thread.SpinWait(16 + (attempt & 31));
        }

        Fail(family, repetition, seed, "cleanup removal did not converge in 1024 attempts");
    }

    private static void RequireRelease(string family, int repetition, int seed, StoreStatus status)
    {
        if (status != StoreStatus.Success)
        {
            Fail(family, repetition, seed, $"lease release={status}");
        }
    }

    private void Record(string family, int repetitions, int seed, string? details = null)
    {
        _output.WriteLine(
            $"family={family} seed={seed} completed={repetitions} " +
            $"productionOperationRaces={repetitions} control=persistent-two-phase-barrier" +
            (details is null ? string.Empty : " " + details));
    }

    private static void Fail(string family, int repetition, int seed, string details)
    {
        throw new XunitException($"family={family}, repetition={repetition}, seed={seed}: {details}");
    }

    private static Store CreateStore(
        string family,
        int slotCount,
        int leaseCount,
        bool enableLeaseRecovery = false)
    {
        SharedMemoryStoreOptions options = CreateOptions(
            family,
            slotCount,
            leaseCount,
            OpenMode.CreateNew,
            enableLeaseRecovery);
        StoreOpenStatus status = Store.TryCreateOrOpen(options, out Store? store);
        if (status != StoreOpenStatus.Success || store is null)
        {
            throw new XunitException($"Could not create {family} production-race store: {status}.");
        }

        return store;
    }

    private static Store OpenStore(SharedMemoryStoreOptions source, OpenMode openMode)
    {
        var options = new SharedMemoryStoreOptions
        {
            Profile = source.Profile,
            Name = source.Name,
            OpenMode = openMode,
            TotalBytes = source.TotalBytes,
            SlotCount = source.SlotCount,
            MaxValueBytes = source.MaxValueBytes,
            MaxDescriptorBytes = source.MaxDescriptorBytes,
            MaxKeyBytes = source.MaxKeyBytes,
            LeaseRecordCount = source.LeaseRecordCount,
            ParticipantRecordCount = source.ParticipantRecordCount,
            EnableLeaseRecovery = source.EnableLeaseRecovery
        };
        StoreOpenStatus status = Store.TryCreateOrOpen(options, out Store? store);
        if (status != StoreOpenStatus.Success || store is null)
        {
            throw new XunitException($"Could not open production-race keeper: {status}.");
        }

        return store;
    }

    private static SharedMemoryStoreOptions CreateOptions(
        string family,
        int slotCount,
        int leaseCount,
        OpenMode openMode,
        bool enableLeaseRecovery = false) =>
        SharedMemoryStoreOptions.CreateLockFree(
            $"sms-sc011-{family}-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: leaseCount,
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery);

    private static int ConfiguredRepetitions()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_PRODUCTION_RACE_REPETITIONS");
        return int.TryParse(configured, out int value) && value > 0 ? value : DefaultRepetitions;
    }

    private static int ConfiguredSeed()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_PRODUCTION_RACE_SEED");
        return int.TryParse(configured, out int value) ? value : DefaultSeed;
    }

    private static int FamilySeed(int rootSeed, int family) =>
        unchecked(rootSeed + family * (int)0x9e37_79b9u);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class TwoActorRace : IDisposable
    {
        private readonly Action _first;
        private readonly Action _second;
        private readonly Thread _firstThread;
        private readonly Thread _secondThread;
        private int _epoch;
        private int _goEpoch;
        private int _ready;
        private int _completed;
        private int _firstDelay;
        private int _secondDelay;
        private int _stop;
        private Exception? _failure;

        internal TwoActorRace(Action first, Action second)
        {
            _first = first;
            _second = second;
            _firstThread = StartThread(firstActor: true);
            _secondThread = StartThread(firstActor: false);
        }

        internal void Run(int firstDelay, int secondDelay)
        {
            Volatile.Write(ref _firstDelay, firstDelay);
            Volatile.Write(ref _secondDelay, secondDelay);
            Volatile.Write(ref _ready, 0);
            Volatile.Write(ref _completed, 0);
            int epoch = unchecked(Volatile.Read(ref _epoch) + 1);
            Volatile.Write(ref _epoch, epoch);

            var waits = 0;
            while (Volatile.Read(ref _ready) != 2)
            {
                Thread.SpinWait(32);
                if ((++waits & 4_095) == 0)
                {
                    Thread.Yield();
                }
            }

            Volatile.Write(ref _goEpoch, epoch);
            waits = 0;
            while (Volatile.Read(ref _completed) != 2)
            {
                Thread.SpinWait(32);
                if ((++waits & 4_095) == 0)
                {
                    Thread.Yield();
                }
            }

            if (_failure is not null)
            {
                throw new XunitException($"Production race worker failed: {_failure.GetType().Name}: {_failure.Message}");
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref _stop, 1);
            Volatile.Write(ref _epoch, unchecked(Volatile.Read(ref _epoch) + 1));
            if (!_firstThread.Join(TimeSpan.FromSeconds(10)) || !_secondThread.Join(TimeSpan.FromSeconds(10)))
            {
                throw new XunitException("Production race workers did not stop.");
            }
        }

        private Thread StartThread(bool firstActor)
        {
            var thread = new Thread(() => Worker(firstActor))
            {
                IsBackground = true,
                Name = firstActor ? "sms-sc011-first" : "sms-sc011-second"
            };
            thread.Start();
            return thread;
        }

        private void Worker(bool firstActor)
        {
            int observedEpoch = 0;
            while (true)
            {
                int epoch;
                var waits = 0;
                while ((epoch = Volatile.Read(ref _epoch)) == observedEpoch)
                {
                    Thread.SpinWait(32);
                    if ((++waits & 4_095) == 0)
                    {
                        Thread.Yield();
                    }
                }

                observedEpoch = epoch;
                if (Volatile.Read(ref _stop) != 0)
                {
                    return;
                }

                try
                {
                    Interlocked.Increment(ref _ready);
                    waits = 0;
                    while (Volatile.Read(ref _goEpoch) != epoch)
                    {
                        Thread.SpinWait(32);
                        if ((++waits & 4_095) == 0)
                        {
                            Thread.Yield();
                        }
                    }

                    Thread.SpinWait(firstActor ? Volatile.Read(ref _firstDelay) : Volatile.Read(ref _secondDelay));
                    if (firstActor)
                    {
                        _first();
                    }
                    else
                    {
                        _second();
                    }
                }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref _failure, error, null);
                }
                finally
                {
                    Interlocked.Increment(ref _completed);
                }
            }
        }
    }

    private struct DeterministicRandom
    {
        private uint _state;

        internal DeterministicRandom(int seed)
        {
            _state = unchecked((uint)seed);
            if (_state == 0)
            {
                _state = 0xa341_316cu;
            }
        }

        internal int NextDelay()
        {
            uint value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return (int)(value & 63u);
        }
    }
}
