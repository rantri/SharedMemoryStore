# Frame Value Sample

## Purpose and Audience

This sample is for consumers who want to store frame-shaped data while keeping
application-specific frame parsing outside the core package. It puts frame
metadata in descriptor bytes and frame payload bytes in the value span.

## Concepts Demonstrated

- Consumer-owned descriptor layout through `FrameDescriptor`.
- Opaque payload bytes.
- Two simultaneous `ValueLease` readers.
- `RemovePending` while readers protect a slot.
- Slot reuse after readers release.
- Frame-neutral behavior: the core store treats the frame and non-frame value
  the same way.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Linux or Windows for ordinary runtime validation.
- Repository checkout from the repository root.

## Run

```powershell
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
```

## Expected Output

Expected success shape:

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

`RemovePending` is expected because two active readers still protect the frame
value when removal is requested. After both leases are disposed, the sample
publishes a non-frame value to show storage reuse and frame neutrality.

## Expected Non-Success Statuses

- `UnsupportedPlatform`: the current platform does not support the required
  named memory-mapped-file behavior.
- `ValueTooLarge` or `DescriptorTooLarge`: the sample frame or descriptor no
  longer fits the configured capacities.
- `LeaseTableFull`: the lease record capacity is too small for the reader
  count.

If open fails, the program prints `open failed: <status>` and exits with a
nonzero code.

## Cleanup

The sample uses a unique store name for each run, disposes reader leases, and
disposes the store handle before exiting. It does not require manual file
cleanup.

## Related Documentation

- [samples.md](../../docs/samples.md)
- [Concepts](../../docs/concepts.md)
- [Examples](../../docs/examples.md)
- [Lifecycle](../../docs/lifecycle.md)
- [Portability](../../docs/portability.md)

## Scope Boundaries and Non-Goals

The descriptor format is a sample convention, not a package schema. The core
package does not parse frames, validate pixel formats, persist frame data, or
provide current C++ or Python bindings.
