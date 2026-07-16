# Public API Contract

## Compatibility principle

Layout 2.0 adds an explicitly selected concurrency profile; it does not add a
second key-value abstraction. Existing v1.2 callers retain their exact helper
method signatures, numeric enum assignments, operation names, overload shapes,
and default behavior.

## Profile selection

```csharp
namespace SharedMemoryStore;

public enum StoreProfile
{
    Legacy = 0,
    LockFree = 1
}

public sealed class SharedMemoryStoreOptions
{
    public StoreProfile Profile { get; init; } = StoreProfile.Legacy;
    public int ParticipantRecordCount { get; init; } = 64;

    // All existing properties remain.

    // Existing signature and layout-v1.2 result remain unchanged.
    public static long CalculateRequiredBytes(
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount);

    // New profile-aware helper; profile is first to avoid overload ambiguity.
    public static long CalculateRequiredBytes(
        StoreProfile profile,
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount = 64);

    // Existing Create(...) signature remains byte-for-byte unchanged and Legacy.

    public static SharedMemoryStoreOptions CreateLockFree(
        string name,
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount = 64,
        OpenMode openMode = OpenMode.CreateOrOpen,
        bool enableLeaseRecovery = false);
}
```

Manually constructed options select layout 2.0 only when `Profile` is explicitly
`LockFree`. The existing `Create(...)` helper always sets `Legacy`. Neither open
path auto-detects and changes the requested profile.

`ParticipantRecordCount` is ignored by the legacy layout calculator/engine and
is a required positive v2 capacity no greater than 1,048,575. One open v2 store
handle consumes one participant record; the default supports 64 simultaneous
handles.

`SlotCount` remains a required positive capacity for both profiles. The
lock-free profile additionally limits it to 1,048,575 so the exact slot
generation can be carried by every portable single-word directory helper
reference. `CreateLockFree`, profile-aware sizing, manual v2 option validation,
and v2 header validation all enforce the same range before payload projection.
The legacy helper and engine retain their existing upper-bound behavior.

## Opened protocol identity

```csharp
public readonly record struct StoreProtocolInfo(
    StoreProfile Profile,
    int LayoutMajorVersion,
    int LayoutMinorVersion,
    int ResourceProtocolVersion,
    ulong RequiredFeatures,
    ulong OptionalFeatures);

public sealed class MemoryStore : IDisposable
{
    public StoreProfile Profile { get; }
    public StoreProtocolInfo ProtocolInfo { get; }
}
```

These properties are immutable for a handle and safe without shared locking.
For the current pre-release layout 2.0 shape, `RequiredFeatures` is `7`: bit 0
is the versioned spill summary, bit 1 is intent-aware publication ordering, and
bit 2 is PID-namespace-safe recovery identity. Required-features-zero,
bit-0-only, and mask-3 draft mappings return
`StoreOpenStatus.IncompatibleLayout`; those draft clients likewise reject the
current mask before payload projection.

## Store operations

The following existing workflows remain the public v2 surface:

- `MemoryStore.TryCreateOrOpen`
- `TryPublish` and `TryPublishSegments`
- `TryReserve`, `ValueReservation.GetSpan`, `DangerousGetMemory`, `Advance`,
  `Commit`, `Abort`, and `Dispose`
- `TryAcquire`, zero-copy `ValueLease` length/span properties, `Release`, and
  `Dispose`
- `TryRemove`
- explicit lease and reservation recovery
- diagnostics snapshot
- `StoreWaitOptions` overloads
- store disposal

No queue, dequeue, claim, acknowledgement, work assignment, subscriber, stream,
or broker API is added.

## Status contract

All existing `StoreOpenStatus` and `StoreStatus` numeric assignments remain
unchanged. Layout 2.0 uses them as follows:

| Condition | Result |
|---|---|
| Exact key owned by `Reserved(ExplicitReservation)`, `Published`, or `RemoveRequested` | `DuplicateKey` |
| No published current generation | `NotFound` |
| Exact confirmed all-non-Free double collect of the value-slot controls | `StoreFull` |
| Exact confirmed all-non-Free double collect of structurally valid lease controls | `LeaseTableFull` |
| Removal won but matching active leases remain, or bounded post-removal classification/reclaim work is incomplete | `RemovePending` |
| Caller retry/deadline budget exhausted | `StoreBusy` |
| Cancellation observed before ordering point | `OperationCanceled` |
| Stale/wrong lease record incarnation | `InvalidLease` or `LeaseAlreadyReleased` according to observable history |
| Stale/wrong reservation generation | `InvalidReservation` or `ReservationAlreadyCompleted` according to observable history |
| Requested profile differs from existing mapping | `StoreOpenStatus.IncompatibleLayout` |
| `CreateNew` finds an existing unpublished zero header | `StoreOpenStatus.AlreadyExists`; existing bytes remain unchanged |
| `CreateOrOpen` finds an existing unpublished zero header whose initialization ownership cannot be proven | `StoreOpenStatus.StoreBusy`; existing bytes remain unchanged |
| `OpenExisting` finds an existing unpublished zero header | `StoreOpenStatus.IncompatibleLayout`; existing bytes remain unchanged |
| Cold coordination cannot be entered within the caller's budget | `StoreOpenStatus.StoreBusy` or `StoreOpenStatus.OperationCanceled` according to the observed bound/cancellation |
| No free v2 participant record for a new handle | `StoreOpenStatus.ParticipantTableFull` (appended numeric value 11) |
| Existing v2 store control is `Corrupt` | `StoreOpenStatus.IncompatibleLayout` |

The cold create/open budget begins before lifecycle coordination and covers
physical discovery or creation, mapping, header work, and handle registration.
Only the attempt that physically created the region may initialize it. No
successful handle is returned until that ownership has been transferred and the
ordered cold gates have been released.

There is no ordinary `IndexFull`: layout 2.0 overflow capacity equals
`SlotCount`. Failure to place a binding while a value slot is owned is an
inconsistent/corrupt state after bounded revalidation, not a smaller documented
capacity.

Persistent mapped structural corruption is store-wide. The detecting path
exact-CASes the shared store control from `Ready` to `Corrupt`; every later
operation on any handle returns `CorruptStore` before a new mapped projection or
mutation, and a later open fails with `IncompatibleLayout`. An already borrowed
span cannot be revoked. Invalid caller-owned inputs and documented concurrent
or token-history outcomes remain local results and never poison the store.

`Initializing` and `Reserved(AtomicPublication)` are mapped states that remain
tentative to the public operation, not `DuplicateKey` witnesses. A same-key caller helps/revalidates them and may
return `StoreBusy` only after exhausting its bounded local budget. If the
tentative lifecycle aborts, it retries key ownership; if it reaches its
intent-specific ordering point, the caller observes the corresponding
`DuplicateKey`. `StoreFull` remains a physical capacity result: a tentative
non-Free slot is not reusable and can contribute to full-store pressure. After
an initial absent-key lookup, a raced insertion may return `StoreFull` at its
candidate claim before final same-key arbitration; the API does not give
`DuplicateKey` precedence over genuine physical exhaustion in that race.
An exhausted scan is provisional. `StoreFull` is exposed only after two
same-order, structurally valid, all-non-Free control snapshots match exactly;
the ordering point is the confirmed candidate instant between those collects.
A free/moving slot or local proof-buffer contention follows the wait policy and
may yield `StoreBusy`, but never a false capacity result.

Lease-record scan exhaustion is also provisional. `LeaseTableFull` is exposed
only after two same-order, structurally valid, all-non-Free lease-control
snapshots match exactly; malformed state/incarnation/owner/token shapes return
`CorruptStore`. A free/moving record or local lease-proof-buffer contention
follows the same wait policy and cannot become a false capacity result.

## Wait semantics

`StoreWaitOptions` retains its existing public shape.

- Legacy profile: bounds the existing shared synchronization acquisition.
- Lock-free profile: bounds local retry, revalidation, cooperative help, and
  backoff.
- It does not wait for a broker message, key arrival, removal, free value slot,
  or free lease record.
- `NoWait` performs the minimum safe attempt and returns a normal lifecycle,
  capacity, or `StoreBusy` result.
- A transient capacity proof restarts from fresh same-key arbitration for finite
  and infinite callers. `Infinite` does not return transient `StoreBusy`; it
  continues until a confirmed result, another normal outcome, or cancellation.
- Cancellation cannot undo a successful operation after its documented ordering
  point.
- Before an ordering point, cancellation/expiry relinquishes owner-controlled
  slot/lease claims. It may publish an unowned helpable abort/unlink descriptor
  before returning; this is not a leaked reservation and any caller can finish
  physical cleanup.

Ordering is workflow-specific. `TryReserve` orders at
`Initializing -> Reserved`; `TryPublish` and `TryPublishSegments` order only at
`Reserved -> Published`; a later explicit `ValueReservation.Commit` also orders
value visibility at `Reserved -> Published`.

## Atomic convenience publication

`TryPublish` and `TryPublishSegments` are one-call atomic convenience
publications. Their internal slot carries `AtomicPublication` intent. Both
`Initializing` and `Reserved` remain tentative to the public call, and failures
before the exact `Reserved -> Published` CAS leave no newly published key.
Another caller must not turn either tentative state into `DuplicateKey` merely
because a directory binding is physically present. Once Published wins, the
public call has ordered and cancellation cannot rewrite its result.

## Reservation lifetime

A successful reservation owns one exact key/slot generation and returns
store-owned writable memory. Descriptor bytes are fixed before success. The
caller must account for exactly the announced payload length before commit.

The slot carries `ExplicitReservation` intent. Its exact
`Initializing -> Reserved` CAS is the `TryReserve` ordering point. A physically
discoverable `Initializing` binding is tentative and helpable, not by itself a
`DuplicateKey` witness; exact Reserved is an ordered explicit reservation and
does block the same key.

Supported recovery never cancels a resource owned by a live `Active`
participant. Normal recovery preserves it. Current-process reservation recovery
is a test/controlled-shutdown override that requires process-wide writer and
writable-view quiescence; racing that override with `TryReserve`, convenience
publication, token activity, or attached store-handle disposal is outside the
public result contract. Once a
reservation owner is safely stale or has published a quiescent
Closing/Recovering handoff, recovery may invalidate its token and reclaim its
slot.

The reservation is an exclusive single-producer token. Copying it for ordinary
value passing is allowed, but concurrent `GetSpan`, `DangerousGetMemory`,
`Advance`, `Commit`, `Abort`, or `Dispose` calls through copies are unsupported.
The library prevents out-of-bounds/later-generation mutation; it does not assign
disjoint ranges to concurrent producers or verify that bytes were written before
the caller accounts for them.

- Safe spans are valid only until the next reservation lifecycle action on that
  token and never after commit, abort, recovery, token disposal, or store
  disposal.
- `DangerousGetMemory` is retained-capable but has the same logical lifetime;
  using it later is explicitly unsafe.
- Commit is atomic visibility, not a payload copy.
- Disposing a still-current reservation performs best-effort abort.
- A copied stale token cannot commit, advance, abort, or expose writable memory
  after generation reuse.

## Lease lifetime

A successful acquire returns one shared read lease over immutable descriptor and
payload bytes in mapped memory. Several processes may lease the same generation.

- Projection requires no named/global lock.
- Spans are valid until that exact lease is released/recovered or its local store
  handle is disposed.
- Release is exactly once for a record incarnation. Copied/stale tokens cannot
  release a later record reuse.
- Logical removal prevents new leases but existing valid leases retain their
  bytes until release/recovery.
- Lease tokens are process-local API values, not blittable, serializable, or
  transferable shared-memory records.

## Diagnostics additions

The existing `DiagnosticsSnapshot` remains available and gains additive v2
properties for:

- current profile/protocol;
- initializing/reserved/reclaiming/retired slot counts;
- claiming/recovering lease counts;
- primary directory occupancy and buckets with logically-Present versioned
  spill summaries;
- overflow occupancy/scans;
- CAS retries, helped transitions, contention-budget exhaustion;
- stale token and owner-classification outcomes.

Legacy tombstone/compaction properties remain numerically meaningful for v1.2
and report zero/not-applicable for v2. A snapshot is bounded and moment-in-time,
not transactionally exact.

## Disposal

Disposal is local to one `MemoryStore` handle. It atomically rejects new local
operations, waits for already-entered local calls before unmapping, completes
that participant record's safely owned reservations/leases, proves no shared
control references its participant index, advances/frees the participant record,
and invalidates its tokens/views. It does not set a mapping-wide disposing state
or wait for other handles.

`TryRemove` success means the key is logically absent and its bounded lease scan
completed without a protecting active lease. `RemovePending` means the key is
logically absent but either a protecting lease was observed or the caller bound
ended before classification/reclaim completed. Either path may leave physical
unlink/slot reuse to cooperative helping; callers may retry remove and must use
capacity outcomes rather than infer that a particular slot was synchronously
recycled.

## Allocation contract

After store initialization and path warm-up, successful and expected-failure v2
publish/reserve/commit, acquire/project/release, remove, and retry paths allocate
0 managed heap bytes per operation. Public tokens remain value types. Test hooks,
diagnostic snapshots, recovery reports, and open/dispose are not claimed as hot
zero-allocation paths unless separately documented.

Opening a lock-free handle eagerly allocates private `long[SlotCount]` and
`long[LeaseRecordCount]` capacity-proof buffers: approximately eight bytes per
configured value slot or lease record. The lease buffer is about 1 KiB for 128
records, 64 KiB for 8,192, and 8 MiB for 1,048,576. Each is reused for the
handle's lifetime behind its own nonblocking local guard. They are neither
shared-memory capacity nor operation allocations and never serialize another
process/handle.
