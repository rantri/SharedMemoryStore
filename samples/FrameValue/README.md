# Frame Value Sample

This sample shows a consumer-owned frame layout on top of the opaque byte-value
store. The core SharedMemoryStore package does not parse frames. The sample puts
frame metadata in descriptor bytes and frame payload bytes in the value span.

See [Examples](../../docs/examples.md) and [Portability](../../docs/portability.md)
for the contract background.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Windows x64 for the current named memory-mapped-file validation target.

## Run

```powershell
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
```

Expected output:

```text
Success
Success
Success
frame 1280x720, bytes 1300000, readers equal True
RemovePending
Success
Success
non-frame bytes: 3
```

The `RemovePending` status is expected because two active readers still protect
the frame value when removal is requested. After both leases are disposed, the
sample publishes a non-frame value to show slot reuse and frame neutrality.

## Layout Rules

- Descriptor layout is owned by the consumer.
- Payload layout is owned by the consumer.
- The core store only enforces byte lengths, lease lifetime, removal, and reuse.
- Future C++ or Python consumers must follow the shared-memory layout contract,
  but no current bindings are provided by this sample.
