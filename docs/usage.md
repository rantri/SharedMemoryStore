# Usage

This guide describes the primary package consumer workflow. Detailed public API
rules are traced to the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md),
and deterministic status behavior is traced to the
[error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md).

## Create or Open a Store

Configure explicit capacity limits and calculate the required mapped-region
size from those limits.

```csharp
using SharedMemoryStore;
using Store = SharedMemoryStore.SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = "sms-app-values",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 128,
    MaxValueBytes = 1_048_576,
    MaxDescriptorBytes = 256,
    MaxKeyBytes = 64,
    LeaseRecordCount = 256,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(128, 1_048_576, 256, 64, 256)
};

var open = Store.TryCreateOrOpen(options, out var store);
if (open != StoreOpenStatus.Success || store is null)
{
    // InvalidOptions, UnsupportedPlatform, AccessDenied, MappingFailed,
    // NotFound, AlreadyExists, IncompatibleLayout, or InsufficientCapacity.
    return;
}
```

`OpenMode.CreateNew` fails with `AlreadyExists` when the mapping exists.
`OpenMode.OpenExisting` fails with `NotFound` when it does not exist.
`OpenMode.CreateOrOpen` creates the mapping when needed and validates layout
compatibility when opening an existing mapping.

## Publish

Publish stores immutable payload bytes and optional descriptor bytes under an
opaque byte key.

```csharp
var key = new byte[] { 1, 2, 3 };
var descriptor = new byte[] { 9, 8 };
var payload = new byte[] { 4, 5, 6, 7 };

var publish = store.TryPublish(key, payload, descriptor);
```

Expected success returns `StoreStatus.Success`. Common non-success statuses are
`DuplicateKey`, `KeyTooLarge`, `ValueTooLarge`, `DescriptorTooLarge`,
`StoreFull`, `UnsupportedPlatform`, `StoreDisposed`, `CorruptStore`, and
`UnknownFailure`.

## Acquire and Read

Acquire returns a `ValueLease` that protects the slot generation while the
caller reads the descriptor and value spans.

```csharp
var acquire = store.TryAcquire(key, out var lease);
if (acquire == StoreStatus.Success)
{
    try
    {
        ReadOnlySpan<byte> descriptorSpan = lease.DescriptorSpan;
        ReadOnlySpan<byte> valueSpan = lease.ValueSpan;
    }
    finally
    {
        _ = lease.Release();
    }
}
```

Read spans are valid only while the lease is active and the store remains open.
The spans are read-only, and the core store never parses application-specific
payload formats.

## Release

Call `Release()` when the status matters. Use `Dispose()` when best-effort
release is enough.

```csharp
var release = lease.Release();
```

The first release of an active lease returns `Success`. Releasing the same lease
again returns `LeaseAlreadyReleased` or `InvalidLease` depending on the lease
record state.

## Remove and Reuse

Remove deletes the key from the public index and makes the slot reusable when no
active lease protects it.

```csharp
var remove = store.TryRemove(key);
```

If no readers hold a lease, expected status is `Success` and the slot can be
reused immediately. If active leases exist, expected status is `RemovePending`.
The final release advances the slot generation and allows reuse.

```csharp
_ = store.TryPublish(key, [10]);
```

Publishing the same key while a value is still published or pending removal
returns `DuplicateKey`.

## Diagnostics

`GetDiagnostics()` returns a snapshot for caller-owned formatting, logging, or
metrics export.

```csharp
var diagnostics = store.GetDiagnostics();
Console.WriteLine($"free slots: {diagnostics.FreeSlotCount}");
Console.WriteLine($"last failure: {diagnostics.LastFailureStatus}");
```

The library does not write diagnostics to the console and does not start hidden
background work.

## Stale Lease Recovery

When `EnableLeaseRecovery` is true, owners may call explicit recovery. Recovery
policy is caller-controlled and platform support is deterministic.

```csharp
var recovery = store.TryRecoverLeases(
    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
    out var report);
```

Use recovery for controlled owner workflows, not as a replacement for normal
lease disposal.

## Dispose

Dispose the store handle when finished.

```csharp
store.Dispose();
```

Disposal invalidates future operations on that handle and invalidates span
projections previously obtained from leases.
