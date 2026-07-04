# Zero-Copy Ingest Sample

## Purpose and Audience

This advanced sample demonstrates the reservation workflow for
length-delimited frames whose payload length and descriptor are known before
all payload bytes arrive. It also shows segmented publication for already
buffered payloads.

## Concepts Demonstrated

- `MemoryStore.TryReserve` and `ValueReservation`.
- Chunked writes through `GetSpan`.
- Exact `Advance` progress and atomic `Commit`.
- `Abort` cleanup for incomplete frames.
- `TryPublishSegments` with `ReadOnlySequence<byte>`.
- A `System.IO.Pipelines` adapter outside the core package.
- Reader acquire, descriptor/value inspection, release, and remove.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Linux or Windows for ordinary runtime validation.
- Repository checkout from the repository root.

## Run

Run the full sample:

```powershell
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
```

Focused modes are also runnable:

```powershell
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- socket
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- pipeline
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- reader
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- segmented
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- abort
```

## Expected Output

Expected full-sample success shape:

```text
stream commit: Success
direct reader acquire: Success
direct reader descriptor: 0C-00-00-00-01
direct reader value: 01-02-03-04-05-06-07-08-09-0A-0B-0C
direct reader release: Success
direct reader remove: Success
abort reserve: Success
abort: Success
abort acquire: NotFound
segmented publish: Success
segmented copied: 5
segmented reader acquire: Success
segmented reader descriptor: 05-00-00-00-03
segmented reader value: 14-15-16-17-18
segmented reader release: Success
segmented reader remove: Success
pipeline publish: Success
pipeline copied: 6
pipeline reader acquire: Success
pipeline reader descriptor: 06-00-00-00-02
pipeline reader value: 1E-1F-20-21-22-23
pipeline reader release: Success
pipeline reader remove: Success
```

Focused modes print the subset for their path. `abort acquire: NotFound` is the
expected proof that aborted reservation bytes were not published.

## Expected Non-Success Statuses

- `UnsupportedPlatform`: the current platform does not support the required
  named memory-mapped-file behavior.
- `ReservationIncomplete`: commit was attempted before exact payload progress.
- `ReservationWriteOutOfRange`: a producer advanced beyond the announced
  payload length.
- `InvalidReservation` or `ReservationAlreadyCompleted`: a token was reused
  after completion.
- `StoreFull` or `DuplicateKey`: capacity or key ownership prevented
  reservation or publish.

If open fails, the program prints `open failed: <status>` and exits with a
nonzero code.

## Cleanup

The sample uses a unique store name for each run. Every successful reader lease
is released, every completed value is removed, incomplete reservations are
aborted, and the store handle is disposed before exit.

## Related Documentation

- [samples.md](../../docs/samples.md)
- [Concepts](../../docs/concepts.md)
- [Usage](../../docs/usage.md)
- [Examples](../../docs/examples.md)
- [Lifecycle](../../docs/lifecycle.md)
- [Diagnostics](../../docs/diagnostics.md)
- [Reservation API contract](../../specs/003-zero-copy-ingest/contracts/reservation-api.md)

## Scope Boundaries and Non-Goals

The core store remains frame-neutral. Descriptor and payload bytes are opaque to
the library. The pipeline mode is an adapter example and does not make
`System.IO.Pipelines` a runtime package dependency.
