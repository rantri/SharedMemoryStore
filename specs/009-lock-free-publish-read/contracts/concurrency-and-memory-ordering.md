# Concurrency and Memory-Ordering Contract

## Progress definition

Layout 2.0 is system-wide lock-free for steady-state data operations: if live
eligible callers continue taking steps, some operation completes in a finite
number of protocol transitions even when another participant stops at any data
transition. It is not wait-free and does not guarantee fairness for one caller
under adversarial same-key contention.

No v2 success or normal-failure path for publish, reserve, segment write,
reservation projection/advance/commit/abort/dispose, acquire, lease projection/
release/dispose, remove, reclaim, directory maintenance/help, diagnostics, or
explicit record recovery enters a named mutex, semaphore, file lock, global
writer flag, or process-owned exclusive state.

The canonical bucket mutation word is a helpable operation descriptor, not a
lock: all information needed to finish is published before the word, every
contender helps rather than waits, and it is released before payload filling or
lease retention.

## Atomic vocabulary

The language-neutral operations are:

| Operation | Required order |
|---|---|
| `LoadAcquire(word)` | Later metadata/byte reads cannot move before it |
| `StoreRelease(word, value)` | Earlier metadata/byte writes cannot move after it |
| `CompareExchange(word, expected, value)` | Sequentially consistent atomic RMW: full fence and one total order with every layout-v2 RMW |
| `Exchange(word, value)` | Sequentially consistent atomic RMW in the same total order |

C# layout 2.0 implements RMW operations with `Interlocked` and loads/stores with
`Volatile` on aligned mapped `long` references. The acquire/remove proof depends
on full-fence, sequentially consistent ordering between lease-control and slot-
control RMWs; independent per-word acquire/release is incompatible. A stronger
implementation is allowed. A weaker implementation or ordinary mixed-width
access is not.

## Externally observable ordering points

| Operation | Ordering point |
|---|---|
| Explicit `TryReserve` key ownership | Slot control CAS `Initializing(g) -> Reserved(g)` with immutable `ExplicitReservation` intent, after insertion established no ordered exact-key owner |
| Explicit reservation commit visibility | Slot control CAS `Reserved(g) -> Published(g)` |
| Atomic `TryPublish`/`TryPublishSegments` success | Slot control CAS `Reserved(g) -> Published(g)` with immutable `AtomicPublication` intent; binding installation and internal Reserved are tentative |
| Acquire | Lease control CAS to `Active(r)` followed by successful directory/slot revalidation; it orders immediately before the first conflicting removal CAS when both succeed |
| Logical remove | Slot control CAS `Published(g) -> RemoveRequested(g)` |
| Lease release | Exact lease control CAS `Active(r) -> Releasing(r)` |
| Reservation abort | Slot control CAS from an owned invisible state to `Aborting(g)` |
| Safe reclamation | Slot control CAS `RemoveRequested(g) -> Reclaiming(g)` after a stable no-active-lease scan |
| Directory unlink | Exact binding CAS to zero under the canonical helpable descriptor |
| Slot reuse | Release publication of `Free(g+1)` after unlink/reset |
| Lease-record reuse | Release publication of `Free(r+1)` after relinquishment/reset |

Participant registration is a cold-path prerequisite, not a data ordering point:
the opener writes PID/identity-kind/start fields and release-publishes an
`Active` participant record under the lifecycle lock before returning a handle.
Its index is embedded by the first slot/lease claim CAS.

An acquire that activates a record but fails final revalidation never completes
successfully and immediately relinquishes that record. Thus its activation is an
internal attempt, not a public acquire ordering point.

## Helpable directory mutation

### Descriptor preparation

The candidate/current slot contains an atomic directory-operation word encoding:

- intent: insert or unlink;
- phase: prepared, target selected, binding changed, complete;
- target: primary lane or overflow cell index when selected;
- exact 33-bit slot generation in every nonzero phase.

All immutable key/hash/length metadata and fixed descriptor bytes are written
before publishing insertion `Prepared`.
The mutator then CASes the canonical bucket mutation word from zero to its exact
slot binding. A stale mutation word may be cleared only after the referenced slot
generation proves it cannot describe a current operation.

The operation generation must equal the bucket mutation binding generation and
the current slot-control generation before a helper acts. A nonzero recorded
directory location carries the same generation. This three-way generation
agreement is revalidated at each phase boundary; validation from an earlier
generation never authorizes an ordinary store or a CAS against an untagged word.

### Helping rule

Every caller that needs to mutate a canonical bucket first:

1. acquire-loads its mutation word;
2. if nonzero, validates the exact slot generation and operation;
3. performs the next idempotent phase through exact full-word CAS, or clears an
   exact stale descriptor/binding;
4. restarts its own lookup/mutation.

No phase grants an unhelpable owner. Target selection is itself published by CAS
in the slot operation word before a directory-cell CAS. Therefore two helpers
cannot install one candidate binding into different cells. If another key wins
the selected empty cell, a helper CASes the unchanged target phase back to
`Prepared` and selection restarts.

If a helper installs an old-generation binding and then discovers that its
exact operation word can no longer advance, it CAS-removes only the binding it
installed and stops. If the slot has already been reused, every old
operation/location CAS fails because generation is part of the compared word;
the helper cannot clear or complete the new lifecycle. Other callers may clear
any provably stale old-generation cell or metadata word without waiting for the
paused helper.

Owned publication-lifecycle cancellation may race these phases. A helper
acquire-validates the immutable `PublicationIntent` after the current
generation's metadata becomes discoverable and reclassifies the exact slot
after each pause/validation window: `Aborting` or
`Reclaiming` routes to exact cancellation cleanup, a changed operation or later
generation is benign loss of authority, and only an impossible same-generation
non-cancel state is corruption. If a `BindingChanged` helper loses its
`Initializing -> Reserved` CAS, it performs this classification before
returning. Likewise, exact `Empty(binding)` observed by an overflow insertion is
benign when the same operation is canceling; it remains corruption if the exact
insert is still current in a non-cancel state because re-publishing that version
would recreate ABA.

A directory-location publisher treats its authorization as one joint tuple:
the canonical mutation, exact operation, current location, slot control,
immutable directory binding, and selected or competing target cells. Terminal
invalid classification requires two stable acquire collections followed by
exact no-op compare/exchange confirmation of every atomic tuple member and a
fresh immutable-binding read. Any loss or movement is ordinary progress or a
budgeted retry.

Cancellation may replace a validated Insert descriptor with `Unlink/Prepared`
before the old insert helper exact-clears its target. The delayed unlink
publisher treats target zero or a structurally valid different in-range binding
as progress and preserves the replacement; a stable malformed or out-of-range
word is corruption. Because Prepared does not name a target, independent unlink
helpers may recover different cells. The first valid location CAS wins; each
loser exact-clears only its distinct recovered old binding and follows the
winner. After `Unlink/TargetSelected`, a same-generation alternate location from
a delayed Prepared publisher is legal: helpers exact-clean the selected and
alternate old-binding witnesses plus the alternate location, while preserving
any replacement binding. A malformed alternate remains corruption.

If an unlink publisher loses its exact operation source after its location CAS,
it withdraws only its exact old target and location. An exact committed Insert
successor or another valid replacement remains untouched. A strictly older
location is exact-cleanable residue. A future-generation location is treated as
reuse only when another member proves that the old tuple moved; a future
location enclosed by the stable exact old-generation tuple is corruption and is
preserved for diagnosis.

Ordering is intent-specific. For `ExplicitReservation`, the exact
`Initializing -> Reserved` CAS is the public `TryReserve` ordering point and
Reserved owns the key. For `AtomicPublication`, that CAS is only an internal
prepared stage; `TryPublish`/`TryPublishSegments` order at
`Reserved -> Published`. Same-generation `Aborting`/`Reclaiming`, terminal
retirement, or a strictly newer generation before the applicable ordering point
is legal cancellation, not a duplicate-key witness. Lower generation, unknown
intent after discoverability, or any other impossible same-generation lifecycle
fails closed as `CorruptStore`.

### Insert

1. Claim a free slot generation and fully initialize key/length/owner metadata,
   including `ExplicitReservation` or `AtomicPublication` intent.
2. Publish insertion `Prepared` and claim/help the canonical bucket descriptor.
3. Re-scan both primary buckets and logically-Present overflow for an exact
   current key and classify its stable state plus intent.
4. If another exact binding is an ordered duplicate witness
   (`Reserved(ExplicitReservation)`, `Published`, or `RemoveRequested`),
   transition the candidate to aborting, publish terminal no-target `Rejected`,
   clear the bucket descriptor, and return/recover it as duplicate. If it is
   tentative (`Initializing` or `Reserved(AtomicPublication)`), help/revalidate
   and retry; bounded exhaustion is `StoreBusy`, never a false `DuplicateKey`.
5. Otherwise select the first currently empty candidate lane; if none, select an
   empty overflow cell in deterministic circular order.
6. Publish the target in the operation word. For an overflow target, load a
   structurally valid exact summary version, revalidate the exact insert and
   canonical mutation, then CAS that version to `Present(candidate)`. Revalidate
   both again; an old helper never rolls this token back.
7. Only after Present publication, CAS the target cell from zero to the exact
   binding; restart target selection on a conflicting CAS loss.
8. CAS-record the exact generation-tagged directory location, publish
   `Reserved`, mark the generation-tagged operation complete, and CAS-clear the
   exact bucket descriptor.

Because same-key mutations share one canonical descriptor, unrelated cell
deletions cannot make two same-key candidates select different holes. Because
any caller completes the descriptor, a stopped inserter cannot block its bucket;
it may leave only the completed reservation/key/slot for explicit recovery.

### Unlink

1. Only `Aborting` or exclusively owned `Reclaiming` lifecycle state may prepare
   unlink.
2. Claim/help the canonical descriptor.
3. Publish the recorded primary/overflow target in the operation word.
4. CAS the full exact binding to zero. A zero or different-generation cell is
   treated as already unlinked only after directory validation finds no second
   exact binding.
5. CAS-clear the exact generation-tagged directory location and complete the
   exact generation-tagged descriptor.
6. Before releasing the mutation, capture any Present spill summary. If its
   exact generation-tagged candidate, overflow location, cell, key hash, and
   canonical bucket all revalidate while this mutation remains current, retain
   Present without a table scan. Otherwise scan the complete overflow section
   under the operation budget. A stable current entry of the same canonical
   bucket retains Present. After a stable empty scan, exact operation/mutation
   revalidation permits only the full-word captured `Present(X) -> Empty(X)`
   CAS. Then CAS-clear the bucket mutation word.
7. Advance generation and release-publish `Free`, or publish `Retired` at
   terminal generation. Ordinary slot metadata is left semantically dead and is
   overwritten only by the next successful initializing owner.

The same pre-release cleanup runs before a completed/rejected/canceled insert
releases its canonical mutation. A completed overflow insert sees its own cell
and retains Present; an insert that retried into primary or was canceled can
restore logical Empty. Budget expiry, unstable current cells, malformed tokens,
or an exact-CAS loss never manufacture Empty. They retain conservative Present
and the helpable exact mutation for a later caller.

Reset/reuse never performs an unconditional write to a directory operation or
location that a stale helper could race. It exact-clears the old generation;
initialization of a later generation may also exact-clear recognizable residue
whose embedded generation differs from the new slot control.

Nor does a reclaim helper clear ordinary key/hash/length/offset/binding metadata.
Multiple callers may help one `Reclaiming(g)` state; after one caller advances
control to `Free(g+1)`, a delayed helper must have no remaining ordinary write.
Free/Retired metadata is ignored, and the winner of the next
`Free(g+1) -> Initializing(g+1,p)` CAS overwrites every required lifecycle field,
including publication intent, before exposing its directory operation,
mutation, or binding.

Readers do not consult the descriptor for a stable binding. During insertion
they may order before binding publication and return `NotFound`; during final
unlink the slot is already logically absent.

## Reservation and publication visibility

1. Slot `Free(g,0) -> Initializing(g,p)` CAS grants one lifecycle and atomically
   carries the complete active participant token `p`; an exact participant
   control recheck follows the claim.
2. Slot `DirectoryBinding`, key, lengths, fixed descriptor, offsets, and
   `PublicationIntent` are written. Release publication of the exact
   generation-tagged `Insert/Prepared` directory operation is the metadata-ready
   marker for those ordinary writes and precedes canonical mutation and
   directory-cell binding publication. A helper that acquire-loads that marker
   validates intent before intent-specific action. Free/Retired and
   pre-metadata Initializing—operation zero with no exact current mutation/cell
   reference—and its direct unreferenced cleanup ignore stale intent bytes; a
   current reference without its required marker is corruption.
3. Directory insertion publishes `Reserved`. For `ExplicitReservation`, its
   exact `Initializing -> Reserved` CAS establishes key ownership and is the
   public `TryReserve` ordering point. For `AtomicPublication`, Reserved is an
   internal tentative stage. An installed binding that is still Initializing is
   tentative for both intents.
4. Payload writes and `Advance` accounting occur only for the exact reservation
   generation. `BytesAdvanced` changes monotonically with checked atomic RMW.
   The successful `BytesAdvanced` compare/exchange is the `Advance` ordering
   point. The operation carries one caller budget across every failed CAS:
   cancellation or expiry before that CAS leaves `BytesAdvanced` unchanged,
   while cancellation or expiry observed after it does not rewrite success.
   `NoWait` performs one bounded probe quantum; `Infinite` retries CAS loss until
   success or explicit cancellation.
5. Commit validates exact length, intent, and control, then CASes
   `Reserved(g,p) -> Published(g,0)`, clearing ownership. This is the explicit
   `ValueReservation.Commit` visibility point and the complete public ordering
   point for `TryPublish`/`TryPublishSegments` with `AtomicPublication` intent.

The commit release makes every preceding descriptor/payload byte visible to an
acquire that observes `Published`. An incomplete/overrun commit never performs
that CAS. Commit/abort/recovery each compare the exact generation, so only one
can leave the reservation lifecycle.

A reservation is single-producer. Concurrent method calls through copied
`ValueReservation` structs are outside the supported contract; atomic byte
accounting prevents range overflow but does not assign disjoint write regions or
prove which bytes a caller actually filled.

## Same-key duplicate classification

Publish/reserve lookup must stabilize the exact directory cell, slot generation,
state, key bytes, and publication intent before returning `DuplicateKey`:

- `Reserved(ExplicitReservation)`, `Published`, and `RemoveRequested` are
  duplicate witnesses;
- `Initializing` is tentative for either intent;
- `Reserved(AtomicPublication)` is tentative because the one-call publication
  has not reached Published;
- `Aborting`/`Reclaiming` is logical cancellation/cleanup and must be helped or
  retried;
- unknown intent on a discoverable current lifecycle is `CorruptStore`.

A bounded operation may return `StoreBusy` while a tentative same-key lifecycle
does not stabilize. If it later aborts, the contender may acquire the key; if it
reaches its intent-specific ordering point, the contender may return
`DuplicateKey`. `StoreFull` is independent physical pressure and may be returned
when all slots are non-reusable, including tentative slots. After an initial
same-key lookup returns absent, candidate claim precedes final directory
arbitration; a raced caller may therefore return genuine `StoreFull` before a
new same-key witness is classified. This is not a false duplicate and does not
grant a tentative lifecycle abstract key ownership.

Allocation-scan exhaustion is not by itself a linearizable full-store result: a
concurrent reusable slot can rotate behind a sequential scanner. After bounded
help and a second claim scan, the rare capacity path uses one per-open
process-local snapshot buffer and nonblocking guard. It reads every slot control
in forward order, identifies the instant after that first collect and before the
second as a proof candidate, and reads every control again in the same order.
`StoreFull` orders at that candidate only when the second pass confirms exact
equality and both passes classify every word as structurally valid and
non-`Free`. The later confirmation callback is evidence that validates the
earlier candidate; it is not itself the physical full instant.

The slot state machine has no reverse edge and no same-generation control ABA.
In particular, claim cleanup is
`Initializing(g,p) -> Aborting(g,0) -> Reclaiming(g,0) -> Free(g+1)` (or terminal
retirement), never `Initializing(g,p) -> Free(g)`. A free slot, any movement, or
another local proof holding the scratch guard is transient contention. The
engine applies the caller's operation-wide wait policy and repeats from fresh
same-key arbitration; `Infinite` cannot return a transient `StoreBusy`.

## Lookup and acquire

Directory lookup for key `K`:

1. compute full hash and two bucket indices;
2. acquire-load every candidate binding and validate generation/hash/key bytes;
3. decode the canonical `SpillSummary`; reject malformed/reserved encodings, and
   if no exact primary binding scan all overflow cells only when Present is set;
4. for a candidate, acquire-load slot control and require `Published(g)`;
5. re-read the directory cell and slot control; both must still equal the exact
   observed values before lease activation begins.

A successful lookup or maintenance classification is a cached witness from one
exact atomic directory reference word. The raw reference and its decoded slot
binding are separate values: a primary/overflow word equals its binding, while
a spill-summary reference is the complete encoded `Present(binding)` word. If a
later consumer of that witness would classify the slot as corrupt, it must not
combine an old reference observation with a newer slot lifecycle. It
acquire-reads the exact raw source word, obtains a fresh stable classification of
the separately decoded binding, then acquire-reads the same raw word again. A
source that differs from the exact raw reference on either side means
unlink/reclaim/reuse or summary replacement won the window; the operation
charges ordinary contention and restarts from a fresh lookup or maintenance
retry. Corruption is permitted only when the same exact reference word encloses
the repeated invalid slot snapshot. This is joint validation, not a new atomic
multi-word primitive. Directory-location publishers extend that source proof to
the complete canonical/operation/location/control/binding/target tuple and use
the no-op confirmation rule above before terminal invalid classification.

Every slot control accepted by directory logic must also satisfy the complete
wire shape. `Initializing` and `Reserved` carry a configured structurally valid
participant token. `Free`, `Published`, `RemoveRequested`, `Aborting`,
`Reclaiming`, and `Retired` carry no participant; `Retired` additionally carries
the terminal generation. A malformed owner/state/generation combination is
invalid even if its generation matches the directory binding, and only a
structurally valid newer control is stale rather than corrupt.

Exact stored-key equality is chunked and probes the same operation-wide budget;
deadline or cancellation propagates `StoreBusy` or `OperationCanceled` rather
than being converted to a false `NotFound`. The null-key stale-cell classifier
does not scan key bytes.

Before releasing a canonical mutation while its spill summary is Present,
cleanup first validates the summary's exact binding/location/cell/hash tuple.
That validation treats the complete encoded summary as the source word and its
embedded binding separately under the source/slot/source rule above.
An exact current witness retains Present without an overflow-table scan. If the
candidate is stale or absent, a complete bounded scan either proves Empty or
finds another exact current same-canonical witness Y. While the same mutation is
still current, cleanup may full-word-CAS `Present(X)` to `Present(Y)`. A raw
Present(Y) recurrence cannot authorize a delayed clearer: its prerequisite
stable-empty scan could not have succeeded while the same nonwrapping Y binding
was current. A stale setter can at worst leave another conservative Present.
Only an exact `Present(X) -> Empty(X)` CAS after stable-empty scan is permitted;
no Empty identity is reused.

Acquire then:

1. CAS `Free(r,0)` to `Claiming(r,p)`, atomically carrying the complete active
   participant token, then revalidate that exact participant control;
2. write the exact slot binding;
3. release-CAS `Claiming(r) -> Active(r)`;
4. acquire-revalidate the exact directory cell and `Published(g)` slot;
5. on success return the lease; otherwise CAS out of `Active`, recycle the
   record, and return `NotFound`/retry/`StoreBusy` as the observed ordering
   allows, or `CorruptStore` only when an unchanged exact reference word encloses
   a repeated malformed slot classification.

It does not read/project descriptor or payload before final success. A claiming
record is not reclamation authority; a later active record cannot return after
removal because final revalidation fails.

Lease allocation-scan exhaustion is only a capacity candidate because a `Free`
record can rotate behind a sequential scanner. The rare slow path owns one
eager `long[LeaseRecordCount]` process-local snapshot and nonblocking guard per
open handle. It reads every lease control in record order, identifies the
instant after the first collect and before the second as the proof candidate,
then repeats the collect in the same order. `LeaseTableFull` orders at that
candidate only when both passes are structurally valid and all non-`Free`, and
the second controls exactly equal the first. `Claiming`/`Active` controls require
a structurally valid configured participant token; `Free`, `Releasing`,
`Recovering`, and `Retired` require participant zero; incarnation and terminal
retirement shapes must also be valid. Malformed controls fail `CorruptStore`.
Lease controls advance incarnation or retire instead of restoring an old Free
word, so equality cannot hide ABA. A free record, movement, or another local
proof holding the guard is transient contention: `NoWait` returns `StoreBusy`,
while finite/infinite callers retry under the original operation-wide budget.
The buffer and guard are neither mapped nor cross-process synchronization.

## Remove, release, and reclaim

Remove CASes `Published(g) -> RemoveRequested(g)` before scanning leases. New
acquires then fail their published-state validation. It scans lease records by:

```text
control1 = LoadAcquire(record.Control)
if control1 is Active:
    binding = record.SlotBinding
    control2 = LoadAcquire(record.Control)
    count only if control1 == control2 and binding == removed binding
```

If any exact active record exists, remove returns `RemovePending`. If the caller
bound expires after logical removal but before its fixed lease-table scan and
classification finish, it also returns conservative `RemovePending`; a later
remove/release/helper retries classification. Otherwise it returns `Success`
after attempting cooperative reclaim. Both statuses describe logical absence;
neither promises physical unlink/reuse completed synchronously. The acquire/
remove race is safe because activation and logical removal are SC RMWs in one
total order:

- activation before remove is visible to the subsequent scan, unless release
  already ended protection;
- activation after remove cannot pass final published-state revalidation.

Release CASes only its exact record incarnation from `Active(r,p)` to
`Releasing(r,0)`, ending public
projection lifetime. It resets/recycles that record and may help reclaim an
exact `RemoveRequested` slot. Concurrent remove, release, retrying remove,
allocation pressure, and recovery may all attempt reclamation; one
`RemoveRequested -> Reclaiming` CAS wins.

## Projection rules

Reservation and lease property access validates local handle state and exact
token incarnation without a named/global lock. A successful lease may project a
`Published` or `RemoveRequested` generation because removal preserves existing
leases. A reservation may project writable bytes only in its exact `Reserved`
generation and within the unadvanced announced range.

Callers must not use a returned span/memory beyond its documented token lifetime.
The protocol prevents safe API projection after lifetime; it cannot revoke an
unsafe pointer or already copied span held by incorrect application code.

## Retry, deadline, and cancellation

CAS loss proves another participant changed shared state and is therefore
progress. Operations restart from a validation boundary, call `SpinWait`, and
sample deadline/cancellation at bounded intervals.

Before that workflow's public ordering point, a caller relinquishes owner-controlled slot and
lease claims before returning `StoreBusy`/`OperationCanceled`. An unbound slot
can transition directly to unowned `Aborting`; a bound slot publishes a complete
helpable unlink descriptor. The returning process need not win another bucket
descriptor, so adversarial later mutations cannot force it to retain participant
ownership past the caller bound. An `Aborting` lifecycle is helpable maintenance,
not a leaked reservation. Thus an atomic convenience publication may abort from
Reserved, while a successfully returned explicit reservation may not be
reclassified as a failed `TryReserve`. After a public ordering point,
cancellation does not rewrite the result. Logical remove returns conservative `RemovePending` if its
post-ordering scan cannot finish within the bound; the key remains absent. All
bounded public-operation tests allow the selected limit plus 250 ms for this
finite handoff/cleanup.

Normal recovery may run concurrently but never changes an owner-controlled slot
whose exact participant remains live Active. The current-process reservation
override is valid only after process-wide reserve/publish/token/writable-view
quiescence, and participant Closing/Recovering is itself a claim-closed
quiescent handoff. Therefore supported recovery cannot race a live owner across
either intent's public ordering point. An override invoked concurrently with a
live writer is outside the public outcome contract; exact generation fencing
must still protect later lifecycles.

## Participant lifecycle

The cold lifecycle lock protects only selection/initialization of participant
records and prevents an incomplete opener from exposing a token. Normal data
claims use the locally cached 28-bit index+incarnation token. They perform an
acquire recheck of its participant control after a successful first claim CAS,
but never acquire the cold lock or mutate the participant registry.

Disposal sets local entry closed, changes its exact `Active` participant record
to `Closing` before resource cleanup, and boundedly scans/help-cleans exact
captured slot and lease controls carrying its token. A stable exact
`Closing`/`Recovering` is an unconditional recovery handoff even while its PID is
live; only `Registering`/`Active` requires liveness classification.
Record-local explicit recovery classifies PID/start identity before clearing an
exact reference. A safely stale participant may enter `Recovering` only for a
final zero-reference retirement pass. `Closing`/`Recovering` makes a post-claim
participant recheck fail, so no legitimate new owner-controlled claim persists.
Only a final reference-free scan permits CAS to PID-free, universally helpable
`Reclaiming`; helpers exact-CAS that control to the next Free incarnation or
Retired record without ordinary identity-field writes. Free/Retired identity
fields are semantically dead, and the next exclusive Registering owner
overwrites all of them before publishing Active, so a delayed helper cannot
erase a reused incarnation.

## Diagnostics and counters

Correctness never depends on a diagnostic counter. Hot counters are striped by
local handle/participant or updated outside the critical visibility transition
to avoid introducing a global cache-line bottleneck. Scans use the same stable
double-read patterns but do not claim records solely for snapshot consistency.

## Disposal ordering

One process-local atomic lifetime word rejects new operations once disposing and
counts operations already entered. Disposal waits only for that handle's entered
calls, publishes exact `Active -> Closing`, then uses one finite post-ownership
cleanup allowance to release/abort exact ownership attributable to its
participant record and attempt reference-free retirement before unmapping. If
the allowance expires, another recovery caller may finish the still claim-closed
participant without waiting for the disposer. Other
handles and mappings continue. A local thread paused after entry may delay
disposal of that handle, but cannot prevent system-wide store progress.

## Required model checks

Before completing the full API, a finite-state/deterministic scheduler must cover
every pause between the atomic steps for:

- same-key insert descriptors with unrelated lane insertion/removal;
- primary spill and overflow publication;
- insertion help/owner recovery;
- commit/acquire;
- acquire/remove;
- release/reclaim/unlink;
- abort versus commit/recovery;
- stale descriptor/binding/lease after generation reuse;
- a helper paused after each operation/location validation and resumed after
  completion, slot reclaim, and reuse;
- local disposal/operation.
- participant open/exhaustion/closing/recovery and a crash immediately after the
  first slot/lease claim CAS.

Any stable-key false miss, two successful current generations, live-owner
recovery, stale-token mutation, or unhelpable descriptor is a convergence failure,
not an allowed outcome.
