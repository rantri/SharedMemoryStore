# Changelog

All notable package and documentation changes are recorded in reverse
chronological order.

## Unreleased

## 3.0.0 - 2026-07-16

### Breaking changes

- NuGet `SharedMemoryStore` now creates and reads only SMS2 layout `2.0`,
  resource protocol `2`, required-feature mask `7`, and optional-feature mask
  `0`.
- Removed the alternate layout selector, compatibility creator, fallback
  engine, and retired mapped-record surface. Ordinary
  `SharedMemoryStoreOptions.Create(...)` and `CalculateRequiredBytes(...)` are
  the only managed sizing and creation helpers.
- A noncurrent, unknown, or malformed mapping is rejected with
  `IncompatibleLayout` before key, descriptor, payload, slot, lease, or
  participant projection. There is no in-place conversion.

### Added

- Completed independent native `SharedMemoryStore` `1.0.0` and Python
  `shared-memory-store` `1.0.0` distributions on the same SMS2 protocol.
- Advanced the fixed-width native boundary to C ABI `2.0`, including
  participant-aware layout sizing, protocol identity, cancellation, expanded
  diagnostics, lifecycle operations, and ownership-safe opaque tokens.
- Added complete C++, Python, and nine ordered .NET/C++/Python interoperability
  coverage for publication, reservations, leases, removal/reuse, recovery,
  participant capacity, diagnostics, corruption rejection, and held-cold-lock
  progress.

### Changed

- Hot publish, segmented publish, reserve, commit, abort, acquire, release,
  remove, reclaim, recovery help, and diagnostics use mapped lock-free atomics
  and bounded helping; OS synchronization is limited to cold create/open/close
  coordination.
- Managed diagnostics now expose the canonical five-field protocol identity,
  SMS2 participant and directory state, and local retry/help/token/recovery
  telemetry without obsolete tombstone or compaction fields.
- Updated NuGet metadata and XML documentation for version `3.0.0`, CMake
  metadata for version `1.0.0`/ABI `2.0`, and Python metadata for version
  `1.0.0`/required ABI `2.0`.

### Migration

- Stop writers and readers, drain leases and reservations, close every handle,
  remove or replace the noncurrent physical store, create a fresh SMS2 store,
  and republish values from an application-owned authoritative source.
- A side-by-side cutover must use a distinct public store name. Current clients
  do not read an old mapping as a migration source.

## 2.0.0 - 2026-07-13

### Added

- Added an explicitly selected C# lock-free key-value profile using mapped
  layout `2.0` and resource protocol `2`, with direct reservation publication,
  shared zero-copy leases, generation-fenced removal/reuse, participant
  incarnations, explicit recovery, and bounded diagnostics.
- Added `StoreProfile`, `CreateLockFree`, profile-aware sizing,
  `ParticipantRecordCount`, immutable `StoreProtocolInfo`, and the appended
  `ParticipantTableFull` open status.

### Changed

- NuGet `SharedMemoryStore` advances to `2.0.0`. Lock-free wait options bound
  local retry, revalidation, helping, and backoff; they do not acquire a named
  operation lock or wait for keys/capacity to appear.
- Documented reservation tokens as exclusive single-producer lifetimes and
  clarified logical removal, `RemovePending`, borrowed-view, recovery, and
  local-handle disposal semantics.

### Compatibility

- The legacy profile remains the default and preserves layout `1.2`, resource
  protocol `1`, public workflow signatures, and existing status numbers.
- Layout `1.2` and `2.0` are never reinterpreted in place or used by mixed
  synchronization participants. Same-name upgrade and rollback require draining
  handles, recreating the mapping, and republishing application-owned values.
- C++ and Python `0.1.0` remain layout-1.2-only and reject layout `2.0`; their
  independently versioned packages and C ABI `1.0` are unchanged.

## 1.0.2 - 2026-07-10

### Added

- Added an independently versioned CMake `SharedMemoryStore` `0.1.0`
  distribution with a C++20 protocol core, Windows and Linux adapters,
  fixed-width C ABI `1.0`, move-only C++ RAII wrappers, install/export rules,
  native tests, a clean-consumer project, and a basic sample.
- Added the independently versioned Python `shared-memory-store` `0.1.0`
  distribution sources for Python 3.10 or newer. Its standard-library `ctypes`
  API loads the packaged native library and exposes context-managed stores,
  leases, reservations, recovery, bounded waits, and diagnostics.
- Added canonical language-neutral specifications and conformance fixtures for
  mapped layout `1.2` and resource naming `1`, plus a complete ordered 3x3
  .NET/C++/Python interoperability harness.

### Fixed

- Corrected managed Linux opening of an existing store so the actual backing
  file is mapped before layout validation; mismatched capacities now report
  `IncompatibleLayout` instead of a mapping failure.
- Made the managed implementation reject non-64-bit or non-little-endian
  processes with `UnsupportedPlatform` before opening platform resources.

### Packaging

- Updated the release workflow to Node 24-based immutable action pins while
  retaining trusted NuGet publishing and draft-first GitHub release behavior.

### Security

- Restricted Python native loading to the library packaged beside its modules;
  it does not search the current directory, `PATH`, or system library paths.
- Preserved the trusted same-host participant boundary across all three
  implementations; no implementation claims protection from a malicious writer
  that already has legitimate access to the shared resources.

### Validation

- Validated native configure, build, CTest, install, clean CMake consumption,
  Python source and installed-wheel behavior, and ordered .NET/C++/Python
  interoperability on Windows x64 and Linux x64.
- Validated mixed lifecycle, bounded-contention, crash-recovery, diagnostics,
  Linux ownership cleanup, protocol fixtures, package consumption, and existing
  managed regression scenarios.

### Compatibility

- NuGet `SharedMemoryStore` advances to `1.0.2` while preserving the managed
  public API, status values, .NET BCL-only runtime dependency surface, mapped
  layout `1.2`, and resource naming `1`.
- CMake `SharedMemoryStore` and Python `shared-memory-store` remain independently
  versioned `0.1.0` alpha sibling distributions. They are not included in the
  NuGet package, and this release does not publish them to PyPI or a native
  package registry.
- All three runtimes interoperate through mapped layout `1.2` and resource
  naming `1`; the native and Python distributions use C ABI `1.0`.

## 1.0.1 - 2026-07-10

### Packaging

- Added Linux and Windows GitHub Actions validation and a manually triggered,
  trusted-publishing release workflow for NuGet.org and GitHub Releases.
- Added portable `.snupkg` symbols to improve package debugging without growing
  the primary package.

### Fixed

- Enforced one caller-selected wait budget across same-handle synchronization,
  Linux lifecycle locking, and shared store locking.
- Replaced destructive key-index rebuilds with crash-safe backward-shift
  compaction and duplicate-tolerant cleanup.
- Made Linux owner metadata replacement atomic and resistant to PID reuse, and
  prevented failed opens from leaving live-looking owner records.
- Rejected layout dimensions whose index, alignment, or section calculations
  cannot be represented safely.
- Moved stores that detect usage-count underflow into shared safe error mode.
- Prevented Windows mapping-handle leaks when view creation fails and removed
  process-local Linux lock registry growth after handles are disposed.
- Matched Windows `Global\` mapping names with global synchronization so
  cross-session participants cannot mutate one mapping under different mutexes.
- Made segmented publish use one synchronized operation so bounded contention
  cannot strand an internal reservation.

### Security

- Linux shared-memory directories now use owner-only `0700` permissions and
  region, synchronization, owner, and lifecycle files use owner-only `0600`
  permissions.

### Compatibility

- Public API names, status values, and shared-memory layout remain compatible
  with the `1.0.0` production contract.

## 1.0.0 - 2026-07-03

### Added

- Added Linux and Windows as first-class runtime and development targets.
- Added same-host Docker container sharing support for Linux containers
  configured with shared IPC, owner-liveness, compatible permissions, and
  sufficient shared-memory capacity.
- Added platform resource adapters, deterministic Linux resource naming,
  portable validation scripts, and the Docker shared-memory sample.

### Documentation

- Reorganized the documentation and samples journey from first use through
  concepts, feature guides, runnable samples, production review, architecture,
  maintainer guidance, packaging, and release validation.
- Added documentation and samples validation expectations for required guide
  inventory, sample README contracts, public API/status references, package
  metadata, release notes, and clean package consumption.
- Replaced older platform wording with Linux, Windows, and supported same-host
  Docker guidance.

### Changed

- Renamed the primary concrete store type from `SharedMemoryStore` to
  `MemoryStore` while keeping the package ID and root namespace
  `SharedMemoryStore`.
- Added `StoreWaitOptions` and wait-policy overloads for open/create, publish,
  reserve, segmented publish, acquire, remove, recovery, diagnostics, lease
  release, and reservation token operations.
- Removed public retained writable `ValueReservation.GetMemory`; reservation
  payload writes now use immediate `GetSpan` access followed by `Advance`.
- Added `InvalidKey`, `StoreBusy`, and `OperationCanceled` operation statuses
  plus open/create equivalents for busy and canceled synchronization waits.
- Added `SharedMemoryStoreOptions.Create` and public option validation details.
- Pruned diagnostics failure-count convenience properties in favor of
  `DiagnosticsSnapshot.GetFailureCount(StoreStatus)`.

### Compatibility

- This is a breaking production API contract step from the prerelease
  `0.2.0` surface.
- Runtime dependencies remain .NET BCL only; the core package does not take
  Microsoft.Extensions hosting dependencies.
- The public API and shared-memory layout major version remain compatible for
  the Linux, Windows, and same-host Docker support update.

## 0.2.0 - 2026-07-02

### Added

- Zero-copy reservation ingest with `TryReserve`, `ValueReservation`,
  exact-byte `Advance`, atomic `Commit`, `Abort`, and disposal cleanup.
- Segmented `ReadOnlySequence<byte>` publication through `TryPublishSegments`
  without a temporary full-payload array.
- Explicit reservation recovery, appended reservation statuses, reservation
  diagnostics, contract tests, integration tests, sample, and benchmarks.
- Owner-scoped lease recovery reporting for recovered, active, unsupported, and
  failed records.
- Deterministic disposal-race outcomes for public store methods and token
  methods.
- Rollover-safe slot lifecycle identity using generation plus reuse epoch.
- Key-index tombstone health diagnostics and synchronous internal compaction
  under mutation pressure.

### Compatibility

- Package remains `SharedMemoryStore` targeting `net10.0`.
- Runtime dependencies remain .NET BCL only.
- Existing publish, acquire, lease, remove, diagnostics, and package workflows
  remain compatible.
- Shared-memory layout major version remains `1`; minor version is now `2`.

## 0.1.0 - 2026-06-27

### Added

- Initial public package contract for create/open, publish, acquire, release,
  remove, diagnostics, stale lease recovery, and slot reuse.
- Public documentation baseline for evaluators, package consumers, production
  reviewers, future implementers, contributors, and maintainers.
- Root MIT license, support policy, security policy, contribution guide, code of
  conduct, issue templates, pull request template, and release guide.
- Documentation validation for required files, placeholders, internal links,
  package metadata alignment, contract links, contributor paths, and release
  readiness.

### Compatibility

- Package targets `net10.0`.
- The initial validation baseline focused on Windows x64 named memory-mapped
  files.
- C++ and Python are future portability audiences; current bindings are not
  included.
- This entry documents a prerelease `0.1.0` baseline and does not imply stable
  `1.0.0` API compatibility.

### Known Limitations

- Runtime support initially focused on Windows.
- The core store treats values and descriptors as opaque bytes and does not
  parse application-specific frame layouts.
- Public repository URL metadata is left to release preparation because no
  repository remote is configured in this checkout.
