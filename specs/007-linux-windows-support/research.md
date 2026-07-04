# Research: Linux, Windows, and Docker Support

## Decision 1: Use Internal Platform Resource Adapters

**Decision**: Introduce internal adapters for store region creation/opening,
shared synchronization, owner-liveness classification, resource naming, and
cleanup. `MemoryStore` should depend on the adapter surface rather than directly
creating a named mapping and named mutex.

**Rationale**: The current code assumes Windows named memory-mapped files and a
Windows-style mutex name. Linux and Docker support require platform-specific
resource resolution while preserving one public store API. Isolating those
choices behind adapters keeps the core store layout, slot, lease, reservation,
diagnostics, and lifecycle rules portable.

**Alternatives considered**:
- Keep platform checks directly inside `MemoryMappedStoreRegion`: rejected
  because synchronization, owner liveness, cleanup, and Docker validation also
  need platform-specific behavior.
- Fork the package by platform: rejected because it would fragment public
  contracts and package consumption.

## Decision 2: Preserve Windows Named Resource Behavior

**Decision**: Keep Windows named memory-mapped resources and named
synchronization as the Windows adapter baseline unless tests reveal a contract
gap.

**Rationale**: Windows x64 is the current validated target, and the existing
public behavior should not regress. Compatibility risk is lowest when Windows
keeps the resource model that existing tests and docs already exercise.

**Alternatives considered**:
- Move Windows to the same file-backed resource model as Linux: rejected for
  this feature because it would change a working platform unnecessarily and
  increase compatibility risk.

## Decision 3: Implement Linux with Shared Runtime Memory Resources

**Decision**: Implement Linux store regions with deterministic resources in a
shared runtime memory location, such as `/dev/shm`, and map them through BCL
memory-mapped file APIs that work with file-backed mappings.

**Rationale**: The current named mapping calls return unsupported on non-Windows.
File-backed memory mappings provide a BCL-only path for Linux while keeping the
payload memory shared between same-host processes. Using a runtime memory
location preserves the package's shared-memory intent better than an ordinary
persistent data directory.

**Alternatives considered**:
- Use native POSIX shared-memory calls directly: viable but rejected for the
  first plan because it introduces platform interop complexity, cleanup edge
  cases, and more review surface. It remains a fallback if BCL-backed resources
  cannot meet the contract.
- Use ordinary temp files by default: rejected because it weakens the
  shared-memory and non-persistence expectations and makes Docker resource
  limits harder to reason about.

**References**:
- Microsoft Learn: [Memory-mapped files](https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files)
- Microsoft Learn Q&A: [Named maps are not supported on Linux; use file-backed mappings](https://learn.microsoft.com/en-us/answers/questions/299628/creating-memorymappedfile-fails-with-platformnotsu)

## Decision 4: Keep Synchronization Semantics Equivalent Through an Adapter

**Decision**: Provide a shared synchronization adapter that preserves mutual
exclusion, bounded waits, cancellation, abandoned-owner handling, and
store-disposed outcomes across Windows, Linux, and supported Docker deployments.
The adapter may use named mutexes where they meet the contract and must expose
the same wait-result vocabulary to `MemoryStore`.

**Rationale**: Existing public wait behavior is part of the production API. The
store must not let platform-specific synchronization details leak as raw runtime
exceptions or timing differences.

**Alternatives considered**:
- Use file locks everywhere: rejected as the default plan because bounded waits,
  cancellation, abandoned-owner semantics, and diagnostics would need additional
  polling and failure mapping.
- Keep `Mutex` usage inline in `MemoryStore`: rejected because Linux and Docker
  validation need resource naming and capability checks separate from store
  logic.

**References**:
- Microsoft Learn: [Mutex class](https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex?view=net-10.0)
- Microsoft Learn: [Mutexes](https://learn.microsoft.com/en-us/dotnet/standard/threading/mutexes)

## Decision 5: Treat Docker as a Same-Host Shared-Resource Profile

**Decision**: Support Docker containers that run on the same host and are
configured to share the required IPC, owner-liveness, permission, and memory
capacity capabilities. Document and validate a Docker Compose profile using
shareable IPC and a joined service namespace or equivalent Docker CLI flags.

**Rationale**: Docker isolates IPC and PID namespaces by default. Cross-container
shared memory is therefore a deployment capability, not a network feature. The
package should validate the supported profile and report or document failures
for isolated profiles rather than claiming distributed behavior.

**Alternatives considered**:
- Support default isolated containers transparently: rejected because two
  isolated containers can create same-named but different resources, which would
  silently violate the shared-store contract.
- Require host namespaces only: rejected as too broad for ordinary containerized
  services when Docker supports sharing namespaces between selected containers.
- Implement cross-host sharing over sockets or files: rejected as out of scope
  and contrary to the package's same-host shared-memory contract.

**References**:
- Docker Docs: [`docker run --ipc`](https://docs.docker.com/reference/cli/docker/container/run/#ipc-settings---ipc)
- Docker Docs: [`docker run --pid`](https://docs.docker.com/reference/cli/docker/container/run/#pid-settings---pid)
- Docker Docs: [Compose service `ipc` and namespace references](https://docs.docker.com/reference/compose-file/services/#ipc)
- Docker Docs: [Compose `shm_size`](https://docs.docker.com/reference/compose-file/services/#shm_size)

## Decision 6: Preserve Owner Safety Before Aggressive Recovery

**Decision**: Owner-liveness classification must remain conservative. If a
platform or container deployment cannot evaluate another owner safely, recovery
must skip the record and report an unsupported or unsafe owner category rather
than reclaiming storage.

**Rationale**: Recovery mistakes can invalidate active readers. Docker PID
namespace isolation can hide or remap process identifiers, so the supported
container profile must include owner-liveness capability, and unsupported
profiles must fail safely.

**Alternatives considered**:
- Treat unknown owner IDs as stale: rejected because PID namespaces and reuse can
  make that unsafe.
- Add hidden heartbeat threads: rejected by the constitution's prohibition on
  hidden background work.

## Decision 7: Make Validation Cross-Platform and Container-Aware

**Decision**: Add validation scripts and tests that run the same package
workflows on Linux, Windows, and supported Docker containers. Scripts should use
portable path handling and `pwsh` on Linux.

**Rationale**: The current validation scripts and tests include Windows-first
assumptions. Without explicit Linux and Docker validation, future changes can
regress the newly supported environments.

**Alternatives considered**:
- Rely on documentation review only: rejected because this is behavior-changing
  platform support.
- Keep Docker validation manual-only: rejected because the feature explicitly
  promises Docker container sharing as supported behavior.
