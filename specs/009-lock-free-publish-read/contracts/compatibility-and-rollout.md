# Compatibility and Rollout Contract

## Independent versions

| Surface | Existing | New |
|---|---|---|
| NuGet package | 1.0.2 | target 2.0.0 |
| Mapped layout | 1.2 | 2.0 |
| Resource protocol | 1 | 2 |
| C ABI | 1.0 | unchanged; v2 unsupported |
| C++ package | 0.1.0 | unchanged; v2 unsupported |
| Python package | 0.1.0 | unchanged; v2 unsupported |

Package, layout, resource, and C ABI versions never imply one another. The
compatibility manifest lists each distribution's create/read set explicitly.

Layout 2.0 is not released yet, so the approved generation-tagged directory
operation/location encoding replaces the earlier draft rather than creating a
2.1 migration. The published 2.0 contract includes the `SlotCount <= 1,048,575`
limit, exact generation-tagged encodings, the versioned spill summary, and the
`PublicationIntent` field at value-slot byte offset 52, and PID-namespace
identity/mode at header offsets 264/272 plus participant offset 32. Its required-
features mask is exactly 7: bit 0 is `versioned_empty_spill_summary`, bit 1 is
`publication_intent`, and bit 2 is `pid_namespace_identity`. Any prototype built
with required-features zero, bit 0 alone, mask 3, or another superseded draft is incompatible in both directions and must
recreate its mapping; no draft-format auto-detection or in-place conversion is
supported.

## Profile matrix

| Requested client/profile | No mapping | Existing v1.2 | Existing v2.0 |
|---|---|---|---|
| C# 2.0 / Legacy | Create/open v1.2 per `OpenMode` | Success if dimensions match | `IncompatibleLayout` |
| C# 2.0 / LockFree | Create/open v2.0 with required-features mask 7 per `OpenMode` | `IncompatibleLayout` | Success only if dimensions and the exact required-feature set match |
| Already released C# 1.x | Create/open v1.2 | Existing behavior | Fail closed before layout data access: ordinarily `IncompatibleLayout`; `CreateNew` is `AlreadyExists`; oversized Windows views may surface existing `AccessDenied` |
| C++ 0.1 / Python 0.1 | Create/open v1.2 | Existing behavior | Deterministic incompatible result; never payload access |

`CreateNew` still reports `AlreadyExists` when either profile already owns the
same public name. `OpenExisting` never creates a parallel mapping. Dimension or
feature mismatch within the requested major returns `IncompatibleLayout` or the
existing precise capacity/options result before unsafe access.

An existing all-zero/unpublished header is not treated as an empty store.
Physical creation, rather than `OpenMode`, profile, dimensions, or zero magic,
is the sole initialization authority. Every profile pairing preserves the
existing bytes and reports `AlreadyExists` for `CreateNew`, `StoreBusy` for
`CreateOrOpen`, or `IncompatibleLayout` for `OpenExisting`.

New C# 2.0 legacy-profile code uses a header-sized probe and returns
`IncompatibleLayout` for `SMS2` independently of requested dimensions. Immutable
released binaries have a safety, not exact-status, guarantee: they never return
success or access layout data. Their platform/view-size-dependent existing
non-success result is preserved and tested with the packed released version.

## Physical resource identity

For a public store name, v2 derives the same Windows named mapping and Linux
`.region`, `.owners`, and `.lifecycle` paths as resource-naming v1. This is
intentional fail-closed discovery.

Resource protocol 2 changes participation:

- one cold transaction covers physical discovery/creation, header
  initialization or validation, owner publication, and participant registration
  under the caller's original wait/cancellation budget;
- Windows acquires the existing named synchronization object before creating,
  opening, or mapping the region and retains it through the complete cold
  transaction;
- Linux acquires `.lifecycle`, reconciles markers and deletes only proven stale
  resources, then opens/acquires `.lock` before mapping, owner-anchor locking,
  owner-line commit, header work, and participant registration; release is
  `.lock` followed by `.lifecycle`;
- failed-open mapped-resource and owner cleanup occurs only after those gates
  are released, because owner cleanup may re-enter `.lifecycle`; a contender
  rejected before mapping publishes no owner line or release marker;
- only the attempt whose physical operation created a new region may initialize
  a zero header; an opened-existing zero header has the fixed outcomes above;
- Linux owner registration/final cleanup remains under `.lifecycle`;
- every v2 Linux handle continues writing a resource-naming-v1-compatible live
  `.owners` line in addition to its mapped participant record, preventing an old
  opener from misclassifying/deleting a live v2 region as stale;
- every current managed Linux handle holds a private mode-`0600`
  `.owners.anchor.<owner-guid>` open-description `flock` for its mapped lifetime;
  the canonical path is the exact per-store `.owners` path plus `.anchor.` and
  exactly 32 lowercase hexadecimal GUID digits;
  under `.lifecycle`, locked is authoritative live evidence across PID namespace
  views, unlocked is stale, missing falls back to PID/start-token for C++,
  Python, and older managed owners, and ambiguous/access/symlink results are
  retained conservatively;
- the anchor is a managed cold-lifecycle extension: it does not change the
  three-field owner line, participant layout, or interoperable `.lock`/`.lifecycle`
  record locks, and it is unreachable from every v2 data operation;
- after unmapping, close/failed-open cleanup waits no more than 250 milliseconds
  for `.lifecycle`; on contention or pre-commit failure it atomically publishes
  one private exact-owner release marker, which a later v2 lifecycle operation
  reconciles by raw exact-line removal and atomic owner-sidecar rewrite before
  marker deletion; this fallback may leave the compatible owner line, anchor
  pathname, or both until that later reconciliation;
- orderly close unlocks/deletes its anchor only after exact sidecar absence or
  successful finalized-marker publication; if neither succeeds it retains the
  lock, while process death releases it automatically and later lifecycle
  cleanup removes a stale artifact only when its conservative independent probe
  proves deletion safe;
- after each replacement `.owners` sidecar commit under `.lifecycle`, current C#
  cleanup derives referenced tokens from canonical committed owner lines and
  sweeps only canonical anchors for that store; it opens each unreferenced
  candidate through a separate `O_NOFOLLOW` descriptor, verifies a regular file,
  and deletes it only while a nonblocking exclusive `flock` is held;
- referenced or locked anchors, ambiguous probes, non-regular files, symbolic
  links, directories, malformed names, and enumeration/open/stat/lock/delete
  access errors are retained; final-owner cleanup never broad-glob deletes them;
- v1 C#, C++, and Python clients ignore these additive marker files and remain
  conservative because the compatible owner line stays present until v2
  reconciliation or ordinary process-liveness cleanup;
- C# 2.0 applies the bounded owner-cleanup extension to both mapped profiles
  because their Linux sidecars are shared, without changing layout-v1.2 bytes or
  its ordinary per-operation locking contract;
- v2 releases/disposes the ordinary synchronization handle after open validation
  or retains it only as a cold-path object that is unreachable from data paths;
- v2 steady-state operations never enter the Windows named mutex/semaphore or
  Linux `.lock` file;
- v1.2 participants continue using resource protocol 1 for every data operation.

The implementation must open/query an existing mapping sufficiently to read its
identity/header even when the caller-calculated length differs. A requested-size
view failure must not mask a readable incompatible major as generic
`MappingFailed`.

## Public API compatibility

- Existing method and enum signatures remain present.
- Existing status numeric assignments 0-22 remain unchanged.
- `SharedMemoryStoreOptions.Create(...)` and the five-dimension
  `CalculateRequiredBytes(...)` remain legacy helpers.
- New profile members/helpers are additive.
- V2 participant capacity is explicit (`ParticipantRecordCount`, default 64) and
  exhaustion appends `StoreOpenStatus.ParticipantTableFull` without renumbering
  previous values.
- Default/manual zero enum value remains legacy.
- Layout-v1.2 tests continue exercising the original synchronization and mapped
  bytes without reinterpretation.
- Public lease/reservation structs remain process-local values. Their private
  representation may grow to carry v2 incarnations; because that changes runtime
  size/AOT assumptions, the package uses a conservative major version and tests
  supported binary/source consumption explicitly.

## Native/Python rejection

C++ and Python in this feature do not implement layout-v2 atomics or recovery.
Their open sequence must:

1. discover the same physical region;
2. read enough validated header bytes to identify `SMS2`/major 2;
3. return the existing incompatible-layout public/ABI result;
4. perform no directory, slot, lease, descriptor, or payload read/write;
5. leave the mapping usable by C# v2 participants.

Executable ordered-pair tests cover a C#-created v2 mapping opened by C++ and
Python on Windows/Linux. C ABI 1.0 remains byte-for-byte unchanged. Future v2
native support requires an explicitly versioned ABI addition and the exact
layout/memory-order/recovery contract.

## Upgrade paths

There is no in-place conversion or dual writer mode.

### Same-name cutover

1. Stop key delivery/publication and drain application work.
2. Release leases/abort reservations and close every v1.2 participant.
3. Application owns any data migration: copy/republish required values elsewhere
   before final close if needed.
4. Let normal final-owner cleanup remove the v1.2 mapping, or perform documented
   operator cleanup after verifying no live owners.
5. Deploy C# 2.0 callers with explicit `StoreProfile.LockFree`/
   `CreateLockFree` and create v2 under the same public name.
6. Run health, protocol identity, collision, recovery, and short performance
   checks before restoring traffic.

### Side-by-side cutover

Use a different **public store name**, not a hidden profile suffix. Publish data
to the new v2 store under application control, switch broker/application keys,
then drain the old v1.2 name. Each name independently fails closed.

## Rollback

V2 bytes cannot be reinterpreted by v1.2. To roll back:

1. stop new v2 operations and drain/close v2 handles;
2. preserve/republish required application data through supported APIs;
3. remove the v2 mapping only after owner/liveness verification;
4. deploy legacy-profile participants and recreate v1.2 under the target name;
5. republish data and restore broker/application key delivery.

An older client accidentally pointed at a still-live v2 name must fail
incompatible; deleting that mapping to make the old client start is never an
automatic library action.

## Package and protocol artifacts

Release work updates:

- `SharedMemoryStore.csproj` package version/release notes/XML docs;
- `protocol/layout-v2.0.md` with exact offsets/fixtures;
- `protocol/resource-naming-v2.md`;
- `protocol/compatibility.json` with per-layout resource protocol support and
  required-features mask 7;
- README migration/profile/progress/trust-boundary guidance;
- package-consumption tests for old legacy consumer, new legacy consumer, and
  new lock-free consumer;
- C++/Python rejection tests and unchanged v1.2 interop matrix.

## Release qualification

A rollout artifact is not valid until Release `dotnet test`, `dotnet pack`,
layout fixtures, v1.2 regressions, v2 contracts, mixed-profile rejection,
package consumption, and the platform-specific lock-free qualification gates
pass. Performance results record hardware/OS/runtime, profile, mapping shape,
CPU affinity, warm-up, duration, trials, percentiles, throughput, fairness, and
status counts; they are not generalized to unmeasured environments.
