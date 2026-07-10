# Usage

This guide describes the primary package consumer workflows. Read
[Concepts](concepts.md) first if the terms store, key, descriptor, payload,
slot, lease, reservation, wait policy, or diagnostics snapshot are new.

Behavior traces:

- [public-api.md](../specs/001-frame-memory-store/contracts/public-api.md)
- [error-taxonomy.md](../specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [reservation-api.md](../specs/003-zero-copy-ingest/contracts/reservation-api.md)
- [ingest-layout.md](../specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [contention-configuration-contract.md](../specs/005-api-production-readiness/contracts/contention-configuration-contract.md)
- [reservation-memory-contract.md](../specs/005-api-production-readiness/contracts/reservation-memory-contract.md)

## Install or Reference

For a repository checkout, build a local package source:

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
dotnet new console -f net10.0 -n SharedMemoryStore.Tryout -o artifacts/tryout
dotnet add artifacts/tryout/SharedMemoryStore.Tryout.csproj package SharedMemoryStore --source artifacts/package
```

See [Getting started](getting-started.md) for the first-use workflow and
[Packaging](packaging.md) for package metadata and clean consumer validation.

## Create or Open

Choose a store name, open mode, and fixed capacities. Use
`SharedMemoryStoreOptions.Create` for ordinary cases or set properties directly
when you need full control.

```csharp
using SharedMemoryStore;

var options = SharedMemoryStoreOptions.Create(
    name: "sms-app-values",
    slotCount: 128,
    maxValueBytes: 1_048_576,
    maxDescriptorBytes: 256,
    maxKeyBytes: 64,
    leaseRecordCount: 256,
    enableLeaseRecovery: true);

var open = MemoryStore.TryCreateOrOpen(options, out var store);
if (open != StoreOpenStatus.Success || store is null)
{
    return;
}
```

`OpenMode.CreateNew` fails with `AlreadyExists` when the mapping already exists.
`OpenMode.OpenExisting` fails with `NotFound` when it does not. `CreateOrOpen`
creates when needed and validates an existing layout before returning success.

## Validate Options

Use validation when options come from configuration:

```csharp
var validation = options.Validate();
if (!validation.IsValid)
{
    foreach (var failure in validation.Failures)
    {
        Console.WriteLine($"{failure.MemberName}: {failure.Message}");
    }
}
```

Validation failures map to `StoreOpenStatus.InvalidOptions` or
`InsufficientCapacity`. Capacity choices should leave room for published values,
pending removals, pending reservations, concurrent readers, and key churn.

## Encode Keys, Descriptors, and Payloads

Keys, descriptors, and payloads are byte sequences. Public operations accept
`ReadOnlySpan<byte>`, so hot paths can write canonical bytes into stack or
pooled buffers and pass spans without creating a new `byte[]`.

```csharp
using System.Buffers.Binary;

Span<byte> key = stackalloc byte[1 + 4];
key[0] = 1; // application-owned key namespace
BinaryPrimitives.WriteInt32LittleEndian(key[1..], orderId);

Span<byte> descriptor = stackalloc byte[12];
BinaryPrimitives.WriteInt32LittleEndian(descriptor[0..4], schemaVersion);
BinaryPrimitives.WriteInt64LittleEndian(descriptor[4..12], timestampTicks);

var status = store.TryPublish(key, payload, descriptor);
```

Use [Byte encoding](byte-encoding.md) for recommended helper methods,
string/GUID key conventions, composite keys, descriptor layout, and payload
allocation guidance.

## Wait Policies

Operations use `StoreWaitOptions.Default` unless you pass a policy overload.

```csharp
var status = store.TryPublish(
    key,
    payload,
    descriptor,
    StoreWaitOptions.NoWait);
```

Use `NoWait` for health probes or request paths where immediate `StoreBusy` is
better than waiting. Use `Infinite` only when indefinite blocking is a deliberate
application decision. Cancellation returns `OperationCanceled`.

## Publish Values

Use `TryPublish` when the payload already exists as a contiguous
`ReadOnlySpan<byte>`.

```csharp
var key = new byte[] { 1, 2, 3 };
var descriptor = new byte[] { 9, 8 };
var payload = new byte[] { 4, 5, 6, 7 };

var publish = store.TryPublish(key, payload, descriptor);
```

Expected success is `StoreStatus.Success`. Common non-success statuses are
`DuplicateKey`, `InvalidKey`, `KeyTooLarge`, `ValueTooLarge`,
`DescriptorTooLarge`, `StoreFull`, `StoreBusy`, `OperationCanceled`,
`UnsupportedPlatform`, `StoreDisposed`, `CorruptStore`, and `UnknownFailure`.

## Acquire and Read

`TryAcquire` returns a `ValueLease` that protects one published slot generation.
Read descriptor and payload spans only while the lease is active.

```csharp
var acquire = store.TryAcquire(key, out var lease);
if (acquire == StoreStatus.Success)
{
    try
    {
        ReadOnlySpan<byte> descriptorBytes = lease.DescriptorSpan;
        ReadOnlySpan<byte> payloadBytes = lease.ValueSpan;
    }
    finally
    {
        _ = lease.Release();
    }
}
```

Use `Release()` when the status matters. Use `Dispose()` for best-effort cleanup
when you do not need the status.

## Remove and Reuse

Removal removes the key from the visible index. If no readers hold leases, the
slot is reclaimed immediately. If readers still hold leases, removal returns
`RemovePending`, new acquires for that key fail, and final release reclaims the
slot.

```csharp
var remove = store.TryRemove(key);
```

Publishing the same key while the value is still published, pending removal, or
pending reservation returns `DuplicateKey`.

## Direct Reservation Ingest

Use `TryReserve` when the key, descriptor, and final payload length are known
before all payload bytes are available. This is the typical direct ingest path
for length-delimited frames.

```csharp
var reserve = store.TryReserve(key, payloadLength: 4, descriptor, out var reservation);
if (reserve == StoreStatus.Success)
{
    new byte[] { 4, 5, 6, 7 }.CopyTo(reservation.GetSpan(4));
    var advance = reservation.Advance(4);
    var complete = advance == StoreStatus.Success
        ? reservation.Commit()
        : reservation.Abort();
}
```

Readers cannot acquire the key until `Commit()` succeeds. Commit before exact
completion returns `ReservationIncomplete`. Advancing beyond the remaining
payload length returns `ReservationWriteOutOfRange`. `Abort()` and active
reservation disposal remove the pending key without exposing partial bytes.

Writable spans are valid only while the reservation is pending and the store
handle remains open. Use `DangerousGetMemory` only for trusted direct-I/O
adapters, such as stream or socket reads that require `Memory<byte>`. The
returned memory is retained-capable, so callers must not retain or use it after
commit, abort, recovery, disposal, store disposal, or slot reuse.

## Segmented Publish

Use `TryPublishSegments` when payload bytes already exist in a
`ReadOnlySequence<byte>` and flattening them first would be wasteful.

```csharp
ReadOnlySequence<byte> payload = GetPayloadSequence();
var publish = store.TryPublishSegments(key, payload, descriptor, out var copiedBytes);
```

The helper acquires shared synchronization once, reserves a contiguous slot,
copies each segment in order, and publishes only after the copied byte count
matches the sequence length. A copy or validation failure reclaims the internal
slot before synchronization is released, so bounded contention cannot strand a
caller-inaccessible reservation.

## Diagnostics

Use `GetDiagnostics()` for a best-effort snapshot from an open store handle.
Use `TryGetDiagnostics()` when the status matters, for example in health checks
or shutdown paths.

```csharp
var status = store.TryGetDiagnostics(out var snapshot);
if (status == StoreStatus.Success)
{
    Console.WriteLine(snapshot.FreeSlotCount);
    Console.WriteLine(snapshot.GetFailureCount(StoreStatus.StoreFull));
}
```

The package does not write logs, export metrics, or start background workers.
See [Diagnostics](diagnostics.md) for fields and support evidence.

## Explicit Recovery

Recovery is owner policy, not automatic cleanup.

```csharp
var leaseRecovery = store.TryRecoverLeases(
    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
    out var leaseReport);

var reservationRecovery = store.TryRecoverReservations(
    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
    out var reservationReport);
```

Use recovery for controlled owner workflows, tests, or cleanup after abnormal
termination. Normal readers and producers should still release leases, commit or
abort reservations, and dispose store handles directly.

## Dispose

Dispose every store handle when finished:

```csharp
store.Dispose();
```

Disposal invalidates future operations on that handle and invalidates span
projections previously obtained from leases or reservations. Public operations
racing with disposal complete if they entered first or return documented
statuses such as `StoreDisposed`, invalid token outcomes, or empty spans.

## Related Samples

- [samples/BasicUsage/README.md](../samples/BasicUsage/README.md): minimal
  create, publish, acquire, release, remove, reuse, and diagnostics.
- [samples/FrameValue/README.md](../samples/FrameValue/README.md): descriptor
  metadata and multiple-reader removal behavior.
- [samples/ZeroCopyIngest/README.md](../samples/ZeroCopyIngest/README.md):
  reservation, abort, segmented publish, and reader workflows.
- [samples/HostedServiceIntegration/README.md](../samples/HostedServiceIntegration/README.md):
  optional lifecycle and health wrapper.
