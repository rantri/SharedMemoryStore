# Zero-Copy Ingest Sample

This sample demonstrates the reservation workflow for length-delimited frames
whose payload length and descriptor are known before payload bytes arrive.

Run:

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

The sample covers:

- length-prefixed stream ingest, chunked writes through reservation spans,
  exact progress, commit, acquire, release, and remove.
- a `System.IO.Pipelines` adapter that publishes the payload sequence through
  `TryPublishSegments`.
- a separate reader mode that opens a committed value through the public
  `TryAcquire` and `ValueLease` workflow.
- abort cleanup for an incomplete frame.
- segmented publication with `ReadOnlySequence<byte>` without a temporary
  full-payload array.

The core store remains frame-neutral. Descriptor and payload bytes are opaque to
the library, and reader leases expose the same immutable spans as values
published with `TryPublish`.
