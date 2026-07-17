# Phase 0 Research: Lock-Free-Only Multi-Language Store

This research resolves the design choices needed to retire layout 1.2 and make
the existing layout 2.0 protocol the only current protocol implemented by C#,
C++, and Python. The existing layout-v2 documentation and validated C# engine
are the implementation baseline; this feature turns that baseline into a
language-neutral product contract rather than defining a third layout.

## One SMS2 Mask-7 Protocol

**Decision**: Use the existing `SMS2` mapped layout 2.0 and resource protocol 2
as the sole creatable and readable current protocol. Every creator writes the
exact required-feature mask `7` (`versioned_empty_spill_summary`,
`publication_intent`, and `pid_namespace_identity`) and optional-feature mask
`0`. Current clients reject `SMS1`, unknown majors, older layout-2 drafts with
required masks `0`, `1`, or `3`, and any unknown required bits before projecting
directory, slot, descriptor, or payload data. Public profile selectors and
legacy sizing paths are removed.

**Rationale**: SMS2 already supplies the generation fencing, participant
identity, helpable directory mutations, terminal corruption latch, and
lock-free data paths required by the feature. Reusing it preserves the existing
qualified wire representation while eliminating the product and test matrix
created by two profiles. Exact required-mask matching prevents a client from
mistaking a pre-release draft for the current protocol.

**Alternatives considered**:

- Retain layout 1.2 as a hidden or read-only engine. Rejected because it leaves
  two synchronization and validation models in product code and permits
  accidental continued dependence on the retired format.
- Define layout 3.0 merely to signal multi-language support. Rejected because
  implementation-language coverage does not change mapped bytes.
- Negotiate subsets of the three required features. Rejected because each bit
  closes a correctness or recovery ambiguity and current clients require all
  three together.

## Faithful Native Port

**Decision**: Port the validated C# SMS2 state machines faithfully into a
modular C++ engine. Preserve the exact control codecs, retry/help behavior,
stable revalidation rules, ordering points, status classification, and
generation-retirement rules. Python uses the packaged C ABI v2 native library
for all mapped-memory and atomic operations; it provides idiomatic ownership
and buffer wrappers but does not implement a second pure-Python state machine.

**Rationale**: The subtle directory, reclamation, and recovery rules are part
of the protocol, not implementation details. In particular, spill-summary
versioning and directory-location publication require exact multi-word
collections and no-op CAS confirmation before corruption can be reported. A
simplified native algorithm protected by a lock would neither interoperate
safely nor satisfy system-wide lock-free progress. A single bundled native core
also gives C++ and Python identical atomic behavior and reduces the number of
independent implementations that must be qualified.

**Alternatives considered**:

- Adapt the existing native layout-v1.2 `Store` by changing record constants.
  Rejected because its global guard, tombstone index, usage counts, PID-only
  recovery, and split lifecycle identifiers do not map to SMS2.
- Reimplement the algorithm independently from the narrative specification.
  Rejected because it creates unnecessary semantic drift from already-tested
  transition logic.
- Manipulate mapped atomics directly in Python. Rejected because Python offers
  no portable cross-process 64-bit CAS with the required memory ordering and
  would make borrowed-buffer lifetime enforcement less reliable.

## Mapped Atomic Abstraction and Toolchain Qualification

**Decision**: Isolate all native mapped atomic access behind a small
`MappedAtomic64` abstraction. On qualified C++20 x86-64 toolchains it uses
`std::atomic_ref<uint64_t>` with acquire loads, release stores, and
sequentially-consistent compare/exchange and read-modify-write operations.
Compilation and startup fail closed unless 64-bit atomic references are always
lock-free and require no more than 8-byte alignment. Every mapped atomic offset
is statically asserted and dynamically bounds/alignment checked. Windows x64
and Linux x64 Release builds must pass cross-process raw visibility and CAS
litmus tests for every supported compiler family before release.

**Rationale**: SMS2 deliberately uses only naturally aligned 64-bit words, a
primitive supported without 128-bit CAS on x86-64. A single adapter makes
memory-order parity reviewable and prevents accidental ordinary or `volatile`
access. The ISO C++ memory model does not itself promise process-shared atomic
semantics for an mmap-backed object, so executable platform/toolchain evidence
is required in addition to source-level assertions.

**Alternatives considered**:

- Use `volatile` fields. Rejected because volatility provides neither atomicity
  nor inter-thread/process ordering.
- Use compiler or OS intrinsics throughout the protocol code. Rejected because
  it spreads platform variation across every state machine; intrinsics remain
  an adapter fallback only if a qualified compiler cannot implement
  lock-free `atomic_ref`.
- Use 128-bit CAS to update related fields together. Rejected because SMS2 was
  designed around generation-tagged one-word descriptors and exact
  revalidation specifically to remain portable without it.
- Use `atomic_ref::wait` or a condition variable. Rejected because their
  process-sharing behavior is not the protocol and a paused waiter must not
  become a progress dependency.

## Cold Open and Resource Protocol

**Decision**: All runtimes implement one resource-protocol-2 cold-open
transaction and retain the existing physical resource identity. On Windows the
named mutex is acquired before creating or opening the mapping. On Linux
`.lifecycle` is acquired first, release markers and stale ownership are
reconciled, then the stable `.lock` inode is acquired before mapping and owner
publication. The gates remain held through actual-capacity mapping, header
initialization or validation, PID-namespace admission, and participant
registration. Only an explicit physical `CreatedNew` disposition authorizes
zeroing and initialization. Gates are released in reverse order and are
unreachable from steady-state data operations.

Every current Linux implementation uses conservative owner evidence compatible
with the documented sidecar format, including private owner anchors, bounded
close cleanup, durable release markers, exact marker reconciliation, and
fail-closed handling of malformed, linked, non-regular, locked, inaccessible,
or otherwise ambiguous artifacts. Python inherits this behavior from the
native library.

**Rationale**: Reusing the physical names makes retired and current mappings
discover one another and fail closed instead of creating parallel stores.
Holding the complete cold transaction prevents double initialization, inode
splits, and a handle escaping before its participant is Active. Anchors and
release markers close Linux crash and PID-namespace windows without placing an
OS lock on a key-value path.

**Alternatives considered**:

- Give SMS2 new mapping names. Rejected because the same public name could then
  silently resolve to two unrelated stores.
- Map first and infer creation from zero magic or `OpenMode`. Rejected because
  an older or paused creator may own an existing unpublished mapping.
- Keep the operation lock around every native call. Rejected because it defeats
  the defining lock-free requirement and allows one paused process to block all
  healthy participants.
- Treat missing or malformed Linux owner metadata as stale. Rejected because
  uncertainty must never authorize deletion of a live mapping.

## C ABI v2

**Decision**: Publish a breaking C ABI major `2` and native SOVERSION `2` rather
than preserving layout-v1 fields. ABI v2 keeps opaque store, lease, and
reservation ownership and fixed-width versioned structures, adds participant
capacity, SMS2 topology/protocol information, expanded lock-free diagnostics,
and `ParticipantTableFull = 11`, while retaining meaningful operation-status
numbers `0..22`. Required-byte calculation includes participant capacity. The
wait contract retains no-wait, finite, and infinite deadlines and gains an
optional opaque process-local cancellation token that can be signaled from
another thread without crossing allocator or exception ownership. C++ wrappers
own the ABI handles with move-only RAII; Python loads only the wheel-packaged
native artifact and exposes context-managed equivalents.

**Rationale**: The existing layout and diagnostics structures describe fields
that do not exist in SMS2, and `sms_store_options` cannot express participant
capacity. Because compatibility is explicitly not required, retaining obsolete
members would make the sole protocol ambiguous. Opaque handles keep C++ runtime
types and allocation ownership behind the ABI, while an opaque cancellation
object gives C and Python the same cancellation outcome as C# without exposing
language-specific token representations.

**Alternatives considered**:

- Add fields while keeping ABI major 1. Rejected because old binaries would
  calculate a different layout and cannot safely participate in SMS2.
- Remove the C ABI and bind Python directly to C++. Rejected because C++ ABI
  stability and exception/allocator ownership are unsuitable package contracts.
- Use a caller-owned plain Boolean cancellation pointer. Rejected because its
  concurrent access and lifetime would not have a portable C ABI contract.
- Preserve obsolete index/tombstone diagnostics as zeros. Rejected because
  placeholder fields obscure the primary/overflow directory model.

## Fixture Authority

**Decision**: Make `protocol/fixtures/v2.0/manifest.json` the executable,
language-neutral conformance authority, paired with the narrative contracts in
`protocol/layout-v2.0.md` and `protocol/resource-naming-v2.md`. Expand it to pin
the complete header, all record sizes and offsets, layout arithmetic, feature
masks, state and status numbers, every control-word codec, hashing and resource
naming vectors, fail-closed examples, and representative offline binary
snapshots. C#, C++, and Python tests consume the same fixture data; no
implementation's private constants are treated as the cross-language source of
truth.

**Rationale**: A shared machine-readable authority detects drift that duplicated
language constants or prose review can miss. Offline mapped snapshots prove
byte interpretation without replaying fake process identities as live
resources. Keeping prose alongside the manifest explains invariants that
cannot be represented by offsets alone.

**Alternatives considered**:

- Treat the C# implementation as the permanent protocol authority. Rejected
  because the feature makes the protocol multi-language and language-neutral.
- Maintain separate fixtures per distribution. Rejected because independently
  updated vectors can all pass while disagreeing.
- Open fixture binaries as live stores. Rejected because fixture ownership and
  liveness metadata are intentionally synthetic.

## Test-Driven Delivery and Qualification

**Decision**: Implement in test-first protocol slices: layout/codecs and atomic
litmus; cold open and participants; directory/slots and publication; leases,
remove, and reclamation; recovery and disposal; diagnostics and packaging.
Each slice requires native unit/state-machine tests, managed parity tests, and
deterministic cross-process schedules before the next dependent slice is
accepted. Final qualification runs Release builds on Windows x64 and Linux x64,
all nine ordered runtime pairs, mixed-runtime collision/pause/crash/recovery
stress, OS-lock tracing, clean package consumers, samples, and the complete
managed/native/Python suite. Missing required evidence fails qualification.

**Rationale**: Lock-free correctness depends on transition-level evidence and
cannot be established by happy-path byte exchange alone. Building from codecs
up localizes failures, while deterministic checkpoints make rare ABA, helping,
and disposal races repeatable. Full cross-runtime qualification is necessary
because compiler memory ordering and lifecycle adapters are part of the actual
contract.

**Alternatives considered**:

- Port the full native engine and add tests afterward. Rejected because a late
  mismatch would be difficult to localize across thousands of transition
  paths.
- Rely only on C# tests plus a small interoperability smoke test. Rejected
  because it does not qualify native atomics, native lifecycle behavior, or
  Python buffer lifetimes.
- Use stress tests without deterministic scheduling. Rejected because passing
  stress cannot prove coverage of the documented pause/reuse windows.

## No In-Place Migration

**Decision**: Provide no reader, converter, fallback, or automatic upgrade for
layout 1.2. Migration is an operational drain-close-recreate-republish process:
applications stop writers and readers, dispose all handles, remove or replace
the retired mapping, create SMS2 using current sizing, and republish values from
application-owned authoritative data. Current clients reject an `SMS1` header
before any legacy payload access. Historical specifications and source history
may remain for archaeology, but current code, packages, samples, compatibility
declarations, and guidance expose only SMS2.

**Rationale**: Mapped layouts, synchronization, ownership, and recovery models
change together. An in-place rewrite cannot safely preserve live zero-copy
leases or partially owned reservations, and the sole consumer has explicitly
accepted recreation and repopulation. Fail-closed rejection is simpler and
safer than carrying permanent migration code in every runtime.

**Alternatives considered**:

- Convert records in place after taking the old global lock. Rejected because
  other runtimes may retain borrowed pointers and the new region topology has a
  different required size.
- Open SMS1 read-only and lazily copy values. Rejected because that retains a
  legacy parser, ownership model, and recovery ambiguity in current packages.
- Automatically create a parallel SMS2 resource. Rejected because the same
  public store name must never identify divergent stores.
