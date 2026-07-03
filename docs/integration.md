# Optional Integration Guidance

SharedMemoryStore keeps the core package focused on the concrete `MemoryStore`
API. The package does not add hosting, dependency injection, logging,
health-check, options-framework, or telemetry dependencies.

Diagnostics integration boundaries are defined by
[diagnostics-integration-contract.md](../specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md).

## When To Add A Wrapper

Add an application-owned wrapper when you need to connect store lifecycle to an
application host, configuration system, health endpoint, or logging pipeline.
Keep the wrapper narrow and tied to the consuming application workflow.

Good wrapper responsibilities:

- create or open the store during application startup.
- validate `SharedMemoryStoreOptions` and surface configuration failures.
- expose a health shape from `TryGetDiagnostics`.
- release leases, abort reservations, run explicit recovery when owner policy
  allows it, and dispose the store during shutdown.
- hide low-level byte keys from application features that should not construct
  them directly.

Avoid broad interfaces that mirror every `MemoryStore` method. Low-level lease,
reservation, span, shared-memory layout, and recovery details are usually poor
mocking boundaries.

## Example Shape

```csharp
public sealed class StoreLifecycleAdapter : IDisposable
{
    private readonly SharedMemoryStoreOptions _options;
    private MemoryStore? _store;

    public StoreOpenStatus Start()
    {
        var validation = _options.Validate();
        return validation.IsValid
            ? MemoryStore.TryCreateOrOpen(_options, out _store)
            : validation.Status;
    }

    public StoreStatus PublishHealthValue(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        return _store?.TryPublish(key, value) ?? StoreStatus.StoreDisposed;
    }

    public StoreStatus Stop()
    {
        _store?.Dispose();
        _store = null;
        return StoreStatus.Success;
    }

    public void Dispose() => _store?.Dispose();
}
```

The [Hosted service integration sample](../samples/HostedServiceIntegration/README.md)
shows a runnable version with diagnostics and explicit recovery.

## Health Checks

Use `TryGetDiagnostics` for health because it returns a status. Health should be
caller-defined. For example:

- healthy when diagnostics succeeds and capacity pressure is below the
  application threshold.
- degraded when `StoreBusy` occurs under `NoWait`.
- unhealthy when `StoreDisposed`, `UnsupportedPlatform`, or repeated
  `CorruptStore` signals are observed.

The package does not decide health policy, log levels, metric names, retry
policy, or alert thresholds.

## Shutdown

On shutdown:

- stop accepting new producer and reader work.
- release active leases owned by this process.
- commit or abort active reservations owned by this process.
- run explicit recovery only when owner policy permits it.
- capture diagnostics if troubleshooting is needed.
- dispose the store handle.

## Boundaries

Do not present optional integration as required package behavior. The core
package remains BCL-only. Applications that use `Microsoft.Extensions.Hosting`,
dependency injection, options, logging, or health checks should keep those
dependencies in the application or in a separate optional adapter package.
