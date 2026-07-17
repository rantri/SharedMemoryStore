# Architecture

SharedMemoryStore is one language-neutral mapped protocol with three language
surfaces. C# and native C++ contain independent SMS2 engines; Python is an
idiomatic lifetime adapter over the native C ABI. All implementations share
byte layout, atomic transitions, hashing, status values, resource naming, owner
classification, and recovery rules.

## Fixed protocol identity

```text
SMS2 layout 2.0
resource protocol 2
required features 7
optional features 0
C ABI 2.0
```

The immutable identity exposed by a handle is `(2, 0, 2, 7, 0)`. NuGet
`3.0.0`, CMake `1.0.0`, and Python `1.0.0` are distribution versions, not
alternate mapped protocols.

Canonical definitions:

- [protocol overview](../protocol/README.md)
- [layout 2.0](../protocol/layout-v2.0.md)
- [resource protocol 2](../protocol/resource-naming-v2.md)
- [executable manifest](../protocol/fixtures/v2.0/manifest.json)
- [public API contract](../specs/010-lock-free-only-multilang/contracts/public-api.md)
- [protocol conformance contract](../specs/010-lock-free-only-multilang/contracts/protocol-conformance.md)
- [interoperability and validation](../specs/010-lock-free-only-multilang/contracts/interoperability-and-validation.md)

## Responsibility boundaries

| Boundary | Owns | Must not own |
|---|---|---|
| Protocol contract | bytes, offsets, sizes, hashes, states, codecs, memory order, feature masks, corruption rules | language objects, platform handles, presentation |
| Store engine | operation orchestration, local budgets, helping, exact token validation, corruption latch | resource-name derivation, package loading |
| Platform lifecycle | mappings, cold locks, owner evidence, namespace identity, final cleanup | key lookup, slot algorithms, payload schemas |
| Language adapter | idiomatic options, statuses, RAII/context management, cancellation and view lifetime | different shared semantics |
| Diagnostics/qualification | bounded observation, counters, failure schedules, performance gates | correctness dependencies or hidden workers |

Dependency direction:

```text
C# public API -> managed SMS2 engine -> protocol primitives -> platform adapter
C++ RAII API -> C ABI 2 -> native SMS2 engine -> protocol primitives -> platform adapter
Python API -> ctypes ABI 2 adapter ------------------------------^

tests and agents -> public APIs plus deterministic checkpoint seams
```

No engine calls a language runtime from mapped protocol code. The C ABI never
exposes C++ standard-library objects, exceptions, allocators, or mapped record
pointers.

## Repository map

| Path | Purpose |
|---|---|
| [`src/SharedMemoryStore/MemoryStore.cs`](../src/SharedMemoryStore/MemoryStore.cs) | Managed public handle and local operation-entry lifetime gate |
| [`src/SharedMemoryStore/LayoutV2/`](../src/SharedMemoryStore/LayoutV2/) | Managed SMS2 sizes, offsets, codecs, and record access |
| [`src/SharedMemoryStore/LockFree/`](../src/SharedMemoryStore/LockFree/) | Managed directory, slot, lease, participant, recovery, and diagnostics algorithms |
| [`src/SharedMemoryStore/Interop/`](../src/SharedMemoryStore/Interop/) | Windows/Linux mapping, cold coordination, identity, and cleanup |
| [`src/cpp/include/shared_memory_store/`](../src/cpp/include/shared_memory_store/) | C ABI 2 and C++20 public headers |
| [`src/cpp/src/`](../src/cpp/src/) | Native SMS2 engine and platform adapters |
| [`src/python/shared_memory_store/`](../src/python/shared_memory_store/) | Python values, context managers, `ctypes` declarations, and adjacent-library loader |
| [`protocol/`](../protocol/) | Current language-neutral protocol and conformance evidence |
| [`tests/SharedMemoryStore.InteropTests/`](../tests/SharedMemoryStore.InteropTests/) | Ordered runtime-pair lifecycle, recovery, diagnostics, and ownership tests |

## Mapped layout

The region contains fixed sections calculated before creation:

```text
512-byte store header
participant records (64 bytes each)
primary directory buckets (128 bytes each, eight lanes)
overflow directory bindings (8 bytes each)
lease records (64 bytes each)
value-slot metadata (128 bytes each)
fixed-stride key storage
fixed-stride descriptor storage
fixed-stride payload storage
```

Offsets and lengths use checked 64-bit arithmetic and required alignment. Every
opener recomputes the layout and validates it against the header and actual
mapped capacity before accessing a later section.

All cross-process atomic words are naturally aligned 64-bit values. The
qualified x86-64 implementation uses sequentially consistent read/modify/write
operations across processes. Plain descriptor, key, and payload bytes are
published only after their owning atomic state establishes visibility.

## Participants

Every open handle claims one generation-tagged participant record before it may
claim a slot or lease. The participant publishes process identity, process-start
evidence, open sequence, and PID namespace identity through helpable states:

```text
Free -> Registering -> Active -> Closing -> Reclaiming -> Free
                         \-> Recovering -/
```

Generation wrap retires a record instead of allowing an ambiguous token.
Participant identity is included in slot, reservation, and lease ownership so a
reused PID or participant index cannot authorize a later incarnation.

## Key directory

SMS2 uses a fixed primary directory and bounded overflow directory:

- canonical FNV-1a selects two primary buckets;
- each bucket contains eight generation-tagged lanes;
- exact key bytes confirm equality;
- a versioned spill summary announces possible overflow candidates; and
- overflow scans are bounded by configured capacity.

Directory modifications publish helpable operation descriptors. Participants
may complete an interrupted insertion or unlink only after validating the exact
operation, location, publisher binding, slot generation, and state. A summary
cannot claim “empty” while a valid overflow binding is reachable.

## Slots and reservations

A slot carries generation-tagged control, directory binding/location,
helpable-directory operation, key hash and lengths, publication intent, progress
and commit sequence, plus immutable section offsets.

High-level lifecycle:

```text
Free -> Initializing -> Reserved -> Published -> PendingRemoval -> Reclaiming -> Free
                             \-> Reclaiming -------------------------------/
```

Atomic publication and explicit reservation use distinct publication-intent
values. Readers may project bytes only after validating a published slot and
then revalidating it after lease activation. Reclamation advances the generation
before reuse; generation wrap retires the slot.

## Leases

Acquisition claims a lease record, binds the exact participant and slot
generation, revalidates directory publication, and only then exposes descriptor
and payload spans. Release changes only the exact active lease incarnation.

Logical removal unlinks the key first. Existing leases keep the old generation
readable; new acquires cannot find it. The final release or a bounded helper can
complete physical reclamation.

## Helping and lock-free progress

Hot operations use compare/exchange loops, immutable published bytes, bounded
candidate scans, version revalidation, and cooperative completion. An
interrupted participant cannot own an unobservable process mutex that blocks
the entire store. Another participant can finish or safely roll back a
published transition.

Lock-free means system-wide progress. It does not mean every individual call is
wait-free. A no-wait or finite call can return `StoreBusy` after exhausting its
local retry/help/backoff budget.

Hot operations include publish, segmented publish, reserve, advance, commit,
abort, acquire, projection validation, release, remove, reclaim, explicit
recovery, and diagnostics. They do not acquire named mutexes or Linux record
locks.

## Cold platform lifecycle

Physical create/open, participant attachment, close, owner reconciliation, and
final cleanup use bounded OS coordination.

Windows uses the canonical named mapping and cold named mutex. Kernel handle
lifetime removes resources after the final close.

Linux uses deterministic files under `/dev/shm/SharedMemoryStore` (or a guarded
temporary fallback): region, stable cold locks, owner sidecar, private owner
anchors, and finalized release markers. Exact PID/start/namespace identity and
anchor-lock evidence prevent PID reuse or container namespace ambiguity from
being treated as stale.

Cold coordination is acquired before mapping inspection. An existing zero,
truncated, noncurrent, or malformed header is rejected; it is never initialized
by an opener.

## Recovery

There is no hidden recovery worker. A caller explicitly scans leases or
reservations. For each candidate the engine:

1. reads a complete incarnation and owner token;
2. classifies the participant as current, live, stale, unsupported,
   inconsistent, or changing;
3. retains any owner that may be live;
4. revalidates that the shared record is unchanged; and
5. performs one exact recovery compare/exchange.

Recovery may help directory and slot transitions, but cannot reclaim a later
incarnation. Reports distinguish recovered, active, unsupported, and failed
records.

## Process-local lifetime gates

One handle accepts concurrent calls. A local entry counter admits operations
until close begins. Close prevents new entries, drains entered calls, invalidates
local token access, releases the participant, and then performs bounded platform
cleanup.

This gate protects language object lifetime only. It is not shared, mapped, or
used as a hot operation lock. Python's local lock is limited to this close-safe
entry accounting; native calls can progress concurrently.

## Diagnostics

Bounded scanners report shared slot, lease, participant, and directory state.
Process-local atomics report retries, help, contention exhaustion, token errors,
recovery, owner classification, and status counts. Diagnostics do not mutate
ownership and are not an algorithmic dependency.

See [diagnostics.md](diagnostics.md).

## Corruption boundary

Caller input, capacity pressure, legal races, cancellation, and missed bounded
work do not latch corruption. A terminal `CorruptStore` condition is published
only after an impossible shared observation is revalidated.

After corruption, operations fail closed. No implementation guesses record
ownership or projects questionable payload bytes.

## Interoperability

The validation matrix covers all nine ordered producer/consumer pairs among
.NET, C++, and Python. The same command and checkpoint catalogs exercise binary
publication, segmented publication, reservations, leases, removal/reuse,
participant capacity, crash recovery, diagnostics, cold-lock independence,
corruption rejection, and Linux final-owner cleanup.

Application interoperability also requires a shared byte schema. The library
does not normalize text, choose integer encoding, or serialize application
objects.

## Deployment replacement

SMS2 has no in-place converter. A current implementation rejects a noncurrent
mapping before payload access. Drain all tokens, close all handles, replace the
physical store, create fresh SMS2 resources, and republish from an
application-owned authoritative source. Side-by-side operation requires a new
public name.
