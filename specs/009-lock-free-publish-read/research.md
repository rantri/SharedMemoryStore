# Phase 0 Research: Lock-Free Shared-Memory Key-Value Store

## Decision 1: Preserve one public store facade with an explicit profile

**Decision**: Keep `MemoryStore`, `ValueReservation`, and `ValueLease` as the
recognizable public workflow. Add `StoreProfile` with numeric zero equal to the
legacy v1.2 profile and an init-only `SharedMemoryStoreOptions.Profile` defaulting
to that value. Preserve the exact existing `Create(...)` and
`CalculateRequiredBytes(...)` signatures and their v1.2 meaning. Add
`CreateLockFree(...)` and a profile-aware sizing overload. V2 adds configurable
`ParticipantRecordCount` with a default of 64 and appends
`StoreOpenStatus.ParticipantTableFull`. Internally dispatch to one concrete
legacy or v2 engine.

**Rationale**: Existing source and compiled consumers continue requesting the
same layout and using the same tokens. An explicit profile prevents silent
auto-upgrade and makes deployment intent reviewable. The facade avoids a second
parallel public store API and keeps zero-copy lifetimes anchored to one mapped
handle.

**Alternatives rejected**:

- A separate `LockFreeMemoryStore` duplicates every operation and token and
  invites semantic drift.
- Changing existing helper signatures by appending an optional profile breaks
  already compiled call sites because the CLR method signature changes.
- Automatically opening whichever layout exists makes creation, rollback, and
  accidental mixed deployments ambiguous.

## Decision 2: Use a true layout-v2 and fail closed on the same name

**Decision**: Define mapped layout `2.0`, magic `SMS2`, and resource protocol 2.
Retain the same physical mapping/region and cold lifecycle resource names for a
given public store name. Use the existing named lock only while creating a zero
mapping and validating its header. Never enter it from a v2 steady-state data
operation. A current requested profile/header mismatch returns
`StoreOpenStatus.IncompatibleLayout` before payload projection or mutation.
Already released clients that cannot map enough bytes to inspect an unknown
header still fail closed through their existing mapping/open failure status.

**Rationale**: Layout 1.2 assumes serialization while v2 relies on atomic state
machines. Treating the redesign as 1.3 would understate incompatibility. Keeping
one physical discovery name ensures an old process cannot silently create a
parallel empty legacy store while v2 data exists. Cold serialization also closes
the simultaneous v1/v2 `CreateOrOpen` zero-header race.

**Alternatives rejected**:

- Reinterpreting v1.2 in place permits incompatible synchronization protocols to
  mutate the same bytes.
- Profile-suffixed physical names hide an incompatible deployment as two
  independent stores.
- A new initialization-only lock unknown to old clients does not serialize an
  old creator racing a new creator.

## Decision 3: Build the protocol from aligned 64-bit atomics

**Decision**: Every independently changing shared control value is a naturally
8-byte-aligned signed `long` accessed atomically through `Interlocked` and
`Volatile`. No shared control word is ever accessed atomically at mixed widths.
Metadata is written only while its containing control word grants an exclusive
initializing state, then published with a release/full-fence transition. Readers
perform acquire reads and validate the binding/control pair before using
metadata. The initial v2 protocol accepts x64 Windows/Linux processes only and
does not require 128-bit CAS or `MemoryBarrierProcessWide`. Other 64-bit
architectures return `UnsupportedPlatform` until separately qualified. Every v2
RMW is sequentially consistent/full-fence in one cross-word order; the acquire/
remove handshake does not rely on independent release/acquire ordering alone.

**Rationale**: .NET exposes efficient 64-bit compare/exchange and acquire/release
reads/writes on the supported baseline. Mapped views share coherent physical
memory across same-host processes. Keeping every observable transition in one
word makes crash points explicit and prevents torn multi-field ownership.
Executable litmus tests remain mandatory because public managed documentation
does not provide a standalone cross-process formal memory-model guarantee.

**Sources**:

- [.NET memory-mapped files](https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files)
- [`Interlocked.CompareExchange`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked.compareexchange)
- [`Volatile.Read`/`Write`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.volatile)
- [Win32 `InterlockedCompareExchange64`](https://learn.microsoft.com/en-us/windows/win32/api/winnt/nf-winnt-interlockedcompareexchange64)
- [Win32 file mapping](https://learn.microsoft.com/en-us/windows/win32/memory/file-mapping)

**Alternatives rejected**:

- A 128-bit binding/state CAS is not portably exposed by the .NET 10 baseline.
- Process-wide memory barriers on every operation are unnecessarily expensive
  when all participants obey the documented atomic protocol.
- Managed references, pointers, object identities, or platform-sized integers
  cannot form an interoperable mapped contract.

## Decision 4: Use fixed CAS buckets plus a capacity-preserving spill directory

**Decision**: Store keys in generation-owned slot key storage. The primary key
directory has two deterministic buckets per hash and eight aligned 64-bit
binding lanes per bucket. The total primary cell count is approximately four
times `SlotCount`, keeping maximum primary load near 25 percent. Each canonical
home bucket also has a versioned `SpillSummary` word and one helpable mutation
descriptor word. A separate overflow directory contains exactly `SlotCount`
binding cells. The versioned-empty summary codec is a required layout-2.0
feature, so earlier pre-release v2 clients that interpreted offset zero as a
monotonic Boolean reject these mappings in both directions.

A binding uses 31 bits for `slotIndex + 1` and 33 bits for a nonzero slot
generation; zero means empty. A slot retires before generation wrap.

Layout 2.0 caps `SlotCount` at `2^20 - 1` (1,048,575). Therefore the canonical
primary directory has at most `2^22` lanes and every primary or overflow target
index fits in 22 bits. `DirectoryOperation` packs intent (2 bits), phase (3),
target kind (2), target index (22), and the exact slot generation (33), leaving
two reserved high bits. `DirectoryLocation` packs target kind (2), target index
(22), and the same generation (33), leaving seven reserved high bits. Every
nonzero operation/location belongs to exactly one slot generation.

Before installing or finally unlinking a key, a mutator publishes a descriptor
in its already initialized slot and CASes the canonical bucket's mutation word
from zero to that exact binding. Any contender that finds the word occupied
finishes the described idempotent operation and restarts; it never waits for the
owner. This localized helping closes the race in which same-key inserters could
observe different holes while unrelated deletions occur. The descriptor is
released before payload filling, commit, acquisition, or lease retention, so a
paused publisher retains only its key/slot rather than a bucket progress gate.

A helper accepts an operation or recorded location only when its embedded
generation equals both the bucket mutation binding and the slot control
generation. Every phase transition and location publication/clear is an exact
full-word CAS. If a helper resumes after the slot was reclaimed and reused, its
CAS cannot match the newer operation/location. A stale target-cell CAS is safe
because the cell binding also carries the old generation; failure to advance
the exact old operation triggers exact rollback, and any observer may clear the
provably stale binding. No ordinary store from an old helper may overwrite a
new lifecycle.

While owning the descriptor, publication scans candidate lanes in deterministic
order. If all candidates are occupied, it full-word-CASes the bucket summary
from its exact prior version to `Present(candidate)` before publishing into the
overflow directory. The summary packs 20 bits of slot-index-plus-one, the full
33-bit nonwrapping slot generation, one Present bit, and ten zero reserved bits.
Spill insertion scans cells in one deterministic hash-derived circular order
and rechecks exact keys through their referenced slots. Lookup scans overflow
only for a structurally valid Present summary.

Before releasing a completed insert/cancel/unlink mutation, a helper captures
the exact Present token and performs a budgeted stable scan for any current
overflow binding with that canonical bucket. After an empty full scan and exact
operation/mutation revalidation, it CASes only `Present(X) -> Empty(X)`. It does
not return to initial zero: the retained exact identity makes every summary
version unique, so a delayed setter expecting an older empty value and a delayed
clearer expecting an older Present value both fail after later churn. A current
overflow witness retains Present; deadline, unstable, or malformed observations
retain the conservative token and the helpable mutation. Malformed codec or
reserved bits fail closed as corruption.

The overflow directory preserves the capacity contract: while a publisher owns
one reserved value slot, at most `SlotCount - 1` other live bindings exist, so at
least one of its `SlotCount` cells is empty if primary placement is unavailable.
Deletion returns cells directly to empty because lookup scans a fixed candidate
set or the complete logically-present overflow directory; no probe chain or
tombstone must be preserved. A conservative Present token may survive a bounded
or interrupted cleanup, but any later exact mutation can re-run the scan. Normal
infinite-budget churn converges to versioned Empty and restores O(1) primary-only
missing lookups. No rebuild, compaction, epoch flip, or stalled maintenance owner
exists.

**Rationale**: The common path is a short fixed scan with stable deletion and no
global maintenance. The bounded fallback handles adversarial exact hash
collisions without reporting directory-full before value capacity is exhausted.
Pathological spill lookup is deliberately O(`SlotCount`) but remains bounded,
observable, and lock-free. The per-home descriptor serializes only short
directory mutations and is lock-free because its complete operation is fully
described before publication and any caller can finish it. This is preferable to
silently reducing configured capacity or introducing a fragile general
reclamation scheme. The explicit 1,048,575-slot ceiling is substantially above
the practical mapped capacity for large values and purchases a deterministic
33-bit generation fence in every helper-visible word. It is preferable to a
platform-specific 128-bit CAS, crash-sensitive hazard registration, or a paused
helper blocking slot reuse.

**Alternatives rejected**:

- Set-associative buckets without full-capacity overflow can saturate before
  value slots and would require a new public capacity outcome.
- Lock-free open addressing with permanent tombstones eventually degrades all
  missing lookups under churn; clearing/moving tombstones safely requires extra
  coordination.
- Cooperative whole-table migration needs quiescence before table reuse; a
  paused reader can pin an epoch and eventually stop maintenance progress.
- Chained nodes tied to reusable value slots require safe unlinking/reclamation
  and can form stale links or ABA cycles.
- Leaving generation only in the bucket mutation binding allows a helper to
  validate that binding, pause across completion/reclaim/reuse, and then act on
  untagged operation/location words whose bit pattern may repeat.
- Shared hazard/refcount registration either introduces a crash-recovery leak or
  permits a stopped helper to delay reclamation, contradicting the progress
  requirement.

## Decision 5: Fence slots with one generation/state control word

**Decision**: Each slot has an atomic control word containing 3 state bits, 33
slot-generation bits, and a 28-bit participant token. The participant token
packs a nonzero record index and that record's incarnation using layout-derived
bit widths. The binding codec reserves 31 bits for `slotIndex + 1` and the same
33-bit generation. A `Free` control has participant token zero. The first
`Free -> Initializing` CAS already embeds the opener's active participant token,
so a crash before any later metadata write remains classifiable. A slot advances
generation before returning to `Free`; terminal generation publishes `Retired`
instead of wrapping.

The former reserved `int32` at slot offset 52 is `PublicationIntent`:
`None=0`, `ExplicitReservation=1`, and `AtomicPublication=2`. Required-feature
bit 1 (`publication_intent`) makes that interpretation mandatory; together with
the versioned spill-summary bit, the current required mask is `3`. The intent is
ordinary metadata written under exclusive Initializing ownership before any
directory-cell binding publication and immutable for that generation. The exact
current-generation `Insert/Prepared` directory operation is release-published as
the metadata-ready marker before canonical mutation or directory-cell
publication. Pre-metadata Initializing means operation zero with no exact
current mutation/cell reference. Unknown intent on a discoverable current
lifecycle, or a current mutation/cell without its required marker, is
corruption; stale bytes are ignored in Free/Retired and pre-metadata
Initializing.

The core state flow is:

```text
Free(0) -> Initializing(p) -> Reserved(p) -> Published(0) -> RemoveRequested(0)
              |            |                         |
              `-> Aborting <-'                         `-> Reclaiming -> Free(next generation)

Commit clears the participant token because published values are unowned;
abort/recovery clears it while publishing a fully helpable state. Any recoverable
owner-controlled state may be changed only after participant classification. A
slot at generation limit becomes Retired instead of Free.
```

Key, lengths, offsets, intent, and the fixed descriptor bytes are prepared
before publishing the directory mutation descriptor. Binding installation
makes a lifecycle physically discoverable for helping, but ordering depends on
intent. An explicit direct reservation establishes key ownership and becomes a
duplicate witness at the exact `Initializing -> Reserved` CAS. Simple and
segmented convenience publication use `AtomicPublication`; their Initializing
and Reserved stages remain tentative, and the public operation orders only at
the `Reserved -> Published` commit CAS. A contender may exhaust its bounded
budget as `StoreBusy`, but cannot report `DuplicateKey` solely from a tentative
intent/state. Both workflows remain invisible to acquisition until Published.
Logical removal is the
`Published -> RemoveRequested` CAS. Reclamation owns the slot with
`RemoveRequested -> Reclaiming`, clears the exact directory binding and
generation-tagged directory metadata with exact CAS, advances generation, and
publishes `Free`. Reclaim helpers do not zero ordinary slot metadata: two
helpers may both observe `Reclaiming`, and a delayed loser must have no ordinary
write capable of erasing a reused generation. Metadata is semantically ignored
while Free/Retired and is completely overwritten only after the successful next
`Free(g+1) -> Initializing(g+1,p)` claim grants exclusive ownership. A later
lifecycle treats any older tagged helper residue as stale and may exact-clear
it; it never interprets that residue as its own operation or location.

A lookup or maintenance result is only a cached exact-reference-word witness.
Reclamation may clear that word and its operation descriptor before advancing
the slot generation, so a later consumer must not combine the old source
observation with the newer slot snapshot and call the transitional pair corrupt.
On a would-be corruption, the chosen rule is source/slot/source joint
revalidation: acquire-read the exact raw reference word, take a fresh stable and
fully shape-validated snapshot of its separately decoded slot binding, and
acquire-read the same raw word again. A primary/overflow source word equals the
binding; a spill-summary source is the complete encoded `Present(binding)` word.
A changed source restarts a budgeted lookup or maintenance retry; only an
unchanged exact reference word enclosing a repeated invalid slot shape fails
closed. This preserves corruption detection without adding a multi-word atomic,
shared epoch, or cross-process synchronization owner.

An owned reservation may publish unowned `Aborting` while another process is
between insert-helper validations. That transition is legal protocol progress,
not corruption. The helper re-reads the exact operation/control identity before
classifying a failed `Reserved` publication, switches to cancellation cleanup
for `Aborting`/`Reclaiming`, and treats an exact versioned Empty spill token from
that cancellation as terminal. Generation-tagged comparisons still fence a
helper that resumes only after reclaim and reuse.

The same rule is required outside the helper loop. For explicit reservation,
recovery/cancellation before `Initializing -> Reserved` leaves no abstract key
owner; after that CAS the reserve has ordered. For atomic convenience
publication, Reserved is still tentative and recovery may discard it until the
Published CAS wins. Supported recovery never cancels a live Active owner:
normal recovery preserves it, and the current-process override requires
process-wide writer quiescence. A concurrent override and live writer is
outside the result contract, while exact generation fencing remains mandatory.
Lower-generation, unknown discoverable intent, or impossible same-generation
states remain `CorruptStore`, preserving fail-closed detection.

**Rationale**: The control word fences every stale token, while the immutable
intent separates two public policies that intentionally reuse the same internal
state chain. This avoids treating an internal Reserved stage of atomic publish
as a completed public operation or weakening explicit zero-copy reservation
semantics. Transitional states retain only one slot/key and are locally
helpable. Generation retirement is deterministic and safer than an ABA-producing
wrap.

**Alternatives rejected**:

- Separate atomic state and generation fields admit torn identity observations.
- Clearing the key binding at logical removal would allow republish while old
  leases still retain the generation, changing current duplicate-key behavior.
- Reusing a slot before clearing its exact old binding permits stale lookup and
  deletion to affect the new lifecycle.
- Inferring public ordering from `Reserved` without an intent conflates an
  explicit reservation with the internal reserve phase of atomic convenience
  publication and permits false `DuplicateKey` results.

## Decision 6: Make lease records the reclamation authority

**Decision**: Do not use a decrementing slot reader count as the sole authority.
Each lease record has a 64-bit control word containing 3 state bits, 33 record-
incarnation bits, and the same 28-bit participant token, plus the exact slot
binding. Acquisition CASes `Free(r, participant=0)` directly to
`Claiming(r, participant)`, so recovery knows the owner even if the process stops
at that instruction. It then fills the target binding, publishes `Active`, and
revalidates both directory binding and slot `Published` state. If revalidation
fails it relinquishes the record and does not expose payload bytes.

Removal first changes the slot to `RemoveRequested`, then scans for stable active
records matching the exact binding. A claiming acquisition that becomes active
after removal must fail its post-validation; an acquisition active before
removal is visible to the subsequent scan. Release is one CAS out of `Active`.
Remove, final release, later remove calls, allocation pressure, and explicit
recovery may all attempt the same idempotent reclaim CAS. Exactly one succeeds.

**Rationale**: Record state makes crash recovery idempotent. A process cannot die
between an unrecorded reader-count increment and a recoverable lease. Several
readers of one value remain independent and a paused lease pins only its record
and value generation.

**Alternatives rejected**:

- A shared `UsageCount` increment/decrement has unrecoverable crash windows and
  cannot distinguish stale record reuse for the same slot generation.
- Per-key reader locks violate progress and the no-exclusive-owner requirement.
- Copying payload bytes removes the lease problem but violates zero-copy scope.

## Decision 7: Register recoverable participant incarnations before data claims

**Decision**: Layout 2.0 contains a fixed participant-record table configured at
creation (64 records by default, maximum 1,048,575). One open `MemoryStore`
handle consumes one record. The 28-bit participant token uses
`ceil(log2(ParticipantRecordCount + 1))` low bits for record index plus one and
the remaining bits for record incarnation. The default therefore has 7 index
bits and 21 incarnation bits; the maximum table still has at least 8 incarnation
bits. Records retire rather than repeat a token.

Under the allowed cold lifecycle lock, open CASes a free participant control to
`Registering`, atomically including PID and record incarnation, writes explicit
platform identity kind/process-start value and the exact Linux PID-namespace
token, then release-publishes `Active` before the public handle can execute a
data operation. The creator first writes its namespace token and an Enabled/
Mixed recovery mode into the store header before `Ready`. A different or
unproven Linux opener release-publishes the irreversible Mixed mode before its
first Registering CAS and then continues ordinary KV access. Windows uses zero.

Slot and lease controls atomically embed the complete participant token in their
first claim CAS and revalidate the exact participant control after claiming.
Normal data operations carry that locally cached token and do not mutate the
participant record. A participant record is not reused
until local disposal or explicit stale-owner recovery has closed entry and a
complete bounded scan proves no slot, lease record, or directory descriptor
references its token. Its incarnation advances within the configured token codec
before reuse and retires instead of wrapping.

Normal handle disposal stops local entry, waits for already-entered local calls,
changes its participant record to `Closing`, relinquishes exact referenced
ownership, proves no references remain, publishes unowned/helpable `Reclaiming`,
then advances/free-publishes or retires the record and unmaps only afterward.
Stale-owner recovery may similarly use `Recovering -> Reclaiming` after PID/
namespace/start classification and a zero-reference scan. A stable Active
snapshot includes the per-record namespace and compares it before any PID/start
lookup. A partial Registering record instead uses the creator header namespace
for presence-only classification only while mode is Enabled; Mixed makes that
classification Unsupported because ordinary fields may still contain an earlier
incarnation's bytes. Closing/Recovering handoffs remain helpable. Another
handle in the same or another process uses a different record. Unknown liveness
preserves the active participant and reports unsupported.

If the table is full, open returns the appended
`StoreOpenStatus.ParticipantTableFull`; already-open handles and their data paths
are unaffected.

**Rationale**: PID alone is reusable and namespace-relative, and writing identity
after a slot/lease CAS creates an unrecoverable crash window. A store-global
namespace mode closes the crash immediately after the Registering CAS: a cross-
namespace opener release-publishes Mixed before that CAS, and recovery acquire-
loads mode after snapshotting control. Cold registration gives the first hot CAS
a compact recoverable token without adding registry cache-line traffic to normal
operations. Delaying participant-record reuse until all references disappear
makes the compact embedded token safe without fitting a full process identity in
every claim word.

**Alternatives rejected**:

- Post-claim owner metadata cannot distinguish a dead claimant from a live
  process paused immediately after CAS.
- Packing a probabilistic PID/start/nonce hash into 64 bits weakens the exact
  incarnation guarantee and still leaves slot generation/state requirements.
- A native 128-bit atomic owner+generation word adds a runtime dependency and is
  unavailable on the baseline.
- Automatic background recovery violates caller control and could misclassify
  live owners.

## Decision 8: Treat waits as bounded local retry/backoff

**Decision**: Preserve `StoreWaitOptions` source shape. In v2 it bounds CAS retry,
state revalidation, helping, and backoff; it does not wait for a global lock or
turn the store into a key-arrival/capacity notification service. Cancellation
wins if observed before the operation's linearization point. `StoreBusy` means
the local contention budget expired. `StoreFull`, `LeaseTableFull`,
`DuplicateKey`, `RemovePending`, and `NotFound` retain distinct meanings.
`StoreFull` is determined by physical slot reuse, not abstract key ownership:
every non-Free slot, including tentative atomic-publication and cleanup states,
consumes capacity. After an initial absent-key lookup, candidate claim precedes
final same-key arbitration, so genuine `StoreFull` may precede `DuplicateKey` in
a race when no candidate is reusable. Because sequential scans can miss a free
slot rotating behind the scanner, scan exhaustion is provisional. After one
helping/reprobe pass, the allocator uses a process-local nonblocking guard and
eager `long[SlotCount]` scratch buffer to perform two same-order control-word
collects. Two structurally valid, all-non-Free, exactly equal collects confirm
the full instant between them. A malformed slot state/generation/owner/token
shape is `CorruptStore`, even when the malformed word is equal across passes.
Free/change/guard conflict is ordinary contention: `NoWait` terminates
`StoreBusy`, while finite and infinite callers retry under their operation-wide
budget. Lease-record scan exhaustion uses the
same exact-proof rule with an eager per-open `long[LeaseRecordCount]` buffer and
nonblocking local guard. Two same-order, structurally valid, all-non-Free,
exactly equal lease-control collects confirm `LeaseTableFull` at the candidate
instant between them. Free/change/guard conflict is contention, malformed
state/incarnation/owner/participant-token shape is `CorruptStore`, and monotonic
lease incarnation advance prevents control ABA.

The proof requires no exact control ABA. Failed pre-metadata claims therefore
follow the existing helpable forward path to `Aborting`/`Reclaiming` and advance
generation; they never restore the original same-generation `Free` word. The
buffer is private to one open handle, costs eight bytes per slot (about 8 MiB at
the layout ceiling), and causes neither per-operation allocation nor mapped,
named, or OS synchronization.

Use `SpinWait`/bounded yield on the hot retry path and check time/cancellation at
bounded intervals so fast successes avoid repeated clock or token overhead. A
caller that has not crossed its public ordering point must relinquish its
owner-controlled claim before returning. It may hand a slot to the unowned,
fully helpable `Aborting` state when physical unlink cannot finish inside the
bound; this is not a leaked reservation. Once a public ordering point has
occurred, cancellation does not rewrite the normal result. `TryRemove` reports
logical absence and returns conservative `RemovePending` when its post-removal
lease classification cannot finish within the bound; physical reclaim may
finish cooperatively after `Success`/`RemovePending`.

**Rationale**: This preserves deterministic bounded calls and existing status
numbers while removing the shared semaphore meaning from v2. Capacity and key
state are external/application concerns unless a future explicit API adds
notifications.

## Decision 9: Use a lock-free local lifetime gate

**Decision**: Replace per-operation monitor entry with one atomic local lifetime
word holding a dispose flag and active-operation count. Operation entry CASes the
count only while open; exit decrements it. Disposal sets the flag and waits only
for operations already entered through that local handle before unmapping.
Borrowed views remain invalid after release/abort/recovery/handle disposal as
documented.

**Rationale**: No managed monitor is paid on each v2 operation, and a paused
operation cannot affect other handles or processes. Waiting during explicit
local disposal is allowed because mapped memory cannot be safely unmapped while
that handle is executing.

## Decision 10: Keep native clients v1.2-only and version independently

**Decision**: C++ and Python remain layout-v1.2/resource-protocol-1 participants
in this feature and must return an incompatible-layout result for v2 before
payload access. Update compatibility metadata and executable mixed-version
tests, but do not change C ABI 1.0. Target NuGet 2.0.0 independently of mapped
layout 2.0.

**Rationale**: A partial native implementation of atomic ordering or recovery is
more dangerous than explicit rejection. The package major communicates expanded
public token representation and wait semantics even while the legacy facade and
enum numeric assignments remain recognizable.

## Decision 11: Validate safety before throughput

**Decision**: Use five layers: deterministic transition schedules; small-history
linearizability checking; cross-process checkpoint/pause/crash tests; raw Release
memory-order and generation-pattern litmus tests; and allocation/performance/OS
tracing. Keep PR, nightly, and release workloads separate as defined in
`contracts/validation-and-performance.md`.

**Rationale**: Random stress alone rarely hits the critical instruction window,
while deterministic hooks can accidentally add fences that hide memory-order
bugs. Both instrumented and raw paths are required. The full matrix writes over
130 GB and runs for hours, so portable PR validation must not pretend to be the
release qualification.

**Non-convergence rule**: Stop rather than weaken the feature if aligned mapped
atomics fail on Windows/Linux, the one-key lifecycle cannot pass controlled
schedules, directory collisions create duplicate current generations, recovery
can reclaim a live owner or leak a dead one repeatedly, a steady-state path
touches the OS operation lock, or short profiling shows no unrelated-key scaling
after two evidence-driven correction cycles.

## Decision 12: Separate intrinsic latency from parallel scaling on Linux

**Decision**: Qualify both acquire/release and publish/remove at one and eight
processes with three independent 60-second Release trials after a 10-second
warmup. The lock-free profile must meet all of these independently recomputed
conditions:

- at one process, median p99 latency is no greater than the matching legacy
  median p99;
- at eight processes, median throughput is no lower than the matching legacy
  median throughput;
- lock-free eight-process median p99 is at most three times its matching
  one-process median p99 and is also at most 10 microseconds;
- every lock-free raw trial at both process counts has an unevictable observed
  maximum across its sampled candidates of at most 10 milliseconds.

The executable benchmark uses two deterministic keys per worker, a fixed
canonical bucket assignment, separate early and late Algorithm-R reservoirs,
and an unevictable running maximum. Qualification binds the raw evidence to the
exact Linux host, architecture, process count, CPU model, and clean commit, then
recomputes all eight scenario/profile/process-count summary rows from the raw
trials. Both PowerShell importers compute the midpoint with explicit floor
semantics and self-test distinct odd and even value sets so a three-trial
median cannot inherit PowerShell's round-to-nearest integer conversion.

**Evidence**: The original probe accidentally allowed fixed-key collisions and
could lose an early maximum when its latency reservoir replaced that sample.
After correcting both defects, eight-process lock-free operation retained a
large throughput advantage and sub-10-microsecond p99, while the legacy Linux
file lock produced a deceptively low p99 by serializing incumbents even though
its raw stalls reached tens of milliseconds. Reducing the lease table from 64
records to one, changing the slot hash, and changing the slot probe stride did
not materially improve lock-free eight-process p99, so each experiment was
reverted.

**Rationale**: A serialized implementation is not a valid parallel-latency
oracle. The one-process comparison checks intrinsic operation cost, the
eight-process throughput comparison checks useful scaling, the lock-free
one-to-eight ratio limits contention growth, and the absolute p99 and raw-maximum
limits protect tail latency without rewarding convoying. This preserves a hard
performance contract while keeping correctness, host binding, and raw-tail
requirements independent of any aggregate score.

## Decision 13: Use OFD locks and stable Linux rendezvous inodes

**Decision**: Current C# and C++ Linux adapters issue nonblocking
`F_OFD_SETLK` open-file-description locks for byte `[0,1)` on both `.lock` and
`.lifecycle`. Each C# wrapper owns its descriptor and a per-wrapper
non-reentrant local gate. C++ wrappers in one module retain the existing shared
per-path `FileState`/descriptor and timed mutex; distinct native modules,
managed assembly-load contexts, and other open descriptions contend in the
kernel. `EINVAL`, `ENOSYS`, and unsupported-filesystem outcomes fail closed as
Unsupported; there is no fallback to process-associated locking.

Current cleanup retains the empty mode-`0600` `.lock` inode, matching the
already-persistent `.lifecycle` inode. Successful, failed, initialized, and
uninitialized teardown releases held gates and disposes ordinary
synchronization before mapped-region/owner cleanup can enter `.lifecycle`.
Unlock failure closes/retires the descriptor before its local gate is reopened.
These rules affect only cold coordination and legacy v1.2 operations; no v2
steady-state key-value path consults a registry or enters an OS lock.

**Rationale**: The earlier shared-descriptor registry fixed sibling close within
one loaded C# assembly, but its static state was per `AssemblyLoadContext`, not
per OS process. Independently loaded packages and an in-process native adapter
could still use different traditional `F_SETLK` descriptors; same-PID calls did
not contend, and closing either descriptor could release every traditional lock
for that inode. OFD locks bind ownership to each open description, conflict even
inside one PID, and are not released by closing an unrelated descriptor. They
also conflict with traditional record locks used by released clients in other
processes. A persistent pathname removes the independent unlink/recreate inode
split, while descriptor-before-region teardown remains defense in depth for an
older participant that still deletes `.lock` after the last owner.

Concurrent use of an older process-associated-lock implementation and a current
OFD implementation in one OS process is explicitly unsupported: closing any new
contender descriptor can release the older implementation's process-associated
lock. Cross-process old/new compatibility and same-process current/current
managed/native compatibility remain supported.

**Evidence**: Linux regressions cover `.lock` and `.lifecycle`, same-thread and
timed local contenders, foreign-process exclusion, two copies of the current
assembly in separate collectible load contexts, and a same-PID native-style OFD
descriptor in both ownership directions. A concurrent final-close/reopen test
holds the persistent pathname lock, proves the reopened legacy store's own hot
operation returns `StoreBusy`, and proves foreign exclusion until release.
Failed-open ordering records synchronization disposal before region-owner
cleanup. The current C++ adapter uses the same command and closes its lock before
region owner cleanup.

**Alternatives rejected**:

- A managed static registry cannot provide process scope across load contexts or
  native modules.
- Traditional `F_SETLK` plus per-module mutexes cannot enforce same-PID mutual
  exclusion and retains the sibling-close hazard.
- Reentrant local monitors permit accidental same-thread unlock of a live gate.
- Switching to `flock` would not conflict with released record-lock clients.
- Teardown ordering alone cannot eliminate every unlink/recreate window; stable
  rendezvous inodes make pathname identity independent of close timing.
