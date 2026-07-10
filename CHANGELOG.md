# Changelog

All notable package and documentation changes are recorded in reverse
chronological order.

## 1.0.1 - 2026-07-09

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
