# DockerSharedMemory Sample

## Purpose and Audience

This sample validates same-host Docker containers that are configured to share
the resources required by SharedMemoryStore. It is for service owners and
maintainers proving container deployment settings before relying on
cross-container shared memory.

## Concepts Demonstrated

- One container creates a store and publishes a value.
- A second container opens the same store by name and reads the value.
- The verifier releases, removes, republishes, and reuses slots.
- Reservation, segmented publish, diagnostics, and recovery entry points run
  through the same public API as host processes.
- Recovery validation uses abrupt-exit lease and reservation owners while a
  keeper container holds the shared store open.
- Contention and disposal-race profiles verify documented lifecycle and wait
  outcomes inside Linux containers.
- Clean-consumer validation packs the package, installs it in a fresh container
  project, and runs first-use plus advanced workflows.
- An isolated profile proves default-isolated containers do not silently behave
  like a supported shared-store deployment.

## Prerequisites

- .NET `net10.0` SDK for local runs.
- Docker Engine or Docker Desktop with Compose support.
- Linux-based containers that can share IPC resources.
- Adequate shared-memory capacity for the configured store.

## Run

From the repository root:

```powershell
pwsh ./scripts/validate-docker-shared-memory.ps1
```

Run one validation profile:

```powershell
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Supported
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Advanced
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Recovery
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Contention
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile DisposalRace
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile CleanConsumer
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Isolated
```

Equivalent direct command:

```powershell
docker compose -f samples/DockerSharedMemory/docker-compose.yml up --build --abort-on-container-exit --exit-code-from verifier
docker compose -f samples/DockerSharedMemory/docker-compose.yml down --volumes
```

Run the local non-container workflow:

```powershell
dotnet run --project samples/DockerSharedMemory/DockerSharedMemory.csproj -c Release -- all
```

## Expected Output

The supported profile prints `docker shared memory validation passed` from the
verifier container. Advanced, recovery, contention, disposal-race, and
clean-consumer profiles print profile-specific `validation passed` lines. The
isolated profile prints `isolated open: NotFound` or another documented
environment outcome.

## Expected Non-Success Statuses

`UnsupportedPlatform`, `AccessDenied`, `MappingFailed`, or `NotFound` can be
valid outcomes when Docker is missing required IPC, owner-liveness, permissions,
or shared-memory capacity. Those outcomes indicate deployment configuration or
an unsupported profile, not a successful cross-container store.

## Cleanup

The validation script runs `docker compose down --volumes` for the selected
profile. Linux host resources are deterministic under the runtime shared-memory
directory and are cleaned when the last participating sample handle disposes.

## Related Documentation

- [Samples](../../docs/samples.md)
- [Portability](../../docs/portability.md)
- [Lifecycle](../../docs/lifecycle.md)
- [Diagnostics](../../docs/diagnostics.md)

## Scope Boundaries and Non-Goals

Docker support is same-host shared-memory participation. It is not cross-host
sharing, persistence, orchestration, service discovery, or network cache
behavior.
