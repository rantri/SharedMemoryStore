# Hosted Service Integration Sample

## Purpose and Audience

This sample is for application owners who need service-style lifecycle and
health behavior around the core package. It shows an application-owned wrapper
that opens `MemoryStore`, validates options, publishes a health value, reads
diagnostics, runs explicit recovery hooks, and disposes the store during
shutdown. The wrapper uses the only current mapped protocol, SMS2.

## Concepts Demonstrated

- `SharedMemoryStoreOptions.Create` and option validation.
- `MemoryStore.TryCreateOrOpen` inside an application lifecycle wrapper.
- Health shape from `TryGetDiagnostics`.
- Explicit `TryRecoverLeases` and `TryRecoverReservations`.
- Shutdown cleanup through store disposal.
- Keeping hosting, dependency injection, logging, and health dependencies
  outside the core package.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Linux or Windows for ordinary runtime validation.
- Repository checkout from the repository root.

## Run

```powershell
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
```

## Expected Output

Expected success shape:

```text
start: Success
publish: Success
health: StoreHealth { IsHealthy = True, LastStatus = Success, FreeSlotCount = 3, BusyCount = 0 }
recover leases: Success
recover reservations: Success
stop: Success
```

The exact `FreeSlotCount` can change if the sample changes capacity or publish
count, but health should report `IsHealthy = True` and recovery calls should
return `Success` on the validated platform.

## Expected Non-Success Statuses

- `UnsupportedPlatform`: the current platform does not support the required
  named memory-mapped-file behavior or owner-liveness checks.
- `InvalidOptions`: application configuration failed validation.
- `StoreBusy` or `OperationCanceled`: startup coordination or a bounded local
  retry/help operation exhausted its policy.
- `StoreDisposed`: a caller used the wrapper after shutdown.

If startup fails, the program prints `start: <status>` and exits with a nonzero
code.

## Cleanup

The sample uses a unique store name for each run and calls `Stop`, which
disposes the store handle. It also demonstrates explicit recovery calls before
shutdown. It does not require manual file cleanup.

## Related Documentation

- [samples.md](../../docs/samples.md)
- [Integration](../../docs/integration.md)
- [Lifecycle](../../docs/lifecycle.md)
- [Diagnostics](../../docs/diagnostics.md)
- [Maintainers](../../docs/maintainers.md)

## Scope Boundaries and Non-Goals

This sample is not a framework adapter package. It intentionally does not add
hosting, dependency injection, logging, options-framework, or health-check
dependencies to the core package. Applications may adapt the wrapper shape to
their own host.
