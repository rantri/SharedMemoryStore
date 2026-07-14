# Recovery Contract

## Scope

Recovery restores bounded resources abandoned by a terminated participant. It is
explicit, caller controlled, and record local. It is not required for healthy
steady-state progress and never acquires authority over the whole mapping.

Two mechanisms are distinct:

- **Helping** completes an already published idempotent transitional operation
  (`DirectoryOperation`, `Aborting`, `Reclaiming`, `Releasing`) without deciding
  that an owner is dead.
- **Recovery** changes an owner-controlled `Initializing`, `Reserved`, `Claiming`,
  or `Active` lifecycle only after classifying its exact participant incarnation,
  unless that exact participant has already published `Closing`/`Recovering`.

Any data caller may help. Explicit recovery performs owner classification only
for owner-controlled `Registering`/`Active` participants. Exact stable
`Closing`/`Recovering` is instead the participant owner's durable quiescent
handoff and requires no OS liveness decision.

## Participant identity

Every owner-controlled slot/lease state atomically stores:

```text
participant token = (participant record incarnation, participant record index + 1)
```

That active participant record stores PID, identity kind, process-start value,
exact PID-namespace identity, and a nonwrapping participant-record incarnation.
The mapping Store ID, slot
generation, and lease-record incarnation further bind the token. PID alone is
never sufficient.

The platform adapter classifies:

| Result | Meaning |
|---|---|
| Live | PID exists and start identity matches |
| Stale | PID is absent or exists with a different start identity, with sufficient platform evidence |
| CurrentHandleAllowed | Owner is the calling process/handle and the explicit test/controlled-shutdown option permits recovery |
| Unsupported | Platform, namespace, permission, or identity source cannot make a safe determination |
| Inconsistent | Stored identity/state is structurally invalid |

Unsupported, live, and inconsistent classifications preserve owner-controlled
state. A store/container deployment must share a process identity/liveness view
sufficient for recovery; otherwise normal access remains supported but recovery
reports unsupported.

Header offsets 264/272 store the creator namespace and atomic recovery mode.
Linux derives the positive ID from `/proc/self/ns/pid`; Windows stores zero. A
different or unproven opener release-publishes monotonic Mixed before its first
Registering CAS and then retains ordinary KV access. Recovery snapshots the
participant control before acquire-loading mode. In Mixed, Registering is always
Unsupported because its per-record ordinary fields can be mixed; Active may be
classified only when its stable per-record namespace exactly matches the
caller's current namespace. The namespace gate precedes PID/start lookup.
Closing/Recovering are already claim-closed and remain helpable in either mode.

## Participant recovery

Participant records are initialized only under the cold lifecycle lock and
become referenceable only after `Active` publication. Lease and reservation
recovery continue to recover only their documented resource type: they double-
read the exact participant control/identity, classify namespace/PID/start, and CAS the
individual lease/slot control. A stale participant record remains `Active` and
therefore unreusable while any resource type still references it.

After record-local recovery, an internal retirement pass definitely classifies
only `Registering`/`Active` participant candidates. It exact-CASes each stale
`Active(incarnation, pid)` candidate to claim-closed
`Recovering(incarnation, pid)` before taking an absence proof; `Closing` and
already-`Recovering` candidates are admitted from their exact captured controls
without process classification because they are already claim-closed. Slot and lease claims
require an exact `Active` post-claim recheck, so any claim ordered before that
fence is visible to the following fresh scan and no valid claim can become usable
after it. The pass then scans every value slot and lease record once, and only an
exact candidate token/control absent from that complete scan may CAS to unowned
`Reclaiming` with PID zero and exact-CAS advance to `Free(next incarnation)` or
`Retired`. Stale `Registering` may retire directly because it has never published
a referenceable Active token. Free/Retired identity fields are semantically dead;
the next exclusive Registering owner overwrites them before Active publication,
preventing a delayed helper from erasing a reused incarnation. Current-process
test/shutdown override does not retire a live `Active` participant record. If retirement
is interrupted, another recovery caller helps `Recovering` or `Reclaiming`; a new
opener cannot reuse it prematurely.

## Lease recovery

`TryRecoverLeases` preserves its current options/report surface and scans a
bounded lease table without a global data lock.

### Current-process override precondition

`RecoverCurrentProcessLeases: false` is the normal recovery mode and remains
safe concurrently with lease acquisition, projection, use, and release. A live
current-process participant is preserved in that mode.

`RecoverCurrentProcessLeases: true` is an administrative override for tests and
controlled shutdown. Before selecting it, the caller MUST establish
process-wide quiescence for every store handle attached to this mapping: no
current-process thread may be acquiring a lease, projecting `ValueSpan` or
`DescriptorSpan`, consuming a previously borrowed span, or releasing a lease.
That quiescence MUST remain in force until `TryRecoverLeases` returns. Outstanding
abandoned Active lease records may remain; they are the override's intended
targets.

The library does not add a process-local acquisition/recovery gate to enforce
this administrative precondition, because every hot-path lease operation would
otherwise pay for an exceptional shutdown/test policy. Concurrent invocation of
the override with current-process lease activity is outside the supported
contract. Exact record-incarnation fencing still prevents an old token from
mutating a later incarnation, but callers MUST NOT infer that an acquire racing
the override will return a still-valid lease. The precondition is process-wide,
not handle-local, because one recovery scan can target Active records created by
any handle in the calling process.

For each record:

1. acquire-load control; skip `Free`/retired records;
2. decode the participant token already present in `Claiming`/`Active`, then
   double-read the corresponding participant record around its identity fields;
3. help `Releasing`/already-owned `Recovering` phases without liveness judgment;
4. classify the stable referenced participant, except that exact stable
   participant `Closing`/`Recovering` is an unconditional handoff for both
   `Claiming` and `Active` regardless of whether that process is still live;
5. for a safely stale `Active(r)`, or a current-process `Active(r)` explicitly
   admitted by the test/shutdown override, CAS exactly to `Recovering(r)`;
6. for a safely stale `Claiming(r)`, CAS exactly to its recovery/reset phase. A
   live current-process `Claiming(r)` remains active/not-eligible even when the
   override is set, unless exact participant `Closing` or published
   `Recovering`/`Reclaiming` state proves the claimant is quiescent;
7. make ordinary fields semantically dead by advancing record incarnation and
   publishing `Free(r+1)` or retired;
8. if the exact protected slot is `RemoveRequested`, attempt cooperative reclaim.

The conservative `Claiming` exception is required because its owner may still
have ordinary `SlotBinding`/`AcquireSequence` initialization writes in flight.
Recycling that record solely on a same-process override could let a delayed write
corrupt a later incarnation. Stale-process classification is sufficient because
that writer is gone; participant closing/recovery is sufficient because it
publishes the required quiescence or helpable handoff.

Recovery helpers do not zero `SlotBinding` or `AcquireSequence`: a helper paused
before such an ordinary write could resume after another helper has published
`Free` and a new claimant has reused the record. Free/retired readers ignore
those fields, and the next exclusive claimant overwrites both before `Active`
publication.

An `Active` CAS to `Recovering` is the recovered lease's release point. A live
release winning first makes the recovery CAS fail; recovery reclassifies the new
state and never decrements or clears it again. A recovered/copied lease token
fails its record-incarnation check and cannot release a later lease.

Report meanings remain:

- `ScannedRecordCount`: records examined;
- `RecoveredLeaseCount`: exact stale lease/claim records relinquished;
- `ActiveLeaseCount`: live/not-eligible records preserved;
- `UnsupportedLeaseCount`: owner safety could not be established;
- `FailedRecoveryCount`: inconsistent or repeatedly changing records not safely
  mutated within the caller bound.

## Reservation recovery

`TryRecoverReservations` preserves its current options/report surface and scans
value slots.

### Current-process reservation override precondition

`RecoverCurrentProcessReservations: false` is normal recovery and is safe
concurrently with healthy publication activity. It preserves every
owner-controlled `Initializing` or `Reserved` lifecycle whose exact participant
is still live Active, regardless of `PublicationIntent`.

`RecoverCurrentProcessReservations: true` is an administrative override for
tests and controlled shutdown. Before selecting it, the caller MUST establish
process-wide quiescence across every handle attached to the mapping: no thread
may be executing `TryReserve`, `TryPublish`, `TryPublishSegments`, reservation
projection, `Advance`, `Commit`, `Abort`, or reservation disposal, or using a
previously borrowed writable span/memory, or disposing a `MemoryStore` handle
attached to the mapping. Quiescence MUST remain until recovery returns.
Concurrent use of this override with current-process writer activity is outside
the supported result contract. The library does not add a hot-path process-wide
gate to enforce this exceptional policy.

Even with the override, a live pre-metadata `Initializing` claimant is not
recycled merely from stale/`None` intent bytes: its ordinary intent/key/length
writes may still be in flight. It becomes eligible only through stale-process
classification or exact participant `Closing`/`Recovering` handoff. A stable
`Reserved` lifecycle may be recovered by the override after the required
quiescence.

For each slot:

1. acquire-load exact control, decode its participant token, and stabilize the
   referenced participant identity;
2. ignore `Published`, `RemoveRequested`, `Free`, and `Retired` as reservation
   recovery targets;
3. help existing `Aborting`/`Reclaiming` and directory descriptors;
4. classify stable owner-controlled `Initializing(g)` or `Reserved(g)`.
   `Pre-metadata Initializing` is exact current Initializing with a zero
   directory-operation word and no exact current canonical mutation/directory
   cell; a current reference without its required `Insert/Prepared` marker is
   corruption. Once that metadata-ready marker or a valid later reference is
   acquire-observed, validate
   `PublicationIntent=ExplicitReservation|AtomicPublication`; unknown intent is
   corruption, while Free/Retired and pre-metadata Initializing ignore stale
   ordinary intent bytes. Direct unreferenced cleanup after recovery of such a
   claim also ignores those bytes;
5. for safely stale ownership, CAS exactly to `Aborting(g)`;
6. use the same generation-tagged abort/unlink helper as ordinary abort and
   reclamation; help/claim the canonical descriptor and clear only the exact
   generation-matching binding/location/operation if installed;
7. advance generation and publish `Free(g+1)` or `Retired`; leave ordinary
   metadata semantically dead for the next exclusive Initializing claimant to
   overwrite.

Recovery never implements a second unlink protocol and never unconditionally
zeros directory or ordinary slot metadata. A publisher or recovery helper that
resumes after its exact lifecycle was recovered fails generation revalidation
and has no ordinary cleanup write capable of altering a later slot generation.

Recovery never copies or publishes incomplete bytes. An
`ExplicitReservation` is publicly ordered at `Initializing -> Reserved`; an
`AtomicPublication` remains tentative in Reserved and is publicly ordered only
at `Reserved -> Published`. Either owner-controlled lifecycle may be reclaimed
only after supported stale/quiescent classification. If commit wins
`Reserved -> Published` before the recovery CAS, recovery preserves that
committed value for either intent and reports the race as non-recovered/active
according to the stable state. A former reservation token fails
generation/owner checks after recovery.

Report meanings remain:

- `ScannedSlotCount`: slots examined;
- `RecoveredReservationCount`: exact stale invisible lifecycles reclaimed;
- `ActiveReservationCount`: live/not-eligible reservations preserved;
- `UnsupportedReservationCount`: safe owner classification unavailable;
- `FailedRecoveryCount`: inconsistent/repeatedly changing state not safely
  changed within the bound.

## Removal/reclamation after a dead lease

Recovery does not clear a value slot merely because it recovered one lease. It
attempts `RemoveRequested -> Reclaiming` only after a fresh stable scan finds no
other exact active lease record. Any concurrent acquire active before logical
removal is included; any activation after logical removal cannot return success.

## Handle disposal

Normal `MemoryStore.Dispose` knows its own participant record and does not need OS
liveness classification. After closing local operation entry, it CASes that
record `Active -> Closing` before any resource cleanup. It then spends one fresh,
finite 250 ms post-ownership allowance scanning and best-effort aborting/releasing
only exact captured controls carrying its participant token, helps transitions,
and attempts a bounded exact zero-reference retirement before unmapping. If the
allowance expires, the participant remains claim-closed `Closing`; any unrelated
explicit recovery caller may recover its resources and retire it without waiting
for or classifying the still-live disposer. If another thread still owns
a reservation/lease token through that facade, subsequent safe token actions
return disposed/invalid. Other handles in the same process remain live because
their participant indices differ.

## Bounds and outcomes

Recovery observes `StoreWaitOptions` as a bound on scan retries, helping, OS
classification, and cancellation. It never returns while holding a mutation
descriptor it claimed; it completes or leaves a fully helpable published
descriptor. Expected results use existing statuses/reports:

- success with zero or more recoveries;
- operation canceled;
- local contention budget exhausted (`StoreBusy`) with report counts collected
  so far and no false recovered count;
- unsupported classification represented in report counts;
- corruption/failed counts for unsafe structural state.

Participant-table exhaustion is an open outcome, not a recovery mutation:
`StoreOpenStatus.ParticipantTableFull` rejects only the new handle. Callers may
explicitly recover stale participants from an already-open handle and retry
open; the library does not run a hidden recovery pass. If every participant
record is occupied and no handle remains open, a new open deterministically
returns `ParticipantTableFull`; it does not classify or reclaim owners while
opening. Deployments that must preserve an existing mapping across total process
loss therefore provision participant headroom and keep a recovery-capable handle
available, or explicitly recreate the fixed-capacity store.

## Safety properties

1. Recovery mutation compares Store ID, complete participant token and
   PID/start identity, slot/record index, and exact incarnation/state as
   applicable.
2. No recovery action can change `Published` back to an invisible state.
3. No live or unsupported `Registering`/`Active` owner is reclaimed without the
   explicit current-process test/shutdown override already present in public
   options and its process-wide quiescence precondition. Exact stable
   `Closing`/`Recovering` is owner-published authority and does not require that
   override or an OS liveness result.
4. A recovery winner invalidates every old token before storage/record reuse.
5. Recovery of one owner cannot stop ordinary operations on unrelated keys.
6. Capacity is considered restored only when a fill-to-capacity test can reuse
   every safely recoverable slot/record, not merely when a diagnostic counter
   increments.
7. A process stopping immediately after `Free -> Initializing` or
   `Free -> Claiming` remains classifiable because that CAS already contains its
   active participant index.
8. Recovery never interprets tentative `Reserved(AtomicPublication)` as a
   completed public publish and never interprets unknown intent as either public
   workflow.
