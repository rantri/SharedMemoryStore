# Data Model: Lock-Free Shared-Memory Key-Value Store

## Model Boundary

Layout 2.0 is a fixed-capacity, little-endian shared-memory protocol. It contains
no managed references, native pointers, process-local handles, queues, worker
assignments, or broker state. Every offset is relative to the mapping base and
every independently changing shared control is an aligned 64-bit word.

The public key-value model remains:

```text
opaque key -> at most one current immutable value generation
                           |
                           +-> zero or more shared read leases
```

## Store Header V2

The header is immutable after `Initializing -> Ready` except for the store
control's irreversible `Ready -> Corrupt` transition and explicitly identified
atomic counters/diagnostic hints.

| Field | Width | Meaning |
|---|---:|---|
| Magic | 32 | `SMS2` little-endian identity |
| Layout major/minor | 16 + 16 | `2.0` |
| Header length | 32 | Exact bytes covered by this header version |
| Resource protocol | 32 | `2` |
| Required feature bits | 64 | Features every opener must understand |
| Optional feature bits | 64 | Ignorable/additive features |
| Total bytes | 64 | Exact mapped capacity |
| Store ID | 64 | Random nonzero mapping incarnation |
| Store control | 64 atomic | Store state and initialization incarnation |
| Slot count | 32 | Configured bounded value generations; `1..1,048,575` in layout 2.0 |
| Lease-record count | 32 | Configured simultaneous lease capacity |
| Participant-record count | 32 | Configured open-handle capacity; default 64, maximum 1,048,575 |
| Max key/descriptor/value bytes | 32 each | Public input bounds |
| Participant index/generation bits | 32 each | Layout-derived split totaling 28 token bits |
| Bucket/lane dimensions | 32 each | Primary directory shape |
| Section offsets/lengths/strides | 64 each | Bounds-checked relative locations |
| PID namespace ID | 64 | Creator's Linux `/proc/self/ns/pid` numeric token; zero on Windows |
| PID namespace mode | 64 atomic | Monotonic recovery mode: `Enabled=1`, `Mixed=2` |
| Diagnostic counters | 64 atomic each | Approximate monotonic event totals |

Header validation checks magic/version/features before any payload projection,
then validates every multiplication, alignment, monotonic section boundary,
stride, count, and exact option match with checked arithmetic.

Layout 2.0 currently requires feature mask `7`: bit 0 is the versioned-empty
exact-generation spill summary, bit 1 assigns per-slot `PublicationIntent`, and
bit 2 assigns the exact store/participant Linux PID-namespace identity. A
required-features-zero, bit-0-only, or mask-3 draft mapping is incompatible,
because those shapes cannot express the current publication and recovery
ordering.

The creator writes header byte offset 264 and the initial mode before `Ready`.
A Linux opener whose current namespace is different or unproven release-
publishes `Mixed` at offset 272 before its first `Registering` CAS, then proceeds
with ordinary KV access. The downgrade never returns to Enabled. Windows stores
zero and begins Enabled.

Store states:

```text
Zero -> Initializing -> Ready
          |               |
          +-----> Corrupt <-+
          \-----> Unsupported
```

Only cold create/open participates in the legacy-compatible named lifecycle
lock. A steady-state operation that proves persistent mapped structural
corruption full-word-CASes `Ready` to `Corrupt`; this is an atomic shared-memory
fail-closed signal, not OS synchronization, and it never returns to `Ready`.
Ordinary success, caller-input failure, contention, cancellation, and legal
concurrent lifecycle observations never change the store state.

### Cold-open initialization authority

Physical creation disposition is transient process-local state, not a mapped
header field. Each cold attempt records either `CreatedNew` or
`OpenedExisting`. Only `CreatedNew` authorizes `Zero -> Initializing` and the
initial writes that follow it; an open mode, requested profile or dimensions,
and observed zero bytes are not ownership evidence.

An `OpenedExisting` zero header is never modified. `CreateNew` reports
`AlreadyExists`, `CreateOrOpen` reports `StoreBusy`, and `OpenExisting` reports
`IncompatibleLayout`. The cold transaction retains its ordered platform gates
through initialization or validation and participant registration, then either
transfers mapped-resource ownership exactly once or releases those gates before
failed-open owner cleanup. One caller wait/cancellation budget covers the whole
transaction.

## Participant Registry V2

One 64-byte participant record represents one open `MemoryStore` handle.

| Field | Width | Meaning |
|---|---:|---|
| Control | 64 atomic | 3-bit state + 28-bit incarnation + 32-bit PID; top bit reserved zero |
| Identity kind | 32 | Windows creation time, Linux proc start ticks, or unsupported |
| Process-start value | 64 | Platform value interpreted only with identity kind |
| Open sequence | 64 | Diagnostic identity, not hot-path ordering |
| PID namespace ID | 64 | Exact admitted Linux namespace token; zero on Windows |
| Reserved | remaining bytes | Zero in layout 2.0 |

Participant state encodings are `Free=0`, `Registering=1`, `Active=2`,
`Closing=3`, `Recovering=4`, `Reclaiming=5`, and `Retired=6`; value 7 is invalid.
Open selects and initializes a record while holding the allowed cold lifecycle
lock, then release-publishes `Active` before returning the handle. An incomplete
participant record cannot be referenced by a data control and is safely cleared
only after the cold lock is reacquired following creator termination.

Registration writes the per-record PID namespace before `Active`. A stable
Active identity snapshot jointly includes control, PID/start, open sequence, and
that namespace value; classification compares it with the caller's current
namespace before PID/start observation. While `Registering`, ordinary record
fields may still be a previous incarnation's mixture. Presence-only
classification therefore uses the creator header namespace only while mode is
Enabled and is Unsupported in Mixed, never trusting the partial per-record
field. A recovery reader snapshots participant control and only then acquire-
loads mode: any cross-namespace opener release-publishes Mixed before its claim,
so a reader that sees that claim also preserves it. Closing and Recovering are
already claim-closed and remain helpable in either mode.

The hot-path participant token is 28 bits. Its low
`ceil(log2(ParticipantRecordCount + 1))` bits encode record index plus one and its
remaining bits encode participant incarnation. With the default 64 records this
is 7 index bits plus 21 incarnation bits; at the maximum 1,048,575 records at
least 8 incarnation bits remain. Normal operations cache the complete token. A
participant record cannot return to `Free(next incarnation)` until local
disposal or explicit stale-owner recovery has prevented new claims and a bounded
full reference scan proves no slot, lease, or directory operation contains that
token. It retires before the configured token incarnation wraps.

Participant transitions are:

```text
Free(g,pid=0) -> Registering(g,pid) -> Active(g,pid)
Active -> Closing       (normal disposal after local operation entry closes)
Active -> Recovering    (safely stale owner, for final zero-reference retirement)
Closing/Recovering -> Reclaiming(g,pid=0)  (stable zero-reference scan)
Reclaiming -> Free(g+1,pid=0) or Retired   (universally helpable metadata clear)
```

`Closing` is release-published only after the facade gate has stopped and
drained local calls, and before disposal begins mapped-resource cleanup.
`Closing` and `Recovering` are therefore exact, claim-closed owner handoffs: a
recovery caller does not classify their PID/start identity and may recover an
exact referenced slot/lease even while that process remains live. Retirement
still requires a fresh bounded exact-token absence scan and a full-control CAS.

`Reclaiming` carries no live PID or ownership token. Any cold opener or recovery
caller may exact-CAS its control to the next Free/Retired control. It performs no
ordinary identity-field writes: those fields are semantically dead while Free or
Retired, and the next exclusive Registering owner overwrites every identity field
before publishing Active. This prevents a delayed reclaim helper from erasing a
later participant incarnation. Diagnostics counts Reclaiming separately from
usable Free records.

## Directory Binding

A directory cell is one aligned atomic 64-bit word. The fixed codec is:

```text
0                                                                  = Empty
(slotGeneration[33 bits] << 31) | (slotIndex + 1)[31 bits]         = Bound
```

The slot index portion is never zero. A bound cell is valid only while:

1. its decoded slot is in range;
2. the slot control carries the same nonzero generation;
3. the slot state owns a key lifecycle (`Initializing`, `Reserved`, `Published`,
   `RemoveRequested`, `Aborting`, or `Reclaiming` as applicable);
4. once metadata is discoverable, `PublicationIntent` is a known nonzero value;
   and
5. the slot's exact stored key matches the caller key.

Stale or impossible bindings are never followed into payload storage. A normal
operation may help clear a binding only when the exact slot generation proves it
obsolete; otherwise it reports corruption/retry according to the contract.

## Primary Key Directory

The primary directory is an array of buckets. Each bucket contains:

| Field | Width | Meaning |
|---|---:|---|
| SpillSummary | 64 atomic | Versioned Present/Empty token carrying exact insertion slot and generation |
| Mutation | 64 atomic | Empty or exact binding of a helpable directory operation |
| Lane 0..7 | 64 atomic each | Empty or exact directory binding |

Two independently mixed bucket indices are derived from the full 64-bit key
hash. Candidate lanes are scanned in one deterministic order shared by every
participant. Total primary lanes are approximately four times `SlotCount`, for
no more than approximately 25% primary load when `SlotCount` keys exist.

`SpillSummary` is a versioned negative cache, not a count. Bits 0..19 carry
slot-index-plus-one, bits 20..52 carry the 33-bit slot generation, bit 53 is
Present, and bits 54..63 are zero. Raw zero is only the initial empty state.
Every nonzero candidate index must also be below this mapping's configured
`SlotCount`; a codec-shaped but mapping-out-of-range identity is corruption and
cannot act as an Empty negative cache.
Every overflow attempt CASes an exact prior summary to `Present(candidate)`
before its cell CAS. A stable empty full scan under the exact canonical mutation
CASes `Present(X)` to `Empty(X)`, preserving X's identity rather than restoring
zero. Lookup skips overflow for initial or versioned Empty. False-positive scans
are allowed on interrupted cleanup; a false negative for a current or
publishable overflow binding is forbidden.

Cleanup first revalidates the summary's exact candidate binding, overflow
location, cell, hash, and canonical bucket. That stable witness retains Present
without a table scan. A stale, absent, or unstable candidate falls back to the
complete bounded overflow scan before Empty is permitted. If that scan finds a
different exact current spill witness Y, the still-current canonical mutation
may full-word-CAS `Present(X)` to `Present(Y)`. This prevents a removed newest
spill from making every later primary mutation rescan. Reusing Present(Y) is
safe only while Y's exact nonwrapping binding is current: an older clearer
could not have completed its required stable-empty scan while that same Y was
present. Empty identities are never reintroduced.

`Mutation` is not an owner lock. Before claiming it, a mutator writes a complete
insert/unlink descriptor into the referenced slot. A caller that sees a nonzero
word validates the binding and idempotently completes or clears that operation.
The word covers only key-claim/final-unlink for one canonical home bucket and is
released before payload filling or lease retention.

## Overflow Directory

The overflow directory contains exactly `SlotCount` aligned binding cells.
Cells are scanned in a deterministic hash-derived circular order and return
directly to zero on exact removal. It has no tombstones, links, resize, compact,
or epoch switch.

Capacity invariant:

```text
live directory bindings <= owned non-Free value slots <= SlotCount
```

When a new publisher already owns one slot and needs overflow, at most
`SlotCount - 1` cells can contain other live bindings. Consequently at least one
cell can accept the binding. A full scan that finds no valid placement is an
inconsistent state, not an ordinary lower index capacity.

## Value Slot V2

Each value slot has fixed metadata and disjoint fixed-stride key, descriptor,
and payload storage.

| Field | Width | Mutation rule |
|---|---:|---|
| Control | 64 atomic | 3-bit state + 33-bit generation + 28-bit participant token |
| Directory binding | 64 | Exact binding installed for this lifecycle |
| Directory location | 64 atomic | None or generation-tagged primary/overflow cell used by exact unlink |
| Directory operation | 64 atomic | None or generation-tagged insert/unlink phase prepared before bucket descriptor publication |
| Key hash | 64 | Immutable for owned lifecycle |
| Key/descriptor/value length | 32 each | Immutable after initialization |
| Publication intent | 32 | `None=0`, `ExplicitReservation=1`, or `AtomicPublication=2`; immutable after metadata publication |
| Bytes advanced | 64 atomic | Monotonic within reservation length |
| Commit sequence | 64 | Diagnostic/order identity, not a cross-key transaction |
| Key/descriptor/payload offsets | 64 each | Layout-derived and bounds checked |

Slot control encoding is fixed:

```text
bits  0..2:  state
bits  3..35: nonzero 33-bit slot generation
bits 36..63: complete participant token, or zero in unowned states
```

The first `Free(g, participant=0) -> Initializing(g, participant=p)` CAS already
identifies a published active participant record. The binding codec carries the
same generation in 33 bits plus a positive 31-bit slot index. Retirement occurs
before either representation wraps.

The exclusive claimant overwrites `PublicationIntent` and every other ordinary
lifecycle field before release-publishing the exact current-generation
`Insert/Prepared` directory operation. That operation is the metadata-ready
marker and precedes canonical mutation and directory-cell publication.
Free/Retired and a pre-metadata Initializing lifecycle—operation zero with no
current-generation mutation/cell reference—ignore stale, `None`, or unknown
bytes at offset 52; direct unreferenced cleanup after recovery of that claim does
the same. A current reference without its required operation marker is
corruption. While the marker or a valid current reference exists, only
`ExplicitReservation` and `AtomicPublication` are valid; unknown intent fails
closed as `CorruptStore`. Reclaim does not zero this ordinary field, so a delayed
helper has no write that can erase a later generation.

Public workflow and ordering are intent-specific:

| Public workflow | Intent | Public ordering point | Pre-`Published` duplicate witness |
|---|---|---|---|
| `TryReserve` returning `ValueReservation` | `ExplicitReservation` | `Initializing -> Reserved` | exact `Reserved` |
| `TryPublish` / `TryPublishSegments` | `AtomicPublication` | `Reserved -> Published` | none; `Initializing` and `Reserved` remain tentative |

`Published` and `RemoveRequested` are duplicate witnesses for either intent.
An exact tentative binding remains physical/helpable, but a same-key contender
must help/revalidate it and may return `StoreBusy` only after bounded retry
exhaustion; it cannot return `DuplicateKey` solely from that tentative state.
This key-ownership rule does not redefine capacity: every non-Free slot is
physically unavailable, so `StoreFull` may observe tentative Initializing or
Reserved lifecycles. After an initial absent-key lookup, a raced insertion may
return `StoreFull` at candidate claim before its final same-key arbitration; a
real physical-capacity result does not imply or require a duplicate-key witness.

Scan exhaustion alone is not that witness. Each open handle owns an eager local
`long[SlotCount]` scratch snapshot and a nonblocking local guard used only on the
rare full candidate path. It collects every control in slot order, records the
instant between collects as a candidate, and repeats the collect in the same
order. Only structurally valid, all-non-Free, exactly equal collects confirm the
candidate and return `StoreFull`. A guard conflict, `Free`, or movement leaves
the candidate unconfirmed and is retried according to `StoreWaitOptions`.
Structural validity covers the complete control word: generation is nonzero and
bounded; `Initializing`/`Reserved` carry a structurally valid token for a
configured participant record; `Free`/`Published`/`RemoveRequested`/`Aborting`/
`Reclaiming`/`Retired` carry token zero; and `Retired` uses the terminal
generation. Any other shape is `CorruptStore`, not occupancy evidence, even if
the malformed word is identical in both collects.

This proof relies on a strictly forward slot lifecycle. In particular, a failed
pre-metadata claim never restores `Free(g)` after publishing
`Initializing(g,p)`; it hands off through `Aborting(g,0)` and
`Reclaiming(g,0)` before reaching `Free(g+1)` or terminal `Retired(g)`. Thus an
exact control value cannot disappear and reappear between the two reads. The
scratch array costs approximately `8 * SlotCount` bytes per process/open handle
(about 8 MiB at `2^20 - 1` slots), is not mapped/shared state, and introduces no
per-operation allocation or cross-process owner.

The lock-free `SlotCount` ceiling is `2^20 - 1`, so the primary directory has at
most `2^22` lanes and every primary/overflow target needs at most 22 bits.
`DirectoryLocation` stores target kind (2 bits), target index (22), and exact
slot generation (33); seven high bits are reserved. `DirectoryOperation` stores
intent (2), phase (3), target kind (2), target index (22), and exact slot
generation (33); two high bits are reserved. All-zero means no location or no
operation. Every nonzero word has a nonzero generation matching the lifecycle.

Operations are None, Insert, or Unlink and progress `Prepared ->
TargetSelected -> BindingChanged -> Complete` with a section-bounded target, or
`Prepared -> Rejected` when an insert loses duplicate/cancellation arbitration
before installing a binding. Helpers compare/exchange complete words. They may
act only when operation/location generation matches both the bucket mutation
binding and slot control. Consequently a helper paused across slot reclaim/reuse
cannot match a later lifecycle, even when intent/phase/target values repeat.

Generation dominance is asymmetric. A helper for generation `G` may clear its
exact `G` word or a strictly older residue after revalidating its exact binding
and operation. A generation greater than `G` proves benign reuse only when the
old canonical tuple has moved; a future location enclosed by an otherwise
stable exact `G` tuple is malformed and is preserved while corruption is
reported. Because all-zero remains the unversioned empty value, a delayed
`0 -> tagged(G)` CAS can briefly install only its own older residue;
postvalidation withdraws that exact word and a current later generation may
exact-clear it. No older helper may clear or replace a later generation's
nonzero word.

Cancellation dominance is also asymmetric. Once exact slot control changes
from owned `Initializing`/`Reserved` to unowned `Aborting` or `Reclaiming`, an
insert helper that revalidates that state switches to the cancellation path; it
does not classify the state as corrupt or try to make the slot `Reserved`.
Phase validation is only a snapshot: a helper that loses the race after
validation re-reads the exact operation and slot generation before reporting a
failure. An overflow `Empty(binding)` created by exact cancellation is a legal
terminal version for that insertion and prevents an older setter from
re-publishing `Present(binding)`.

Location publication is validated as one joint tuple: canonical mutation,
operation, location, slot control, immutable directory binding, and selected or
competing target cells. A repeated invalid tuple is terminal only after stable
double collection plus exact no-op atomic confirmation; movement restarts or
ends the stale helper. During canceling `Insert -> Unlink/Prepared` handoff, a
cleared target or structurally valid replacement is progress, while a stable
malformed or out-of-range target is corruption. The first valid Prepared unlink
location wins; a loser exact-clears only its distinct old binding. A later
TargetSelected unlink may exact-clean a same-generation alternate location and
old-binding cells while preserving replacements. Post-CAS source loss withdraws
only the publisher's exact old target/location and cannot erase a committed
Insert successor.

For `ExplicitReservation`, the exact
`Initializing(g,p) -> Reserved(g,p)` CAS is the public reserve ordering point.
Losing that CAS to legal cancellation keeps the lifecycle tentative and has no
abstract key-ownership effect. For `AtomicPublication`, the same CAS is only an
internal prepared stage; its public convenience operation orders at
`Reserved(g,p) -> Published(g,0)`. Lower-generation, unknown discoverable
intent, or impossible same-generation observations remain `CorruptStore`.

Supported recovery does not cancel resources owned by a live `Active`
participant. Normal recovery preserves them; current-process reservation
recovery requires process-wide writer quiescence, and exact
`Closing`/`Recovering` is already a quiescent owner handoff. Thus supported
recovery can reclaim an ordered explicit reservation or a tentative atomic
publication only after the owner is stale or quiescent. Racing the
current-process administrative override with a live reserve/publish call is
outside the public result contract; generation fencing still prevents mutation
of a later lifecycle.

### Slot states

Slot state encodings are `Free=0`, `Initializing=1`, `Reserved=2`,
`Published=3`, `RemoveRequested=4`, `Aborting=5`, `Reclaiming=6`, and
`Retired=7`.

| State | Key discoverable? | Acquirable? | Writable? | Reusable? |
|---|---|---|---|---|
| Free | No | No | No | Yes |
| Initializing | Physically after binding install, for helping only; not a public duplicate witness | No | Owner only | No |
| Reserved | Yes; duplicate only for `ExplicitReservation`, tentative for `AtomicPublication` | No | Owner only | No |
| Published | Yes | Yes | No | No |
| RemoveRequested | Yes, duplicate detection | No new leases | No | No |
| Aborting | Yes until exact binding clear | No | No | No |
| Reclaiming | Yes until exact binding clear | No | No | No |
| Retired | No | No | No | Never |

### Slot transitions and ordering points

```text
Free(g,0) --claim CAS-------------> Initializing(g,p,intent)
Initializing(g,p,ExplicitReservation) --reserve CAS--> Reserved(g,p) [TryReserve point]
Initializing(g,p,AtomicPublication) --prepare CAS-----> Reserved(g,p) [still tentative]
Reserved(g,p) --commit CAS-------> Published(g,0) [commit point; TryPublish/TryPublishSegments point]
Published(g,0) --remove CAS------> RemoveRequested(g,0)  [logical-removal point]
Initializing/Reserved(g,p) --abort CAS--> Aborting(g,0)
RemoveRequested(g,0) --no leases CAS---> Reclaiming(g,0)
Aborting/Reclaiming --unlink/advance---> Free(g+1,0) or Retired(g,0)
```

Simple and segmented publication use `AtomicPublication`, may go from
`Initializing` through the same prepared `Reserved` state, and order only at
the commit CAS within their one public call. Explicit direct ingest uses
`ExplicitReservation`, returns after `Reserved`, and later `Commit` orders
value visibility at the same commit CAS. No payload or descriptor byte changes
after `Published` becomes observable.

Helpable abort/reclaim finalization exact-clears only generation-tagged
operation/location words and directory bindings. It does not zero ordinary slot
metadata: a delayed helper may resume after another helper publishes Free and a
new owner reuses the slot. Free/Retired metadata is ignored, and the next
successful `Free -> Initializing` claimant overwrites every lifecycle metadata
field under exclusive generation ownership before publishing a binding.

`ValueReservation` is an exclusive single-producer lifecycle token. It may be
copied as a C# value for ordinary passing, but concurrent `GetSpan`, `Advance`,
`Commit`, or `Abort` calls on copies are unsupported; the library guarantees
bounds and lifecycle fencing, not coordination of overlapping producer writes.

## Lease Record V2

Each record independently represents one protecting read lease.

| Field | Width | Meaning |
|---|---:|---|
| Control | 64 atomic | 3-bit state + 33-bit record incarnation + 28-bit participant token |
| Slot binding | 64 | Exact slot/generation protected |
| Acquire sequence | 64 | Diagnostic identity |

Lease states:

Lease state encodings are `Free=0`, `Claiming=1`, `Active=2`, `Releasing=3`,
`Recovering=4`, and `Retired=5`; values 6-7 are invalid in layout 2.0.

```text
Free(r,0) -> Claiming(r,p) -> Active(r,p) -> Releasing(r,0) -> Free(r+1,0)
             |              |
             `--------------+-> Recovering(r,0) -> Free(r+1,0)
```

- The first claim CAS already embeds an active participant token. `Claiming` is
  therefore recoverable even when later target fields are incomplete, but it is
  not reclamation authority. An acquire publishes `Active` and then revalidates
  the directory and slot.
- `Active` matching the exact slot binding protects that generation.
- The `Active -> Releasing` CAS is the release ordering point. A public token
  carries record index and incarnation, so a copied stale token cannot act on a
  later lease in the same record.
- A crash in `Releasing` is helpable because protection ended at its CAS.
- Recovery may claim only an `Active` record whose exact participant incarnation
  is safely classified stale.

An exhausted allocation scan is not a simultaneous capacity witness because a
`Free` record can rotate behind it. Each open handle therefore owns an eager
local `long[LeaseRecordCount]` snapshot and nonblocking guard for the rare proof
path. It collects controls twice in record order. Only two structurally valid,
all-non-Free, exactly equal collects confirm `LeaseTableFull` at the candidate
instant between them. `Claiming`/`Active` require a structurally valid configured
participant token; every unowned state requires token zero, and retirement is
valid only at the terminal incarnation. Malformed controls are corruption. Every
reuse advances incarnation (or retires), so an exact control cannot disappear
and recur between collects. Free/change/guard conflict is contention under the
operation-wide wait policy. The snapshot costs `8 * LeaseRecordCount` private
bytes per handle and introduces neither per-operation allocation nor shared/OS
synchronization.

## Participant Incarnation

Participant identity is the stable lookup:

```text
(mapping Store ID, participant record index, participant record incarnation)
    -> (PID, identity kind, process-start value)
```

The record incarnation distinguishes later handles reusing one record. The
hot slot/lease control carries the complete compact index+incarnation token, and
the no-reuse-until-no-references rule supplies a second fence. Process start distinguishes PID reuse. Slot generation and lease-
record incarnation complete individual token identity.

## Public Token Handles

The facade stores engine-neutral private handles.

```text
ReservationHandle:
  Store ID, participant index/incarnation, slot index/generation, payload length

LeaseHandle:
  Store ID, participant index/incarnation, slot index/generation,
  lease-record index/incarnation
```

Every token action first checks the local handle lifetime, then the relevant
shared control incarnation. Stale tokens can return only documented invalid or
already-completed outcomes and never mutate a current lifecycle.

## Recovery Decision

A recovery attempt is a caller-controlled classification plus exact CAS:

| Classification | Mutation |
|---|---|
| Live owner | None; count/report active |
| Safely stale owner | Claim exact state/incarnation, finish abort/release/reclaim |
| Unsupported/unknown | None; count/report unsupported |
| Inconsistent identity/state | None unless exact help rule proves obsolete; report failed/corrupt |

Recovery scans are moment-in-time and may race live actions. Classification does
not grant global authority. Every mutation rechecks the exact control word it
classified.

## Diagnostics Snapshot

Diagnostics aggregate bounded scans and local atomic counters:

- free, initializing/reserved, published, remove-requested, reclaiming, retired
  slots;
- active/claiming/recovering/free lease records;
- active/closing/recovering/free/retired participant records and open failures;
- primary occupancy, buckets with spill, overflow occupancy, maximum observed
  candidate scan, CAS retries, and contention-budget exhaustion;
- publish/acquire/remove/release/recovery status counts;
- invalid/stale token attempts and recovery classifications.

Counts may reflect different instants. Diagnostics never change ownership merely
to obtain an exact snapshot and never make a data operation wait for the scan.

## Entity Relationships and Invariants

```text
Store(1)
  |-- ParticipantRecord(ParticipantRecordCount) --> owner liveness identity
  |-- PrimaryDirectory(1) --binding--> ValueSlot(0..SlotCount)
  |-- OverflowDirectory(1) --binding-> ValueSlot(0..SlotCount)
  |-- ValueSlot(SlotCount) --owns-----> Key + Descriptor + Payload
  `-- LeaseRecord(LeaseRecordCount) --> exact ValueSlot generation
```

Global invariants:

1. One exact key has at most one live directory binding.
2. One directory binding names exactly one slot generation; stale bindings never
   project bytes.
3. Every nonzero directory operation/location names the same generation as its
   owning slot lifecycle; a stale helper can clear or roll back only the exact
   old tagged word/binding and cannot mutate a later lifecycle.
4. A value becomes acquirable only at its commit CAS after all immutable bytes.
5. A successful lease has one active record before returning and revalidated the
   exact published binding after activation.
6. A slot is reusable only after logical removal/abort, zero matching active
   leases, exact binding clear, exact old-generation helper-word cleanup, and
   generation advance; ordinary stale metadata is ignored until the next
   exclusive initializing owner overwrites it.
7. No single slot, lease record, directory lane, recovery caller, or process owns
   authority required for operations on all other suitable keys.
8. Every owner-controlled slot/lease state contains a nonzero participant token
   matching an `Active` participant control; that record remains unreused until
   every exact token reference is gone.
9. A cached directory reference and a slot snapshot form one valid observation
   only when the exact raw source word is unchanged on both sides of stable
   classification of its separately decoded slot binding. Primary/overflow
   words equal the binding; a spill-summary source is the complete encoded
   `Present(binding)` word. Source movement orders a fresh lookup or maintenance
   retry; an unchanged exact reference word enclosing a malformed control orders
   fail-closed corruption.
10. Directory-reachable slot controls always obey their full wire shape:
    owner-controlled states carry a structurally valid configured participant
    token, unowned states carry zero, generations are nonzero and bounded, and
    Retired is terminal.
