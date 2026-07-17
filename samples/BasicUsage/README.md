# Basic Usage Sample

## Purpose and Audience

This is the first sample for new package consumers. It demonstrates the primary
create/open, publish, acquire, read, release, remove, reuse, diagnostics, and
dispose workflow described in
[Getting started](../../docs/getting-started.md).
It uses the ordinary participant-aware API and the only current protocol, SMS2.

## Concepts Demonstrated

- `SharedMemoryStoreOptions.Create` with explicit slot, lease, and participant
  capacity.
- `MemoryStore.TryCreateOrOpen`.
- Opaque byte key, descriptor, and payload values.
- Allocation-conscious helper methods for canonical integer keys and fixed
  binary descriptors.
- `TryPublish`, `TryAcquire`, `ValueLease.Release`, and `TryRemove`.
- Slot reuse after removal.
- `GetDiagnostics` and `FreeSlotCount`.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Linux or Windows for ordinary runtime validation.
- Repository checkout from the repository root.

## Run

```powershell
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
```

## Expected Output

Expected success shape:

```text
protocol: SMS2 2.0, resource 2, required 0x7, optional 0x0, participants 4
Success
Success
value bytes: 04-05-06-07
Success
Success
Success
free slots: 1
```

The first line pins the immutable protocol and participant-capacity identity.
The next two lines are publish and acquire statuses. The value bytes line shows
the acquired payload. The key is a one-byte application namespace plus a
little-endian integer, and the descriptor is a small fixed binary structure.
The remaining status lines are release, remove, and reuse publish results.

## Expected Non-Success Statuses

- `UnsupportedPlatform`: the current platform does not support the required
  named memory-mapped-file behavior.
- `InvalidOptions` or `InsufficientCapacity`: sample options no longer match
  package validation rules.
- `ParticipantTableFull`: every configured participant record is occupied by
  an open handle.
- `StoreBusy`: a bounded cold-open or local retry/help budget expired.

If open fails, the program prints `open failed: <status>` and exits with a
nonzero code.

## Cleanup

The sample uses a unique store name for each run and disposes the store handle
before exiting. It does not require manual file cleanup.

## Related Documentation

- [samples.md](../../docs/samples.md)
- [Getting started](../../docs/getting-started.md)
- [Concepts](../../docs/concepts.md)
- [Byte encoding](../../docs/byte-encoding.md)
- [Usage](../../docs/usage.md)
- [Errors](../../docs/errors.md)
- [Diagnostics](../../docs/diagnostics.md)

## Scope Boundaries and Non-Goals

This sample does not demonstrate direct reservation ingest, segmented publish,
multiple process coordination, hosting integration, persistence, or schema
parsing. Continue with the [Frame value sample](../FrameValue/README.md) and
[Zero-copy ingest sample](../ZeroCopyIngest/README.md) for advanced workflows.
