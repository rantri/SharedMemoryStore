# Release and migration notes

This page describes the current single-protocol release line. Historical package
changes remain in [CHANGELOG.md](../CHANGELOG.md) and the archived Spec-Kit
feature directories.

## Current release matrix

| Artifact | Version | Compatibility identity |
|---|---:|---|
| NuGet `SharedMemoryStore` | `3.0.0` | SMS2 `2.0`, resource protocol `2` |
| CMake `SharedMemoryStore` | `1.0.0` | SMS2 `2.0`, C ABI/SOVERSION `2` |
| Python `shared-memory-store` | `1.0.0` | SMS2 `2.0`, requires C ABI `2.0` |

All distributions require feature mask `7`, accept optional mask `0`, and are
qualified on Windows x64 and Linux x64.

## Managed 3.0 breaking boundary

NuGet `3.0.0` commits the ordinary public API to one SMS2 engine. There is no
runtime layout selector, compatibility creator, fallback reader, alternate
sizing rule, or old mapped-record surface.

Use:

```csharp
SharedMemoryStoreOptions.Create(
    name,
    slotCount,
    maxValueBytes,
    maxDescriptorBytes,
    maxKeyBytes,
    leaseRecordCount,
    participantRecordCount,
    openMode,
    enableLeaseRecovery);
```

`CalculateRequiredBytes(...)` uses the same capacities and always calculates
SMS2. Every successful `MemoryStore` exposes exact protocol identity
`(2, 0, 2, 7, 0)`.

A noncurrent, unknown, truncated, or malformed mapping returns
`IncompatibleLayout` before key, descriptor, payload, slot, lease, or
participant projection. Existing bytes are never reinterpreted or converted in
place.

## Native 1.0 and C ABI 2

The native `1.0.0` distribution implements SMS2 directly and installs:

- a fixed-width, exception-contained C ABI `2.0`;
- a move-only C++20 RAII API;
- participant-aware sizing and open options;
- contiguous and segmented publication;
- zero-copy reservations and leases;
- remove/reuse and explicit recovery;
- cancellation and bounded waits;
- protocol/layout queries and expanded diagnostics; and
- Windows/Linux platform adapters using resource protocol `2`.

ABI structures begin with size/version fields and use fixed-width lengths.
Opaque store, lease, reservation, and cancellation handles remain process-local.
No C++ standard-library object or mapped record pointer crosses the ABI.

## Python 1.0

The Python `1.0.0` wheel provides immutable options/value types and
context-managed stores, leases, reservations, and cancellation sources over the
packaged ABI `2.0` library.

The wheel loader accepts only the adjacent platform library and rejects a
missing or wrong ABI before exposing a store. Zero-copy `memoryview` objects end
with their exact owning token/store lifetime.

## Protocol changes in this line

SMS2 provides:

- a 512-byte self-describing header;
- participant records with PID/start/namespace identity;
- a fixed two-choice primary directory with versioned spill summaries;
- bounded overflow directory cells;
- generation-tagged slots and lease records;
- explicit publication intent and reservation progress;
- helpable directory, publication, removal, reclamation, and recovery
  transitions;
- exact stale-token rejection; and
- a shared corruption latch only after impossible-state revalidation.

Hot operations require no process-owned or globally exclusive store-wide lock.
Cold physical create/open/close and final owner cleanup remain bounded platform
lifecycle operations.

## Diagnostics changes

All runtimes expose:

- five-field protocol identity;
- capacity and slot lifecycle state;
- lease and reservation state;
- participant occupancy and exhaustion;
- primary/spill/overflow directory health;
- retries, helping, contention exhaustion;
- invalid and stale token counts;
- recovery and owner classifications; and
- terminal/failure status evidence.

Shared structural facts are comparable across runtimes. Retry/help/failure
counters are local to the runtime or handle that performed the work. Obsolete
tombstone-pressure and synchronous-compaction fields are not part of the current
diagnostics contract.

## Required deployment migration

The library cannot use a noncurrent mapping as its own migration source. Values
must come from an application-owned authoritative source.

Same-name replacement:

1. stop all publishers;
2. prevent new readers and wait for application work to quiesce;
3. release every lease and commit or abort every reservation;
4. close every store handle in every process;
5. verify that old physical ownership is gone, then remove or archive the old
   physical resources;
6. create a new SMS2 store with deliberate slot, byte, lease, and participant
   capacities;
7. republish authoritative values; and
8. start only current clients.

Side-by-side replacement:

1. create the SMS2 store under a new public name;
2. populate it from authoritative data;
3. atomically redirect application discovery/configuration;
4. drain and close the old deployment; and
5. remove old resources after no client can reach them.

Do not copy mapped files, patch headers, reinterpret record bytes, or run old
and current writers against the same public name.

## Rollback planning

Application rollback and mapped-store rollback are separate. A package rollback
is safe only when the selected application still implements SMS2 and the same
resource protocol/features. Otherwise rollback also requires a complete
drain-close-replace-republish cycle using an application-owned data source.

Keep the authoritative source until the deployment is fully qualified. The
shared store is IPC state, not a durable backup.

## Qualification evidence

Before publishing or deploying this release line, require:

- warnings-as-errors builds for managed and native code;
- complete managed unit, contract, integration, and linearizability suites;
- native CTest, installed CMake consumer, and exported-symbol checks;
- source and installed-wheel Python suites, wrong/missing ABI rejection, and
  unrelated-directory sample execution;
- all nine ordered .NET/C++/Python pairs;
- pause/crash/help/reuse and exact recovery schedules;
- raw visibility and held-cold-lock hot-progress evidence;
- Windows and Linux owner cleanup and PID namespace tests;
- package metadata/documentation consistency; and
- clean NuGet, CMake, and wheel consumers.

Repository commands:

```powershell
dotnet build SharedMemoryStore.slnx -c Release
dotnet test SharedMemoryStore.slnx -c Release
pwsh ./scripts/validate-package-consumption.ps1 -Configuration Release
pwsh ./scripts/validate-native.ps1 -Configuration Release
pwsh ./scripts/validate-python.ps1 -Configuration Release
```

## Release artifacts

Managed publication produces `SharedMemoryStore.3.0.0.nupkg` and a matching
symbol package. Native installation exports headers, library, and CMake config
files. Python publication produces platform-specific wheels and a complete
source distribution capable of rebuilding the bundled native library.

Signing, registry publication, trusted publishing, and draft release policy are
owned independently by each ecosystem. A successful repository build does not
by itself publish any artifact.

Before publication, verify the managed `PackageReleaseNotes`, this page,
[CHANGELOG.md](../CHANGELOG.md), [SUPPORT.md](../SUPPORT.md), and
[SECURITY.md](../SECURITY.md) describe the same supported release boundary.

## Compatibility declaration

[`protocol/compatibility.json`](../protocol/compatibility.json) is the
machine-readable current matrix. Release metadata, package versions, C ABI,
protocol manifest, samples, and this page must agree with it.

## Related guides

- [Packaging](packaging.md)
- [Getting started](getting-started.md)
- [Portability](portability.md)
- [Protocol overview](../protocol/README.md)
- [Changelog](../CHANGELOG.md)
