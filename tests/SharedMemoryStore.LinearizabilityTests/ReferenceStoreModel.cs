using System.Collections.Concurrent;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.LinearizabilityTests;

internal enum ReferenceOperationKind
{
    OpenParticipant,
    CloseParticipant,
    Publish,
    Reserve,
    CommitReservation,
    AbortReservation,
    Acquire,
    AcquireLease,
    ReleaseLease,
    Remove,
    RecoverReservation,
    RecoverLease,
    DisposeParticipant
}

internal enum ReferenceResultCode
{
    Success,
    DuplicateKey,
    NotFound,
    StoreFull,
    ParticipantTableFull,
    ParticipantNotActive,
    LeaseTableFull,
    RemovePending,
    InvalidLease,
    LeaseAlreadyReleased,
    InvalidReservation,
    ReservationAlreadyCompleted,
    StoreDisposed,
    OperationCanceled,
    StoreBusy,
    Unexpected
}

internal readonly record struct ReferenceCommand(
    ReferenceOperationKind Kind,
    int ParticipantId,
    string Key,
    string Value,
    int TokenId,
    int ByteCount)
{
    public static ReferenceCommand OpenParticipant(int participantId) =>
        new(ReferenceOperationKind.OpenParticipant, participantId, string.Empty, string.Empty, 0, 0);

    public static ReferenceCommand CloseParticipant(int participantId) =>
        new(ReferenceOperationKind.CloseParticipant, participantId, string.Empty, string.Empty, 0, 0);

    public static ReferenceCommand Publish(int participantId, string key, string value) =>
        new(ReferenceOperationKind.Publish, participantId, key, value, 0, 0);

    public static ReferenceCommand Reserve(
        int participantId,
        int reservationId,
        string key,
        string value) =>
        new(ReferenceOperationKind.Reserve, participantId, key, value, reservationId, value.Length);

    public static ReferenceCommand CommitReservation(int participantId, int reservationId) =>
        new(ReferenceOperationKind.CommitReservation, participantId, string.Empty, string.Empty, reservationId, 0);

    public static ReferenceCommand AbortReservation(int participantId, int reservationId) =>
        new(ReferenceOperationKind.AbortReservation, participantId, string.Empty, string.Empty, reservationId, 0);

    public static ReferenceCommand Acquire(int participantId, string key) =>
        new(ReferenceOperationKind.Acquire, participantId, key, string.Empty, 0, 0);

    public static ReferenceCommand AcquireLease(int participantId, int leaseId, string key) =>
        new(ReferenceOperationKind.AcquireLease, participantId, key, string.Empty, leaseId, 0);

    public static ReferenceCommand ReleaseLease(int participantId, int leaseId) =>
        new(ReferenceOperationKind.ReleaseLease, participantId, string.Empty, string.Empty, leaseId, 0);

    public static ReferenceCommand Remove(int participantId, string key) =>
        new(ReferenceOperationKind.Remove, participantId, key, string.Empty, 0, 0);

    public static ReferenceCommand RecoverReservation(int participantId, int reservationId) =>
        new(ReferenceOperationKind.RecoverReservation, participantId, string.Empty, string.Empty, reservationId, 0);

    public static ReferenceCommand RecoverLease(int participantId, int leaseId) =>
        new(ReferenceOperationKind.RecoverLease, participantId, string.Empty, string.Empty, leaseId, 0);

    public static ReferenceCommand DisposeParticipant(int participantId) =>
        new(ReferenceOperationKind.DisposeParticipant, participantId, string.Empty, string.Empty, 0, 0);
}

internal sealed record RecordedOperation(
    int Id,
    int ActorId,
    ReferenceCommand Command,
    ReferenceResultCode Result,
    long InvocationSequence,
    long EntrySequence,
    long ReturnSequence,
    long ResponseSequence,
    string? ObservedValue = null,
    long ObservedGeneration = 0,
    bool RequiresAcquireObservation = false,
    bool UsesInfiniteWait = false)
{
    public bool HasValidCallEnvelope =>
        InvocationSequence < EntrySequence
        && EntrySequence < ReturnSequence
        && ReturnSequence < ResponseSequence;

    public bool HappensBefore(RecordedOperation other) =>
        ResponseSequence < other.InvocationSequence;

    public bool Overlaps(RecordedOperation other) =>
        !HappensBefore(other) && !other.HappensBefore(this);

    /// <summary>
    /// Returns whether the two calls were simultaneously inside the mapped
    /// implementation. Invocation/response overlap alone proves no mapped
    /// race because callers can create both invocation records in advance.
    /// </summary>
    public bool ImplementationOverlaps(RecordedOperation other) =>
        EntrySequence < other.ReturnSequence
        && other.EntrySequence < ReturnSequence;
}

internal enum RecordedSlotResourceKind
{
    Claim,
    Free,
    Retire,
    StoreFullProof,
    LeaseTableFullProof
}

internal readonly record struct RecordedSlotResourceWitness(
    RecordedSlotResourceKind Kind,
    int SlotIndex,
    long Generation,
    long Sequence,
    long ConfirmationSequence = 0);

internal sealed class MonotonicHistoryRecorder :
    ILockFreeStoreFullProofObserver,
    ILockFreeLeaseTableFullProofObserver
{
    private readonly ConcurrentDictionary<int, RecordedOperation> _operations = new();
    private readonly ConcurrentQueue<RecordedSlotResourceWitness> _slotResources = new();
    private readonly ConcurrentDictionary<long, int> _storeFullCandidates = new();
    private readonly ConcurrentDictionary<long, int> _leaseTableFullCandidates = new();
    private readonly bool _strictProductionHistory;
    private long _sequence;

    internal MonotonicHistoryRecorder(bool strictProductionHistory = false)
    {
        _strictProductionHistory = strictProductionHistory;
    }

    public PendingInvocation Invoke(int id, int actorId, ReferenceCommand command)
    {
        return new PendingInvocation(
            this,
            id,
            actorId,
            command,
            NextSequence(),
            _strictProductionHistory);
    }

    public IReadOnlyList<RecordedOperation> Snapshot()
    {
        return _operations.Values.OrderBy(static operation => operation.Id).ToArray();
    }

    public IReadOnlyList<RecordedSlotResourceWitness> ResourceSnapshot()
    {
        return _slotResources.OrderBy(static witness => witness.Sequence).ToArray();
    }

    internal void ObserveSlotResource(LockFreeSlotResourceEvent resourceEvent)
    {
        RecordedSlotResourceKind kind = resourceEvent.Kind switch
        {
            LockFreeSlotResourceEventKind.Claim => RecordedSlotResourceKind.Claim,
            LockFreeSlotResourceEventKind.Free => RecordedSlotResourceKind.Free,
            LockFreeSlotResourceEventKind.Retire => RecordedSlotResourceKind.Retire,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceEvent))
        };
        _slotResources.Enqueue(new RecordedSlotResourceWitness(
            kind,
            resourceEvent.SlotIndex,
            resourceEvent.Generation,
            NextSequence()));
    }

    public long BeginCandidate(int slotCount)
    {
        if (slotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }

        long candidateSequence = NextSequence();
        if (!_storeFullCandidates.TryAdd(candidateSequence, slotCount))
        {
            throw new InvalidOperationException(
                $"StoreFull proof candidate sequence {candidateSequence} was observed twice.");
        }

        return candidateSequence;
    }

    public void CompleteCandidate(long token, bool confirmed)
    {
        if (token <= 0 || !_storeFullCandidates.TryRemove(token, out int slotCount))
        {
            throw new InvalidOperationException(
                $"StoreFull proof token {token} completed without its candidate.");
        }

        if (!confirmed)
        {
            return;
        }

        // The second collect validates the earlier common instant. The witness
        // therefore carries the candidate's sequence plus its later distinct
        // confirmation sequence.
        _slotResources.Enqueue(new RecordedSlotResourceWitness(
            RecordedSlotResourceKind.StoreFullProof,
            SlotIndex: -1,
            Generation: slotCount,
            Sequence: token,
            ConfirmationSequence: NextSequence()));
    }

    long ILockFreeLeaseTableFullProofObserver.BeginCandidate(int leaseRecordCount)
    {
        if (leaseRecordCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseRecordCount));
        }

        long candidateSequence = NextSequence();
        if (!_leaseTableFullCandidates.TryAdd(candidateSequence, leaseRecordCount))
        {
            throw new InvalidOperationException(
                $"LeaseTableFull proof candidate sequence {candidateSequence} was observed twice.");
        }

        return candidateSequence;
    }

    void ILockFreeLeaseTableFullProofObserver.CompleteCandidate(
        long token,
        bool confirmed)
    {
        if (token <= 0
            || !_leaseTableFullCandidates.TryRemove(token, out int leaseRecordCount))
        {
            throw new InvalidOperationException(
                $"LeaseTableFull proof token {token} completed without its candidate.");
        }

        if (!confirmed)
        {
            return;
        }

        _slotResources.Enqueue(new RecordedSlotResourceWitness(
            RecordedSlotResourceKind.LeaseTableFullProof,
            SlotIndex: -1,
            Generation: leaseRecordCount,
            Sequence: token,
            ConfirmationSequence: NextSequence()));
    }

    internal long NextSequence() => Interlocked.Increment(ref _sequence);

    internal void Add(RecordedOperation operation)
    {
        if (!_operations.TryAdd(operation.Id, operation))
        {
            throw new InvalidOperationException($"Operation ID {operation.Id} was recorded more than once.");
        }
    }
}

internal sealed class PendingInvocation
{
    private readonly MonotonicHistoryRecorder _recorder;
    private readonly int _id;
    private readonly int _actorId;
    private readonly ReferenceCommand _command;
    private readonly long _invocationSequence;
    private readonly bool _strictProductionHistory;
    private long _entrySequence;
    private int _completed;

    internal PendingInvocation(
        MonotonicHistoryRecorder recorder,
        int id,
        int actorId,
        ReferenceCommand command,
        long invocationSequence,
        bool strictProductionHistory)
    {
        _recorder = recorder;
        _id = id;
        _actorId = actorId;
        _command = command;
        _invocationSequence = invocationSequence;
        _strictProductionHistory = strictProductionHistory;
    }

    public void Enter()
    {
        if (Interlocked.CompareExchange(ref _entrySequence, _recorder.NextSequence(), 0) != 0)
        {
            throw new InvalidOperationException("An invocation can enter the implementation only once.");
        }
    }

    public RecordedOperation Complete(
        ReferenceResultCode result,
        string? observedValue = null,
        long observedGeneration = 0)
    {
        if (Volatile.Read(ref _entrySequence) == 0)
        {
            throw new InvalidOperationException("An invocation must enter before it returns.");
        }

        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            throw new InvalidOperationException("An invocation can complete only once.");
        }

        bool isAcquire = _command.Kind is ReferenceOperationKind.Acquire
            or ReferenceOperationKind.AcquireLease;
        if (_strictProductionHistory
            && isAcquire
            && result == ReferenceResultCode.Success
            && (observedValue is null || observedGeneration <= 0))
        {
            throw new InvalidOperationException(
                "A successful production acquire must record its returned bytes and exact nonzero slot generation.");
        }

        if ((!isAcquire || result != ReferenceResultCode.Success)
            && (observedValue is not null || observedGeneration != 0))
        {
            throw new InvalidOperationException(
                "Only a successful acquire may carry a returned value observation.");
        }

        var returnSequence = _recorder.NextSequence();
        var responseSequence = _recorder.NextSequence();
        var operation = new RecordedOperation(
            _id,
            _actorId,
            _command,
            result,
            _invocationSequence,
            Volatile.Read(ref _entrySequence),
            returnSequence,
            responseSequence,
            observedValue,
            observedGeneration,
            RequiresAcquireObservation: _strictProductionHistory && isAcquire,
            UsesInfiniteWait: _strictProductionHistory);
        _recorder.Add(operation);
        return operation;
    }
}

internal sealed class ReferenceStoreModel
{
    private readonly HashSet<int> _participants;
    private readonly Dictionary<string, ValueEntry> _values;
    private readonly Dictionary<int, ReservationEntry> _reservations;
    private readonly Dictionary<int, LeaseEntry> _leases;

    public ReferenceStoreModel(
        int participantCapacity,
        int valueCapacity,
        IEnumerable<int>? initialParticipants = null,
        int? leaseCapacity = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valueCapacity, 1);
        ParticipantCapacity = participantCapacity;
        ValueCapacity = valueCapacity;
        LeaseCapacity = leaseCapacity ?? Math.Max(1, valueCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseCapacity, 1);
        _participants = initialParticipants?.ToHashSet() ?? [];
        if (_participants.Count > participantCapacity || _participants.Any(static participant => participant <= 0))
        {
            throw new ArgumentException("Initial participants must be positive and fit the configured capacity.", nameof(initialParticipants));
        }

        _values = new Dictionary<string, ValueEntry>(StringComparer.Ordinal);
        _reservations = [];
        _leases = [];
    }

    private ReferenceStoreModel(ReferenceStoreModel source)
    {
        ParticipantCapacity = source.ParticipantCapacity;
        ValueCapacity = source.ValueCapacity;
        LeaseCapacity = source.LeaseCapacity;
        _participants = new HashSet<int>(source._participants);
        _values = new Dictionary<string, ValueEntry>(source._values, StringComparer.Ordinal);
        _reservations = new Dictionary<int, ReservationEntry>(source._reservations);
        _leases = new Dictionary<int, LeaseEntry>(source._leases);
    }

    public int ParticipantCapacity { get; }

    public int ValueCapacity { get; }

    public int LeaseCapacity { get; }

    public int ParticipantCount => _participants.Count;

    public int ValueCount => _values.Count + _reservations.Count;

    public bool TryGetValue(string key, out string? value)
    {
        if (_values.TryGetValue(key, out ValueEntry entry)
            && entry.State == ReferenceValueState.Published)
        {
            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    public bool TryApply(
        ReferenceCommand command,
        ReferenceResultCode observedResult,
        out ReferenceStoreModel? next,
        bool physicalStoreFullWitnessed = false,
        bool physicalLeaseTableFullWitnessed = false,
        string? observedValue = null,
        long observedGeneration = 0,
        bool requiresAcquireObservation = false)
    {
        next = new ReferenceStoreModel(this);

        // A claimed-but-not-yet-ordered lifecycle consumes a physical slot but
        // owns no abstract key. The strict checker admits StoreFull at that
        // physical point without adding a value or reservation to this model.
        if (observedResult == ReferenceResultCode.StoreFull && physicalStoreFullWitnessed)
        {
            bool validPhysicalResult = _participants.Contains(command.ParticipantId)
                && command.Kind is ReferenceOperationKind.Publish or ReferenceOperationKind.Reserve
                && (command.Kind != ReferenceOperationKind.Reserve
                    || (command.TokenId > 0 && !_reservations.ContainsKey(command.TokenId)));
            if (validPhysicalResult)
            {
                return true;
            }

            next = null;
            return false;
        }

        // A Claiming lease record consumes physical capacity before the acquire
        // becomes abstractly successful. A strict exact proof may therefore
        // justify LeaseTableFull even when this bounded model has no public
        // lease token for every occupied physical record.
        if (observedResult == ReferenceResultCode.LeaseTableFull
            && physicalLeaseTableFullWitnessed)
        {
            bool validPhysicalResult = _participants.Contains(command.ParticipantId)
                && command.Kind == ReferenceOperationKind.AcquireLease
                && command.TokenId > 0
                && !_leases.ContainsKey(command.TokenId)
                && _values.TryGetValue(command.Key, out ValueEntry value)
                && value.State == ReferenceValueState.Published;
            if (validPhysicalResult)
            {
                return true;
            }

            next = null;
            return false;
        }

        if (observedResult is ReferenceResultCode.OperationCanceled or ReferenceResultCode.StoreBusy)
        {
            return true;
        }

        var expectedResult = next.Apply(command);
        if (expectedResult == observedResult)
        {
            if (requiresAcquireObservation
                || observedValue is not null
                || observedGeneration != 0)
            {
                if (!next.TryBindAcquireObservation(
                        command,
                        observedResult,
                        observedValue,
                        observedGeneration))
                {
                    next = null;
                    return false;
                }
            }

            return true;
        }

        // The production contract permits RemovePending after the logical
        // removal ordering point even when no protecting lease remains, when
        // bounded unlink/reclaim work is still incomplete. Its abstract state
        // transition is the same successful removal modeled above.
        if (command.Kind == ReferenceOperationKind.Remove
            && observedResult == ReferenceResultCode.RemovePending
            && expectedResult == ReferenceResultCode.Success)
        {
            return true;
        }

        next = null;
        return false;
    }

    public string Fingerprint()
    {
        var participants = string.Join(',', _participants.Order());
        var values = string.Join(
            '|',
            _values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair =>
                    pair.Key
                    + "="
                    + pair.Value.Value
                    + ":"
                    + pair.Value.State
                    + ":g"
                    + pair.Value.MappedGeneration));
        var reservations = string.Join(
            '|',
            _reservations.OrderBy(static pair => pair.Key)
                .Select(static pair => pair.Key + "=" + pair.Value.ParticipantId + ":" + pair.Value.Key));
        var leases = string.Join(
            '|',
            _leases.OrderBy(static pair => pair.Key)
                .Select(static pair => pair.Key + "=" + pair.Value.ParticipantId + ":" + pair.Value.Key));
        return participants + ";" + values + ";" + reservations + ";" + leases;
    }

    private ReferenceResultCode Apply(ReferenceCommand command)
    {
        return command.Kind switch
        {
            ReferenceOperationKind.OpenParticipant => OpenParticipant(command.ParticipantId),
            ReferenceOperationKind.CloseParticipant => CloseParticipant(command.ParticipantId),
            ReferenceOperationKind.Publish => Publish(command.ParticipantId, command.Key, command.Value),
            ReferenceOperationKind.Reserve => Reserve(command),
            ReferenceOperationKind.CommitReservation => CommitReservation(command),
            ReferenceOperationKind.AbortReservation => AbortReservation(command),
            ReferenceOperationKind.Acquire => Acquire(command.ParticipantId, command.Key),
            ReferenceOperationKind.AcquireLease => AcquireLease(command),
            ReferenceOperationKind.ReleaseLease => ReleaseLease(command),
            ReferenceOperationKind.Remove => Remove(command.ParticipantId, command.Key),
            ReferenceOperationKind.RecoverReservation => RecoverReservation(
                command.ParticipantId,
                command.TokenId),
            ReferenceOperationKind.RecoverLease => RecoverLease(
                command.ParticipantId,
                command.TokenId),
            ReferenceOperationKind.DisposeParticipant => DisposeParticipant(command.ParticipantId),
            _ => ReferenceResultCode.Unexpected
        };
    }

    private bool TryBindAcquireObservation(
        ReferenceCommand command,
        ReferenceResultCode result,
        string? observedValue,
        long observedGeneration)
    {
        if (command.Kind is not (
                ReferenceOperationKind.Acquire or ReferenceOperationKind.AcquireLease))
        {
            return false;
        }

        if (result != ReferenceResultCode.Success)
        {
            return observedValue is null && observedGeneration == 0;
        }

        if (observedValue is null
            || observedGeneration is < 1 or > LockFreeSlotTable.TerminalGeneration
            || !_values.TryGetValue(command.Key, out ValueEntry value)
            || value.State != ReferenceValueState.Published
            || !string.Equals(value.Value, observedValue, StringComparison.Ordinal))
        {
            return false;
        }

        if (value.MappedGeneration != 0
            && value.MappedGeneration != observedGeneration)
        {
            return false;
        }

        if (value.MappedGeneration == 0)
        {
            _values[command.Key] = value with { MappedGeneration = observedGeneration };
        }

        return true;
    }

    private ReferenceResultCode OpenParticipant(int participantId)
    {
        if (participantId <= 0 || _participants.Contains(participantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        if (_participants.Count == ParticipantCapacity)
        {
            return ReferenceResultCode.ParticipantTableFull;
        }

        _participants.Add(participantId);
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode CloseParticipant(int participantId)
    {
        return _participants.Remove(participantId)
            ? ReferenceResultCode.Success
            : ReferenceResultCode.ParticipantNotActive;
    }

    private ReferenceResultCode Publish(int participantId, string key, string value)
    {
        if (!_participants.Contains(participantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        if (ContainsKey(key))
        {
            return ReferenceResultCode.DuplicateKey;
        }

        if (ValueCount == ValueCapacity)
        {
            return ReferenceResultCode.StoreFull;
        }

        _values.Add(key, new ValueEntry(value, ReferenceValueState.Published, MappedGeneration: 0));
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode Reserve(ReferenceCommand command)
    {
        if (!_participants.Contains(command.ParticipantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        if (command.TokenId <= 0 || _reservations.ContainsKey(command.TokenId))
        {
            return ReferenceResultCode.InvalidReservation;
        }

        if (ContainsKey(command.Key))
        {
            return ReferenceResultCode.DuplicateKey;
        }

        if (ValueCount == ValueCapacity)
        {
            return ReferenceResultCode.StoreFull;
        }

        _reservations.Add(
            command.TokenId,
            new ReservationEntry(command.ParticipantId, command.Key, command.Value));
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode CommitReservation(ReferenceCommand command)
    {
        if (!_participants.Contains(command.ParticipantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        if (!_reservations.Remove(command.TokenId, out ReservationEntry reservation)
            || reservation.ParticipantId != command.ParticipantId)
        {
            return ReferenceResultCode.InvalidReservation;
        }

        _values.Add(
            reservation.Key,
            new ValueEntry(reservation.Value, ReferenceValueState.Published, MappedGeneration: 0));
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode AbortReservation(ReferenceCommand command)
    {
        if (!_participants.Contains(command.ParticipantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        return _reservations.Remove(command.TokenId, out ReservationEntry reservation)
            && reservation.ParticipantId == command.ParticipantId
                ? ReferenceResultCode.Success
                : ReferenceResultCode.InvalidReservation;
    }

    private ReferenceResultCode Remove(int participantId, string key)
    {
        if (!_participants.Contains(participantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        if (!_values.TryGetValue(key, out ValueEntry value))
        {
            return ReferenceResultCode.NotFound;
        }

        bool isProtected = _leases.Values.Any(
            lease => string.Equals(lease.Key, key, StringComparison.Ordinal));
        if (value.State == ReferenceValueState.RemoveRequested)
        {
            // Logical absence rejects new acquires, but the exact generation
            // remains discoverable to a retrying remove until reclamation.
            if (isProtected)
            {
                return ReferenceResultCode.RemovePending;
            }

            _values.Remove(key);
            return ReferenceResultCode.Success;
        }

        if (isProtected)
        {
            _values[key] = value with { State = ReferenceValueState.RemoveRequested };
            return ReferenceResultCode.RemovePending;
        }

        _values.Remove(key);
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode Acquire(int participantId, string key)
    {
        if (!_participants.Contains(participantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        return _values.TryGetValue(key, out ValueEntry value)
            && value.State == ReferenceValueState.Published
            ? ReferenceResultCode.Success
            : ReferenceResultCode.NotFound;
    }

    private ReferenceResultCode AcquireLease(ReferenceCommand command)
    {
        if (!_participants.Contains(command.ParticipantId))
        {
            return ReferenceResultCode.ParticipantNotActive;
        }

        if (!_values.TryGetValue(command.Key, out ValueEntry value)
            || value.State != ReferenceValueState.Published)
        {
            return ReferenceResultCode.NotFound;
        }

        if (command.TokenId <= 0 || _leases.ContainsKey(command.TokenId))
        {
            return ReferenceResultCode.InvalidLease;
        }

        if (_leases.Count == LeaseCapacity)
        {
            return ReferenceResultCode.LeaseTableFull;
        }

        _leases.Add(command.TokenId, new LeaseEntry(command.ParticipantId, command.Key));
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode ReleaseLease(ReferenceCommand command)
    {
        if (!_leases.Remove(command.TokenId, out LeaseEntry lease)
            || lease.ParticipantId != command.ParticipantId)
        {
            return ReferenceResultCode.InvalidLease;
        }

        ReclaimIfUnprotected(lease.Key);
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode RecoverReservation(int recoveringParticipantId, int reservationId)
    {
        if (!_reservations.TryGetValue(reservationId, out ReservationEntry reservation)
            || reservation.ParticipantId == recoveringParticipantId)
        {
            return ReferenceResultCode.InvalidReservation;
        }

        _reservations.Remove(reservationId);
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode RecoverLease(int recoveringParticipantId, int leaseId)
    {
        if (!_leases.TryGetValue(leaseId, out LeaseEntry lease)
            || lease.ParticipantId == recoveringParticipantId)
        {
            return ReferenceResultCode.InvalidLease;
        }

        _leases.Remove(leaseId);
        ReclaimIfUnprotected(lease.Key);
        return ReferenceResultCode.Success;
    }

    private ReferenceResultCode DisposeParticipant(int participantId)
    {
        if (!_participants.Remove(participantId))
        {
            return ReferenceResultCode.StoreDisposed;
        }

        foreach (int reservationId in _reservations
                     .Where(pair => pair.Value.ParticipantId == participantId)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _reservations.Remove(reservationId);
        }

        foreach (int leaseId in _leases
                     .Where(pair => pair.Value.ParticipantId == participantId)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            LeaseEntry lease = _leases[leaseId];
            _leases.Remove(leaseId);
            ReclaimIfUnprotected(lease.Key);
        }

        return ReferenceResultCode.Success;
    }

    private bool ContainsKey(string key) =>
        _values.ContainsKey(key)
        || _reservations.Values.Any(reservation => string.Equals(reservation.Key, key, StringComparison.Ordinal));

    private void ReclaimIfUnprotected(string key)
    {
        if (_values.TryGetValue(key, out ValueEntry value)
            && value.State == ReferenceValueState.RemoveRequested
            && !_leases.Values.Any(lease => string.Equals(lease.Key, key, StringComparison.Ordinal)))
        {
            _values.Remove(key);
        }
    }

    private enum ReferenceValueState
    {
        Published,
        RemoveRequested
    }

    private readonly record struct ValueEntry(
        string Value,
        ReferenceValueState State,
        long MappedGeneration);

    private readonly record struct ReservationEntry(int ParticipantId, string Key, string Value);

    private readonly record struct LeaseEntry(int ParticipantId, string Key);
}
