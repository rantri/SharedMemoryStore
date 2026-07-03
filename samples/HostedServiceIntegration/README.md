# Hosted Service Integration Sample

This sample keeps service-style lifecycle and health behavior outside the core
package. It opens `MemoryStore`, validates options, publishes a health value,
requests diagnostics, runs explicit cleanup/recovery hooks, and disposes the
store during shutdown.

The sample intentionally does not add hosting dependencies to the core package.
Applications that use `Microsoft.Extensions.Hosting`, health checks, logging, or
dependency injection should keep those dependencies in their application or in a
separate optional adapter package.

Run:

```powershell
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
```

Expected output includes `start: Success`, a healthy diagnostics result,
successful recovery calls, and `stop: Success`.

Validation on 2026-07-03:

```powershell
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
```

Result: start, publish, health, lease recovery, reservation recovery, and stop
all returned `Success`.
