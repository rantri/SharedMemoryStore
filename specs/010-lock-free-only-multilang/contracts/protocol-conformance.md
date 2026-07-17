# Contract: Canonical SMS2 Protocol Conformance

## Scope

This contract defines the minimum conformance boundary for every current C#,
C++, and Python distribution. Python may delegate mapped-memory operations to
the packaged native library, but its observable statuses, ownership lifetimes,
protocol identity, and diagnostics remain subject to the same contract.

An implementation is conformant only when it creates, reads, mutates, recovers,
and diagnoses the canonical protocol below. Recognizing a header solely to
reject it is not support for another protocol.

## Fixed Protocol Identity

| Property | Canonical value |
|---|---:|
| Magic | `SMS2` / `0x32534d53` |
| Mapped layout | `2.0` |
| Resource protocol | `2` |
| Required features | exact mask `7` |
| Creator optional features | `0` |
| Byte order | little-endian |
| Qualified architecture | x86-64 |
| Shared atomic width/alignment | 8 bytes / 8 bytes |
| Store header | 512 bytes |
| Participant record | 64 bytes |
| Primary directory bucket | 128 bytes, eight lanes |
| Overflow binding | 8 bytes |
| Lease record | 64 bytes |
| Value-slot metadata | 128 bytes |

Required bit `0` is `versioned_empty_spill_summary`, bit `1` is
`publication_intent`, and bit `2` is `pid_namespace_identity`. A current opener
MUST require all three bits and MUST reject a mapping with any missing or
unknown required bit. Current creators MUST emit optional mask zero; optional
bits, if defined by a later compatible contract, MUST NOT be treated as required
compatibility proof by an older reader.

The canonical region order, every header field, section calculation, record
offset, control-word bit range, state value, and directory descriptor encoding
are fixed by `protocol/fixtures/v2.0/manifest.json` and explained by
`protocol/layout-v2.0.md`. Implementations MUST NOT substitute compiler-native
structure layout without size and offset assertions.

## Atomic and Memory-Ordering Parity

Every shared atomic is one naturally aligned 64-bit mapped word. All runtimes
MUST implement the following equivalent operations:

| Protocol action | Required ordering |
|---|---|
| Observe a shared control, binding, mutation, location, operation, spill summary, or counter | Acquire load |
| Publish initialized immutable metadata or a new visible state | Release store where the transition is single-writer |
| Claim, help, hand off, release, advance a lifecycle, or latch corruption | Sequentially consistent full-word compare/exchange or RMW |
| Failed compare/exchange observation | At least acquire semantics and never an invalid release-only failure order |

The C# implementation uses `Volatile.Read/Write` and `Interlocked`; the native
implementation MUST provide equivalent semantics through its qualified mapped
atomic adapter. `volatile`, ordinary racy reads, process-local mutex memory
effects, and a named OS lock are not substitutes for these orderings.

Control words MUST be compared and replaced in full. A helper MUST include the
exact generation and participant or target identity encoded by the protocol;
it MUST NOT update a state field independently or act on a decoded identity
after the raw source word has changed. Terminal generations retire rather than
wrap to a prior valid identity.

Before release-publishing a discoverable state, its owner MUST write all
immutable fields and bytes required by that state's validation rule. A reader
that acquire-observes the publication may then read those immutable fields.
No implementation may expose payload or descriptor bytes from Initializing,
tentative atomic publication, a stale generation, or an invalid lease.

Every mapped-data operation MUST acquire-read the store control before a new
projection or mutation. Persistent corruption may be published only by an exact
full-word `Ready -> Corrupt` CAS after the protocol's required repeated stable
collections and confirmation CAS operations. Caller input, capacity,
contention, cancellation, disposal, and legal concurrent progress MUST NOT
latch corruption.

## Creation, Attachment, and Participant Contract

All runtimes derive the same physical mapping and synchronization resources
from the public store name. `CreateNew`, `OpenExisting`, and `CreateOrOpen` MUST
agree across runtimes on one physical creator.

Only a platform mapping result carrying `CreatedNew` authorizes zeroing and
initialization. Open mode, zero magic, or an all-zero existing page is not
creation evidence. Windows acquires the named mutex before mapping. Linux
acquires `.lifecycle`, reconciles resource-protocol-2 ownership state, then
acquires the stable `.lock` inode before mapping and publishing an owner. The
cold gates remain held through header publication or validation, namespace
admission, and participant registration, and are released in reverse order.

No store handle may escape until it owns one exact Active participant
incarnation. Participant capacity exhaustion returns
`ParticipantTableFull = 11` and does not mutate an unrelated live record.
Closing drains process-local entered operations before publishing Closing,
cleans or hands off exact owned slot and lease records, and retires the
participant only after exact-reference scans permit it.

## Fail-Closed Validation

Before projecting any directory, slot, key, descriptor, or payload address, an
opener MUST validate all of the following:

1. The process and atomic adapter are qualified x86-64, little-endian, and
   lock-free for aligned 64-bit words.
2. The actual mapped capacity is large enough to read the fixed header.
3. Magic, layout major/minor, header length, resource protocol, and exact
   required-feature mask match this contract.
4. Store control is exactly Ready; Unsupported and Corrupt fail explicitly,
   while unpublished existing mappings follow the documented bounded cold-open
   outcome and are never initialized by an opener.
5. Every configured count and maximum is within protocol limits, including
   slot and participant counts `1..1,048,575`.
6. Participant token bit allocation, primary lane/bucket arithmetic, strides,
   offsets, lengths, and required bytes recompute exactly from header
   dimensions using checked arithmetic.
7. Every atomic address is 8-byte aligned, every cache-line section has its
   required alignment, sections are ordered without overlap, and all projected
   ranges end within the actual mapping capacity and declared total bytes.
8. Required immutable header fields, store identity, PID-namespace fields, and
   recovery mode have structurally valid values.

During operation, every observed participant, slot, lease, directory binding,
spill summary, location, and operation word MUST pass its complete state,
reserved-bit, generation, owner-token, and in-range checks before use. A single
moving observation causes retry or a bounded contention outcome. Persistent
invalid structure may become Corrupt only after the exact revalidation required
by layout 2.0. An unknown or retired-layout header returns an incompatible
outcome before payload access; it is never treated as an empty store.

## Conformance Fixture Obligations

`protocol/fixtures/v2.0/manifest.json` is the common executable fixture. It MUST
contain and every distribution MUST test:

- protocol, ABI-independent status, open-mode, feature, architecture, and byte-
  order identities;
- the complete 512-byte header and every record size and field offset;
- valid and invalid layout calculations, alignment boundaries, count limits,
  and arithmetic-overflow vectors;
- participant, slot, lease, binding, spill-summary, directory-location, and
  directory-operation encode/decode vectors, including terminal generations
  and malformed reserved bits;
- FNV-1a hash and exact-key vectors, including binary keys and exact hash
  collisions;
- Windows and Linux resource-name and ownership-artifact vectors;
- public status numbers, including `ParticipantTableFull = 11` and operation
  statuses `0..22`;
- representative offline mapped snapshots for empty, reserved, published,
  leased, pending-removal, spilled, recovering, reclaimed, and corrupt states.

Fixture binaries are offline parser/conformance inputs only. They MUST NOT be
opened as live stores because their process, participant, and platform owner
identities are synthetic.

A protocol-affecting change is incomplete unless the narrative contract,
manifest, all three runtime conformance suites, interoperability matrix, and
compatibility declaration change together. No runtime-private fixture may
override the shared manifest.

## No Hot Operation Lock

After successful attachment and Active participant publication, these paths
MUST NOT acquire a Windows named mutex, Linux `.lock`/`.lifecycle`, `flock`,
process-shared mutex, globally exclusive owner lock, or any equivalent
store-wide operation lock:

- contiguous or segmented publish;
- reserve, writable projection validation, advance, commit, and abort;
- acquire, immutable projection validation, and lease release;
- logical remove, directory unlink, reclamation, and helping;
- explicit lease or reservation recovery;
- diagnostics and corruption-state observation.

These paths use only mapped atomics, immutable mapped bytes, bounded scans,
helpable descriptors, operation-wide deadline/cancellation checks, backoff, and
process-local telemetry. Process-local lifetime gates may reject new calls and
drain callbacks during disposal, but they MUST NOT become a shared progress
dependency or protect mapped protocol transitions.

Cold synchronization remains permitted only for bounded create/open ownership,
header compatibility, participant registration, and platform owner cleanup.
Automated qualification MUST trace or instrument the steady-state paths on both
supported operating systems and fail if they enter a named/file operation lock.

## Cross-Runtime Behavioral Evidence

Conformance requires all nine ordered producer-consumer pairs among C#, C++,
and Python plus mixed three-runtime workloads. Evidence MUST cover exact binary
publish/acquire, segmented publication, reservation visibility and completion,
same-key races, exact hash collisions and overflow churn, foreign leases,
logical removal and final release, reuse, participant exhaustion, bounded wait
and cancellation, deterministic pause/help schedules, crash recovery, PID and
namespace ambiguity, corruption propagation, disposal, and clean package
consumption.

Native raw atomic tests and cross-runtime schedules MUST run in Release on
Windows x64 and Linux x64. Passing one compiler, one runtime pair, Debug-only
tests, or stress without transition coverage is insufficient qualification.

## Compatibility and Migration

The current distributions create and read SMS2 only. SMS1 is retired and has no
in-place converter, fallback reader, or parallel resource name. Migration
requires all old handles to be drained and closed, the retired mapping to be
recreated, and values to be republished from application-owned data. Package,
native ABI, mapped-layout, and resource-protocol versions remain independent
identities and MUST be reported unambiguously by every distribution.
