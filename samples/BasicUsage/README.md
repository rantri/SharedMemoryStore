# Basic Usage Sample

This sample demonstrates the primary consumer workflow described in
[Getting started](../../docs/getting-started.md) and [Usage](../../docs/usage.md).
It creates or opens a named store, publishes a value, acquires a lease, reads
the value bytes, releases the lease, removes the value, publishes again to show
slot reuse, and prints a diagnostic field.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Windows x64 for the current named memory-mapped-file validation target.

## Run

```powershell
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
```

Expected output:

```text
Success
Success
value bytes: 04-05-06-07
Success
Success
Success
free slots: 1
```

If the platform does not support the required named memory-mapped-file behavior,
the first line may report `open failed: UnsupportedPlatform`.

## Cleanup

The sample uses a unique store name for each run and disposes the store handle
before exiting. It does not require manual file cleanup.
