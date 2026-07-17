using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeStoreFullRegressionTests
{
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(10);

    [Fact]
    public void CancellationAtClaimAdvancesGenerationInsteadOfRestoringSameFreeControl()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var resources = new ConcurrentQueue<LockFreeSlotResourceEvent>();
        var canceledClaim = 0;
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(
            static _ => { },
            resource =>
            {
                resources.Enqueue(resource);
                if (resource.Kind == LockFreeSlotResourceEventKind.Claim
                    && Interlocked.CompareExchange(ref canceledClaim, 1, 0) == 0)
                {
                    cancellation.Cancel();
                }
            });

        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-claim-cancel-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            checkpoint);
        LockFreeSlotTable slots = ReadSlots(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        long initialFree = AtomicControlWord.LoadAcquire(ref slot.Control);
        long initialGeneration = SlotGeneration(initialFree);

        StoreStatus status = store.TryPublish(
            [0x11],
            [0x21],
            default,
            new StoreWaitOptions(Timeout.InfiniteTimeSpan, cancellation.Token));

        Assert.Equal(StoreStatus.OperationCanceled, status);
        Assert.Equal(LockFreeSlotTable.FreeState, SlotState(initialFree));
        LockFreeSlotResourceEvent claim = Assert.Single(
            resources,
            static item => item.Kind == LockFreeSlotResourceEventKind.Claim);
        LockFreeSlotResourceEvent release = Assert.Single(
            resources,
            static item => item.Kind is LockFreeSlotResourceEventKind.Free
                or LockFreeSlotResourceEventKind.Retire);
        Assert.Equal(0, claim.SlotIndex);
        Assert.Equal(initialGeneration, claim.Generation);
        Assert.Equal(claim.SlotIndex, release.SlotIndex);
        Assert.Equal(claim.Generation, release.Generation);

        long advancedFree = AtomicControlWord.LoadAcquire(ref slot.Control);
        Assert.NotEqual(initialFree, advancedFree);
        Assert.Equal(LockFreeSlotTable.FreeState, SlotState(advancedFree));
        Assert.Equal(initialGeneration + 1, SlotGeneration(advancedFree));
        Assert.Equal((int)SlotPublicationIntent.None, Volatile.Read(ref slot.PublicationIntent));

        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish([0x12], [0x22], default, StoreWaitOptions.Infinite));
        LockFreeSlotResourceEvent replacementClaim = resources
            .Where(static item => item.Kind == LockFreeSlotResourceEventKind.Claim)
            .Last();
        Assert.Equal(initialGeneration + 1, replacementClaim.Generation);
    }

    [Fact]
    public async Task StoreFullProofGateIsPerHandleAndRespectsNoWaitAndInfinitePolicies()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-full-proof-gate-{Guid.NewGuid():N}";
        using var pause = new ProofPause();
        var resources = new ConcurrentQueue<LockFreeSlotResourceEvent>();
        var proofs = new ProofRecorder();
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(
            pause.Observe,
            resources.Enqueue,
            proofs);
        using MemoryStore firstHandle = CreateInstrumentedStore(name, OpenMode.CreateNew, checkpoint);
        using MemoryStore otherHandle = OpenOrdinaryStore(name);
        Assert.Equal(StoreStatus.Success, firstHandle.TryPublish([0x21], [0x31]));
        resources.Clear();

        Task<StoreStatus> heldProof = Task.Run(() => firstHandle.TryPublish(
            [0x22],
            [0x32],
            default,
            StoreWaitOptions.Infinite));

        Task<StoreStatus>? infiniteContender = null;
        try
        {
            pause.WaitUntilProofPaused();

            Assert.Equal(
                StoreStatus.StoreBusy,
                firstHandle.TryPublish([0x23], [0x33], default, StoreWaitOptions.NoWait));

            // The proof gate is process-local and per open handle. Holding it on
            // firstHandle must not prevent another handle from proving the same
            // stable physical capacity result.
            Assert.Equal(
                StoreStatus.StoreFull,
                otherHandle.TryPublish([0x24], [0x34], default, StoreWaitOptions.Infinite));

            infiniteContender = Task.Run(() =>
            {
                pause.WatchInfiniteContenderThread();
                return firstHandle.TryPublish(
                    [0x25],
                    [0x35],
                    default,
                    StoreWaitOptions.Infinite);
            });
            pause.WaitUntilInfiniteContenderEntered();
            Assert.False(infiniteContender.IsCompleted);

            pause.ResumeProof();
            Assert.Equal(StoreStatus.StoreFull, await heldProof.WaitAsync(TestBound));
            Assert.Equal(StoreStatus.StoreFull, await infiniteContender.WaitAsync(TestBound));

            ProofObservation[] proofEvents = proofs.Snapshot();
            Assert.Equal(2, proofEvents.Count(static item =>
                item.Kind == ProofObservationKind.Candidate));
            Assert.Equal(2, proofEvents.Count(static item =>
                item.Kind == ProofObservationKind.Confirmed));
            Assert.DoesNotContain(
                proofEvents,
                static item => item.Kind == ProofObservationKind.Rejected);
            Assert.Equal(
                proofEvents
                    .Where(static item => item.Kind == ProofObservationKind.Candidate)
                    .Select(static item => item.Token)
                    .Order(),
                proofEvents
                    .Where(static item => item.Kind == ProofObservationKind.Confirmed)
                    .Select(static item => item.Token)
                    .Order());
            Assert.All(proofEvents, static item => Assert.Equal(1, item.SlotCount));
        }
        finally
        {
            pause.ResumeProof();
            _ = await heldProof.WaitAsync(TestBound);
            if (infiniteContender is not null)
            {
                _ = await infiniteContender.WaitAsync(TestBound);
            }
        }
    }

    [Fact]
    public async Task MovementBetweenCollectsReturnsStoreBusyForNoWaitWithoutConfirmingFull()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-full-proof-movement-{Guid.NewGuid():N}";
        using var pause = new ProofPause();
        var resources = new ConcurrentQueue<LockFreeSlotResourceEvent>();
        var proofs = new ProofRecorder();
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(
            pause.Observe,
            resources.Enqueue,
            proofs);
        using MemoryStore store = CreateInstrumentedStore(name, OpenMode.CreateNew, checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x31], [0x41]));
        resources.Clear();

        Task<StoreStatus> contender = Task.Run(() => store.TryPublish(
            [0x32],
            [0x42],
            default,
            StoreWaitOptions.NoWait));
        try
        {
            pause.WaitUntilProofPaused();
            Assert.Equal(StoreStatus.Success, store.TryRemove([0x31], StoreWaitOptions.Infinite));
            pause.ResumeProof();

            Assert.Equal(StoreStatus.StoreBusy, await contender.WaitAsync(TestBound));
            ProofObservation candidate = Assert.Single(
                proofs.Snapshot(),
                static item => item.Kind == ProofObservationKind.Candidate);
            ProofObservation rejected = Assert.Single(
                proofs.Snapshot(),
                static item => item.Kind == ProofObservationKind.Rejected);
            Assert.Equal(candidate.Token, rejected.Token);
            Assert.DoesNotContain(
                proofs.Snapshot(),
                static item => item.Kind == ProofObservationKind.Confirmed);

            Assert.Equal(
                StoreStatus.Success,
                store.TryPublish([0x32], [0x42], default, StoreWaitOptions.Infinite));
        }
        finally
        {
            pause.ResumeProof();
            _ = await contender.WaitAsync(TestBound);
        }
    }

    [Fact]
    public void MalformedControlIntroducedAfterFirstCollectFailsClosedOnSecondCollect()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        LockFreeSlotTable? slots = null;
        long originalControl = 0;
        var injected = 0;
        var resources = new ConcurrentQueue<LockFreeSlotResourceEvent>();
        var proofs = new ProofRecorder();
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(
            entry =>
            {
                if (entry.Id != LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification
                    || Interlocked.CompareExchange(ref injected, 1, 0) != 0)
                {
                    return;
                }

                LockFreeSlotTable table = Assert.IsType<LockFreeSlotTable>(slots);
                ref ValueSlotMetadataV2 slot = ref table.Slot(0);
                originalControl = AtomicControlWord.LoadAcquire(ref slot.Control);
                long malformed = unchecked((long)AtomicControlWord.EncodeSlot(
                    LockFreeSlotTable.PublishedState,
                    SlotGeneration(originalControl),
                    participantToken: 1));
                AtomicControlWord.StoreRelease(ref slot.Control, malformed);
            },
            resources.Enqueue,
            proofs);

        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-full-proof-malformed-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x41], [0x51]));
        slots = ReadSlots(store);
        resources.Clear();

        StoreStatus status;
        try
        {
            status = store.TryPublish([0x42], [0x52], default, StoreWaitOptions.Infinite);
        }
        finally
        {
            if (Volatile.Read(ref injected) != 0)
            {
                ref ValueSlotMetadataV2 slot = ref Assert.IsType<LockFreeSlotTable>(slots).Slot(0);
                AtomicControlWord.StoreRelease(ref slot.Control, originalControl);
            }
        }

        Assert.Equal(1, Volatile.Read(ref injected));
        Assert.Equal(StoreStatus.CorruptStore, status);
        ProofObservation candidate = Assert.Single(
            proofs.Snapshot(),
            static item => item.Kind == ProofObservationKind.Candidate);
        ProofObservation rejected = Assert.Single(
            proofs.Snapshot(),
            static item => item.Kind == ProofObservationKind.Rejected);
        Assert.Equal(candidate.Token, rejected.Token);
        Assert.DoesNotContain(
            proofs.Snapshot(),
            static item => item.Kind == ProofObservationKind.Confirmed);

        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire([0x41], out _));
    }

    [Fact]
    public void StoreFullProofRejectsOwnedControlWithOutOfRangeParticipantIndex()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-full-proof-participant-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { }));
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                [0x61],
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));

        LockFreeSlotTable slots = ReadSlots(store);
        IndexBinding binding = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long originalControl = AtomicControlWord.LoadAcquire(ref slot.Control);
        const int malformedParticipant = 13; // generation 1, index-plus-one 5 for count 4
        Assert.False(ParticipantToken.IsStructurallyValid(malformedParticipant, 4));
        long malformedControl = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.ReservedState,
            binding.Generation,
            malformedParticipant));
        AtomicControlWord.StoreRelease(ref slot.Control, malformedControl);

        try
        {
            NoOpLockFreeCheckpoint checkpoint = default;
            StoreStatus status = slots.TryProveStoreFull(
                LockFreeOperationBudget.StructuralAttempt,
                ref checkpoint,
                out bool provenFull);

            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.False(provenFull);
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref slot.Control, originalControl);
        }

        Assert.Equal(StoreStatus.CorruptStore, reservation.Abort(StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.CorruptStore, store.TryPublish([0x62], [0x72]));
    }

    private static MemoryStore CreateInstrumentedStore(
        string name,
        OpenMode openMode,
        InstrumentedLockFreeCheckpoint checkpoint)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options(name, openMode),
            checkpoint,
            out MemoryStore? candidate);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(candidate);
    }

    private static MemoryStore OpenOrdinaryStore(string name)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, OpenMode.OpenExisting),
            out MemoryStore? candidate);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(candidate);
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount: 1,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 2,
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery: true);

    private static LockFreeSlotTable ReadSlots(MemoryStore store)
    {
        FieldInfo engineField = typeof(MemoryStore).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MemoryStore._engine is absent.");
        object engine = engineField.GetValue(store)
            ?? throw new InvalidOperationException("MemoryStore._engine is null.");
        FieldInfo slotsField = engine.GetType().GetField(
            "_slots",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Lock-free engine._slots is absent.");
        return Assert.IsType<LockFreeSlotTable>(slotsField.GetValue(engine));
    }

    private static int SlotState(long control) => (int)(unchecked((ulong)control) & 0x7UL);

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private enum ProofObservationKind
    {
        Candidate,
        Confirmed,
        Rejected
    }

    private readonly record struct ProofObservation(
        ProofObservationKind Kind,
        long Token,
        int SlotCount);

    private sealed class ProofRecorder : ILockFreeStoreFullProofObserver
    {
        private readonly ConcurrentQueue<ProofObservation> _observations = new();
        private long _nextToken;

        public long BeginCandidate(int slotCount)
        {
            long token = Interlocked.Increment(ref _nextToken);
            _observations.Enqueue(new ProofObservation(
                ProofObservationKind.Candidate,
                token,
                slotCount));
            return token;
        }

        public void CompleteCandidate(long token, bool confirmed)
        {
            int slotCount = _observations
                .First(item => item.Kind == ProofObservationKind.Candidate && item.Token == token)
                .SlotCount;
            _observations.Enqueue(new ProofObservation(
                confirmed ? ProofObservationKind.Confirmed : ProofObservationKind.Rejected,
                token,
                slotCount));
        }

        internal ProofObservation[] Snapshot() => _observations.ToArray();
    }

    private sealed class ProofPause : IDisposable
    {
        private readonly ManualResetEventSlim _proofPaused = new(initialState: false);
        private readonly ManualResetEventSlim _resumeProof = new(initialState: false);
        private readonly ManualResetEventSlim _infiniteContenderEntered = new(initialState: false);
        private int _proofPauseClaimed;
        private int _infiniteContenderThreadId;

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id == LockFreeCheckpointId.PublishBeforeSlotClaim
                && Environment.CurrentManagedThreadId
                    == Volatile.Read(ref _infiniteContenderThreadId))
            {
                _infiniteContenderEntered.Set();
            }

            if (entry.Id != LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification
                || Interlocked.CompareExchange(ref _proofPauseClaimed, 1, 0) != 0)
            {
                return;
            }

            _proofPaused.Set();
            if (!_resumeProof.Wait(TestBound))
            {
                throw new Xunit.Sdk.XunitException(
                    "Paused StoreFull proof was not resumed within the test bound.");
            }
        }

        internal void WatchInfiniteContenderThread() =>
            Volatile.Write(ref _infiniteContenderThreadId, Environment.CurrentManagedThreadId);

        internal void WaitUntilProofPaused() => Assert.True(
            _proofPaused.Wait(TestBound),
            "The StoreFull first-collect checkpoint was not reached.");

        internal void WaitUntilInfiniteContenderEntered() => Assert.True(
            _infiniteContenderEntered.Wait(TestBound),
            "The infinite contender did not enter the publish operation.");

        internal void ResumeProof() => _resumeProof.Set();

        public void Dispose()
        {
            _resumeProof.Set();
            _proofPaused.Dispose();
            _resumeProof.Dispose();
            _infiniteContenderEntered.Dispose();
        }
    }
}
