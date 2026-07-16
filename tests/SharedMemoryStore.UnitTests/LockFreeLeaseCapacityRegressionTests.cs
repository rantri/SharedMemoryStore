using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeLeaseCapacityRegressionTests
{
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(10);

    [Fact]
    public void StableCapacityEmitsOneExactProofAndReturnsLeaseTableFull()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        var proofs = new LeaseProofRecorder();
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { }, proofs);
        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-lease-full-proof-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            leaseRecordCount: 2,
            checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x11], [0x21]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0x11], out ValueLease first));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0x11], out ValueLease second));

        Assert.Equal(
            StoreStatus.LeaseTableFull,
            store.TryAcquire([0x11], StoreWaitOptions.Infinite, out ValueLease rejected));
        Assert.False(rejected.IsValid);

        LeaseProofObservation candidate = Assert.Single(
            proofs.Snapshot(),
            static item => item.Kind == LeaseProofObservationKind.Candidate);
        LeaseProofObservation confirmed = Assert.Single(
            proofs.Snapshot(),
            static item => item.Kind == LeaseProofObservationKind.Confirmed);
        Assert.Equal(candidate.Token, confirmed.Token);
        Assert.Equal(2, candidate.LeaseRecordCount);
        Assert.Equal(candidate.LeaseRecordCount, confirmed.LeaseRecordCount);
        Assert.DoesNotContain(
            proofs.Snapshot(),
            static item => item.Kind == LeaseProofObservationKind.Rejected);

        Assert.Equal(StoreStatus.Success, first.Release());
        Assert.Equal(StoreStatus.Success, second.Release());
    }

    [Fact]
    public async Task ProofGateIsPerHandleAndRespectsNoWaitFiniteAndInfinitePolicies()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-lease-full-gate-{Guid.NewGuid():N}";
        using var pause = new LeaseProofPause();
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { }, pause);
        using MemoryStore firstHandle = CreateInstrumentedStore(
            name,
            OpenMode.CreateNew,
            leaseRecordCount: 1,
            checkpoint);
        using MemoryStore otherHandle = OpenOrdinaryStore(
            name,
            leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, firstHandle.TryPublish([0x21], [0x31]));
        Assert.Equal(StoreStatus.Success, firstHandle.TryAcquire([0x21], out ValueLease held));

        Task<StoreStatus> heldProof = Task.Run(() => firstHandle.TryAcquire(
            [0x21],
            StoreWaitOptions.Infinite,
            out _));
        Task<StoreStatus>? infiniteContender = null;
        try
        {
            pause.WaitUntilProofPaused();

            Assert.Equal(
                StoreStatus.StoreBusy,
                firstHandle.TryAcquire([0x21], StoreWaitOptions.NoWait, out _));

            var started = Stopwatch.StartNew();
            Assert.Equal(
                StoreStatus.StoreBusy,
                firstHandle.TryAcquire(
                    [0x21],
                    new StoreWaitOptions(TimeSpan.FromMilliseconds(50)),
                    out _));
            Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            // Proof state is private to one open handle. A second handle may
            // independently prove the same stable physical lease exhaustion.
            Assert.Equal(
                StoreStatus.LeaseTableFull,
                otherHandle.TryAcquire([0x21], StoreWaitOptions.Infinite, out _));

            using var contenderEntered = new ManualResetEventSlim(initialState: false);
            infiniteContender = Task.Run(() =>
            {
                contenderEntered.Set();
                return firstHandle.TryAcquire(
                    [0x21],
                    StoreWaitOptions.Infinite,
                    out _);
            });
            Assert.True(contenderEntered.Wait(TestBound));
            Assert.False(infiniteContender.IsCompleted);

            pause.ResumeProof();
            Assert.Equal(StoreStatus.LeaseTableFull, await heldProof.WaitAsync(TestBound));
            Assert.Equal(
                StoreStatus.LeaseTableFull,
                await infiniteContender.WaitAsync(TestBound));
        }
        finally
        {
            pause.ResumeProof();
            _ = await heldProof.WaitAsync(TestBound);
            if (infiniteContender is not null)
            {
                _ = await infiniteContender.WaitAsync(TestBound);
            }

            Assert.Equal(StoreStatus.Success, held.Release());
        }
    }

    [Fact]
    public async Task ReleaseBetweenCollectsReturnsStoreBusyForNoWaitWithoutConfirmingFull()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var pause = new LeaseProofPause();
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { }, pause);
        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-lease-full-movement-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            leaseRecordCount: 1,
            checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x31], [0x41]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0x31], out ValueLease held));

        Task<StoreStatus> contender = Task.Run(() => store.TryAcquire(
            [0x31],
            StoreWaitOptions.NoWait,
            out _));
        try
        {
            pause.WaitUntilProofPaused();
            Assert.Equal(StoreStatus.Success, held.Release());
            pause.ResumeProof();

            Assert.Equal(StoreStatus.StoreBusy, await contender.WaitAsync(TestBound));
            LeaseProofObservation candidate = Assert.Single(
                pause.Snapshot(),
                static item => item.Kind == LeaseProofObservationKind.Candidate);
            LeaseProofObservation rejected = Assert.Single(
                pause.Snapshot(),
                static item => item.Kind == LeaseProofObservationKind.Rejected);
            Assert.Equal(candidate.Token, rejected.Token);
            Assert.DoesNotContain(
                pause.Snapshot(),
                static item => item.Kind == LeaseProofObservationKind.Confirmed);

            Assert.Equal(
                StoreStatus.Success,
                store.TryAcquire([0x31], StoreWaitOptions.Infinite, out ValueLease replacement));
            Assert.Equal(StoreStatus.Success, replacement.Release());
        }
        finally
        {
            pause.ResumeProof();
            _ = await contender.WaitAsync(TestBound);
            if (held.IsValid)
            {
                Assert.Equal(StoreStatus.Success, held.Release());
            }
        }
    }

    [Fact]
    public async Task BindingRemovedBeforeFullCandidateReturnsNotFoundAfterExactProof()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var pause = new AcquireBeforeClaimPause();
        var proofs = new LeaseProofRecorder();
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(pause.Observe, proofs);
        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-lease-full-binding-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            leaseRecordCount: 1,
            checkpoint,
            slotCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x61], [0x71]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x62], [0x72]));

        Task<StoreStatus> targetAcquire = Task.Run(() => store.TryAcquire(
            [0x61],
            StoreWaitOptions.Infinite,
            out _));
        ValueLease blocker = default;
        try
        {
            pause.WaitUntilPaused();
            Assert.Equal(
                StoreStatus.Success,
                store.TryRemove([0x61], StoreWaitOptions.Infinite));
            Assert.Equal(
                StoreStatus.Success,
                store.TryAcquire([0x62], StoreWaitOptions.Infinite, out blocker));
            pause.Resume();

            Assert.Equal(StoreStatus.NotFound, await targetAcquire.WaitAsync(TestBound));
            Assert.Single(
                proofs.Snapshot(),
                static item => item.Kind == LeaseProofObservationKind.Confirmed);
        }
        finally
        {
            pause.Resume();
            _ = await targetAcquire.WaitAsync(TestBound);
            if (blocker.IsValid)
            {
                Assert.Equal(StoreStatus.Success, blocker.Release());
            }
        }
    }

    [Theory]
    [InlineData(MalformedLeaseControl.InvalidState)]
    [InlineData(MalformedLeaseControl.ZeroIncarnation)]
    [InlineData(MalformedLeaseControl.FreeWithOwner)]
    [InlineData(MalformedLeaseControl.ClaimingWithoutOwner)]
    [InlineData(MalformedLeaseControl.ReleasingWithOwner)]
    [InlineData(MalformedLeaseControl.NonterminalRetired)]
    [InlineData(MalformedLeaseControl.InvalidParticipantIndex)]
    public void MalformedControlIntroducedAfterFirstCollectFailsClosed(
        MalformedLeaseControl malformedKind)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        LockFreeLeaseRegistry? registry = null;
        long originalControl = 0;
        var proofs = new LeaseProofRecorder(() =>
        {
            ref LeaseRecordV2 record = ref Assert.IsType<LockFreeLeaseRegistry>(registry).Record(0);
            originalControl = AtomicControlWord.LoadAcquire(ref record.Control);
            long malformed = CreateMalformedControl(malformedKind, originalControl);
            AtomicControlWord.StoreRelease(ref record.Control, malformed);
        });
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { }, proofs);
        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-lease-full-malformed-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            leaseRecordCount: 1,
            checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x41], [0x51]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0x41], out ValueLease held));
        registry = ReadLeases(store);

        StoreStatus status;
        try
        {
            status = store.TryAcquire(
                [0x41],
                StoreWaitOptions.Infinite,
                out _);
        }
        finally
        {
            if (originalControl != 0)
            {
                AtomicControlWord.StoreRelease(
                    ref Assert.IsType<LockFreeLeaseRegistry>(registry).Record(0).Control,
                    originalControl);
            }
        }

        Assert.Equal(StoreStatus.CorruptStore, status);
        LeaseProofObservation candidate = Assert.Single(
            proofs.Snapshot(),
            static item => item.Kind == LeaseProofObservationKind.Candidate);
        LeaseProofObservation rejected = Assert.Single(
            proofs.Snapshot(),
            static item => item.Kind == LeaseProofObservationKind.Rejected);
        Assert.Equal(candidate.Token, rejected.Token);
        Assert.DoesNotContain(
            proofs.Snapshot(),
            static item => item.Kind == LeaseProofObservationKind.Confirmed);
        Assert.Equal(StoreStatus.CorruptStore, held.Release());
    }

    [Fact]
    public void MalformedControlInFirstCollectFailsClosedWithoutCapacityCandidate()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        var proofs = new LeaseProofRecorder();
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(static _ => { }, proofs);
        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-lease-full-first-malformed-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            leaseRecordCount: 1,
            checkpoint);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x51], [0x61]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0x51], out ValueLease held));
        LockFreeLeaseRegistry registry = ReadLeases(store);
        ref LeaseRecordV2 record = ref registry.Record(0);
        long original = AtomicControlWord.LoadAcquire(ref record.Control);
        AtomicControlWord.StoreRelease(
            ref record.Control,
            CreateMalformedControl(MalformedLeaseControl.InvalidParticipantIndex, original));

        StoreStatus status;
        try
        {
            status = store.TryAcquire([0x51], StoreWaitOptions.Infinite, out _);
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref record.Control, original);
        }

        Assert.Equal(StoreStatus.CorruptStore, status);
        Assert.Empty(proofs.Snapshot());
        Assert.Equal(StoreStatus.CorruptStore, held.Release());
    }

    private static long CreateMalformedControl(
        MalformedLeaseControl kind,
        long originalControl)
    {
        const int invalidParticipantToken = 13;
        int owner = checked((int)((unchecked((ulong)originalControl) >> 36) & 0x0fff_ffffUL));
        Assert.NotEqual(0, owner);
        return kind switch
        {
            MalformedLeaseControl.InvalidState => Signed(AtomicControlWord.EncodeLease(
                state: 6,
                generation: 1,
                participantToken: 0)),
            MalformedLeaseControl.ZeroIncarnation =>
                LockFreeLeaseRegistry.ActiveState | ((long)owner << 36),
            MalformedLeaseControl.FreeWithOwner => Signed(AtomicControlWord.EncodeLease(
                LockFreeLeaseRegistry.FreeState,
                generation: 1,
                owner)),
            MalformedLeaseControl.ClaimingWithoutOwner => Signed(AtomicControlWord.EncodeLease(
                LockFreeLeaseRegistry.ClaimingState,
                generation: 1,
                participantToken: 0)),
            MalformedLeaseControl.ReleasingWithOwner => Signed(AtomicControlWord.EncodeLease(
                LockFreeLeaseRegistry.ReleasingState,
                generation: 1,
                owner)),
            MalformedLeaseControl.NonterminalRetired => Signed(AtomicControlWord.EncodeLease(
                LockFreeLeaseRegistry.RetiredState,
                generation: 1,
                participantToken: 0)),
            MalformedLeaseControl.InvalidParticipantIndex => Signed(AtomicControlWord.EncodeLease(
                LockFreeLeaseRegistry.ActiveState,
                generation: 1,
                invalidParticipantToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static long Signed(ulong value) => unchecked((long)value);

    private static MemoryStore CreateInstrumentedStore(
        string name,
        OpenMode openMode,
        int leaseRecordCount,
        InstrumentedLockFreeCheckpoint checkpoint,
        int slotCount = 1)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options(name, openMode, leaseRecordCount, slotCount),
            checkpoint,
            out MemoryStore? candidate);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(candidate);
    }

    private static MemoryStore OpenOrdinaryStore(string name, int leaseRecordCount)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, OpenMode.OpenExisting, leaseRecordCount, slotCount: 1),
            out MemoryStore? candidate);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(candidate);
    }

    private static SharedMemoryStoreOptions Options(
        string name,
        OpenMode openMode,
        int leaseRecordCount,
        int slotCount) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount,
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery: true);

    private static LockFreeLeaseRegistry ReadLeases(MemoryStore store)
    {
        FieldInfo engineField = typeof(MemoryStore).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MemoryStore._engine is absent.");
        object engine = engineField.GetValue(store)
            ?? throw new InvalidOperationException("MemoryStore._engine is null.");
        FieldInfo leasesField = engine.GetType().GetField(
            "_leases",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Lock-free engine._leases is absent.");
        return Assert.IsType<LockFreeLeaseRegistry>(leasesField.GetValue(engine));
    }

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public enum MalformedLeaseControl
    {
        InvalidState,
        ZeroIncarnation,
        FreeWithOwner,
        ClaimingWithoutOwner,
        ReleasingWithOwner,
        NonterminalRetired,
        InvalidParticipantIndex
    }

    private enum LeaseProofObservationKind
    {
        Candidate,
        Confirmed,
        Rejected
    }

    private readonly record struct LeaseProofObservation(
        LeaseProofObservationKind Kind,
        long Token,
        int LeaseRecordCount);

    private class LeaseProofRecorder : ILockFreeLeaseTableFullProofObserver
    {
        private readonly ConcurrentQueue<LeaseProofObservation> _observations = new();
        private readonly Action? _candidateAction;
        private long _nextToken;

        internal LeaseProofRecorder(Action? candidateAction = null)
        {
            _candidateAction = candidateAction;
        }

        public virtual long BeginCandidate(int leaseRecordCount)
        {
            long token = Interlocked.Increment(ref _nextToken);
            _observations.Enqueue(new LeaseProofObservation(
                LeaseProofObservationKind.Candidate,
                token,
                leaseRecordCount));
            _candidateAction?.Invoke();
            return token;
        }

        public void CompleteCandidate(long token, bool confirmed)
        {
            int leaseRecordCount = _observations
                .First(item =>
                    item.Kind == LeaseProofObservationKind.Candidate
                    && item.Token == token)
                .LeaseRecordCount;
            _observations.Enqueue(new LeaseProofObservation(
                confirmed
                    ? LeaseProofObservationKind.Confirmed
                    : LeaseProofObservationKind.Rejected,
                token,
                leaseRecordCount));
        }

        internal LeaseProofObservation[] Snapshot() => _observations.ToArray();
    }

    private sealed class LeaseProofPause : LeaseProofRecorder, IDisposable
    {
        private readonly ManualResetEventSlim _proofPaused = new(initialState: false);
        private readonly ManualResetEventSlim _resumeProof = new(initialState: false);
        private int _proofPauseClaimed;

        public override long BeginCandidate(int leaseRecordCount)
        {
            long token = base.BeginCandidate(leaseRecordCount);
            if (Interlocked.CompareExchange(ref _proofPauseClaimed, 1, 0) == 0)
            {
                _proofPaused.Set();
                if (!_resumeProof.Wait(TestBound))
                {
                    throw new Xunit.Sdk.XunitException(
                        "Paused LeaseTableFull proof was not resumed within the test bound.");
                }
            }

            return token;
        }

        internal void WaitUntilProofPaused() => Assert.True(
            _proofPaused.Wait(TestBound),
            "The LeaseTableFull proof candidate was not reached.");

        internal void ResumeProof() => _resumeProof.Set();

        public void Dispose()
        {
            _resumeProof.Set();
            _proofPaused.Dispose();
            _resumeProof.Dispose();
        }
    }

    private sealed class AcquireBeforeClaimPause : IDisposable
    {
        private readonly ManualResetEventSlim _paused = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private int _claimed;

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id != LockFreeCheckpointId.AcquireBeforeLeaseClaimCas
                || Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                return;
            }

            _paused.Set();
            if (!_resume.Wait(TestBound))
            {
                throw new Xunit.Sdk.XunitException(
                    "Acquire-before-claim pause was not resumed within the test bound.");
            }
        }

        internal void WaitUntilPaused() => Assert.True(
            _paused.Wait(TestBound),
            "Acquire did not reach the before-lease-claim checkpoint.");

        internal void Resume() => _resume.Set();

        public void Dispose()
        {
            _resume.Set();
            _paused.Dispose();
            _resume.Dispose();
        }
    }
}
