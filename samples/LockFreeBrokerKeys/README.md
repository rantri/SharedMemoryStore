# Broker-Key Dispatch Sample

## Purpose and Audience

This sample is for applications that already own a broker or work queue and
want its messages to carry small shared-memory keys instead of large payloads.
The broker remains application-owned; SharedMemoryStore remains a bounded
key-value store.

## Concepts Demonstrated

- one ordinary SMS2 store opened by a producer, several workers, and an
  observer;
- explicit participant capacity for concurrently open handles;
- direct reservation publication of 4 KiB values;
- channel messages containing eight-byte keys rather than payload copies;
- concurrent immutable leases and lease-protected `RemovePending`;
- bounded missing-key lookup, explicit recovery, diagnostics, and protocol
  identity `(2, 0, 2, 7, 0)`.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- A qualified x86-64 Windows or Linux host.
- Repository checkout from the repository root.

## Run

```powershell
dotnet run --project samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj -c Release
```

Choose six to twelve workers and at least as many frames:

```powershell
dotnet run --project samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj -c Release -- --workers 8 --frames 64
```

## Expected Output

The successful run prints one result line with the selected counts and
operation outcomes:

```text
RESULT workers=6 frames=48 processed=48 workerChecksum=<value> observerChecksum=<value> pendingRemove=RemovePending missing=NotFound diagnostics=Success layout=2.0 recoveredLeases=0 recoveredReservations=0
```

Checksums are application evidence that workers and the independent observer
read the published bytes. Recovery counts are normally zero because the sample
closes every token normally.

## Expected Non-Success Statuses

- `UnsupportedPlatform`: required mapped atomic, mapping, lifecycle, or owner
  evidence is unavailable.
- `ParticipantTableFull`: configured open-handle capacity is exhausted.
- `StoreFull`, `LeaseTableFull`, or `DuplicateKey`: a configured capacity or
  key invariant was violated.
- `StoreBusy` or `OperationCanceled`: a bounded local progress policy ended the
  operation.

Unexpected outcomes fail the sample with a nonzero exit code and an actionable
message.

## Cleanup

The sample uses a unique name, releases every lease, removes every value, and
disposes all handles. No manual mapping cleanup is required after a normal run.

## Related Documentation

- [Samples](../../docs/samples.md)
- [Examples](../../docs/examples.md)
- [Usage](../../docs/usage.md)
- [Lifecycle](../../docs/lifecycle.md)
- [Diagnostics](../../docs/diagnostics.md)
- [Architecture](../../docs/architecture.md)

## Scope Boundaries and Non-Goals

The in-process channels stand in for application dispatch. SharedMemoryStore
does not queue, route, acknowledge, retry, or persist messages, and it does not
provide cross-host transport.
