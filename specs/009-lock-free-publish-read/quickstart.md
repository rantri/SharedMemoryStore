# Quickstart: Lock-Free Key-Value Store with Broker-Directed Workers

This example uses an application-owned broker to send keys to workers. The
shared-memory package stores values by key; it does not choose workers, enqueue
keys, or acknowledge work.

The API below is the layout-2.0 surface. All participants must use the
same name and dimensions.

## 1. Create explicit lock-free options

```csharp
using SharedMemoryStore;

const string StoreName = "frames-v2";

var options = SharedMemoryStoreOptions.CreateLockFree(
    name: StoreName,
    slotCount: 256,
    maxValueBytes: 1_300_000,
    maxDescriptorBytes: 16,
    maxKeyBytes: 32,
    leaseRecordCount: 128,
    openMode: OpenMode.CreateOrOpen,
    enableLeaseRecovery: true);

var open = MemoryStore.TryCreateOrOpen(options, out var store);
if (open != StoreOpenStatus.Success || store is null)
{
    throw new InvalidOperationException($"Cannot open {StoreName}: {open}");
}

using (store)
{
    Console.WriteLine($"Layout {store.ProtocolInfo.LayoutMajorVersion}." +
                      $"{store.ProtocolInfo.LayoutMinorVersion}, {store.Profile}");
    // Run the process role shown below.
}
```

`StoreProfile.Legacy` remains the default for existing options/helpers. Lock-free
layout 2.0 is never selected implicitly. `CreateLockFree` defaults to 64
participant records—one per simultaneously open `MemoryStore` handle. Set
`participantRecordCount` explicitly when the deployment needs more; a full table
rejects only the new open with `ParticipantTableFull`. Recovery is explicit and
runs through an already-open handle; opening does not silently classify stale
participants. Provision headroom (and keep a recovery-capable handle available)
when an existing mapping must survive broad process churn.

Layout 2.0 accepts `slotCount` values from 1 through 1,048,575. The explicit
ceiling keeps every helpable directory reference generation-fenced in one
portable atomic word; larger stores require a future layout version.

## 2. Producer: write directly into mapped storage

The producer owns frame creation and publishes the key to its broker only after
the store commit succeeds.

```csharp
static StoreStatus PublishFrame(
    MemoryStore store,
    ReadOnlySpan<byte> key,
    ReadOnlySpan<byte> descriptor,
    Stream frameSource,
    int frameLength,
    IApplicationKeyBroker broker)
{
    var status = store.TryReserve(key, frameLength, descriptor, out var reservation);
    if (status != StoreStatus.Success)
    {
        return status;
    }

    using (reservation)
    {
        while (reservation.RemainingBytes != 0)
        {
            var destination = reservation.GetSpan();
            var read = frameSource.Read(destination);
            if (read == 0)
            {
                return StoreStatus.ReservationIncomplete;
            }

            status = reservation.Advance(read);
            if (status != StoreStatus.Success)
            {
                return status;
            }
        }

        status = reservation.Commit();
        if (status != StoreStatus.Success)
        {
            return status;
        }
    }

    // Application concern: choose a worker and deliver/track the key.
    broker.PublishKey(key);
    return StoreStatus.Success;
}
```

There is no producer-owned full-frame buffer and no library copy after the
source fills the reservation. Partial bytes remain invisible until commit.
One reservation belongs to one producer; do not concurrently write/advance
through copied reservation structs. In lock-free v2, this explicit reservation
orders at `Reserved(ExplicitReservation)` and then becomes visible at commit.
The `TryPublish` and `TryPublishSegments` convenience APIs instead use an
internal `Reserved(AtomicPublication)` stage that remains tentative until the
one public call reaches `Published`; a same-key contender cannot use that
tentative stage alone as `DuplicateKey`. Both kinds of non-Free slot still
consume physical capacity and may contribute to `StoreFull`.

## 3. Six to twelve workers: acquire the assigned key

Each worker receives a key from the application broker, independently opens the
same store, and reads the immutable shared bytes.

```csharp
static void ProcessAssignedKey(
    MemoryStore store,
    ReadOnlySpan<byte> key,
    IApplicationKeyBroker broker,
    int workerId)
{
    var status = store.TryAcquire(key, out var lease);
    if (status == StoreStatus.NotFound)
    {
        broker.ReportMissing(workerId, key); // Application retry/dead-letter policy.
        return;
    }

    if (status != StoreStatus.Success)
    {
        broker.ReportStoreFailure(workerId, key, status);
        return;
    }

    using (lease)
    {
        DecodeFrame(lease.DescriptorSpan, lease.ValueSpan);
    }

    broker.Acknowledge(workerId, key); // Not stored in SharedMemoryStore.
}
```

A copied key message is not a store lease. A worker must acquire and release its
own lease. Missing/removed keys are normal key-value outcomes, not broker state.

## 4. Independent observer: read the same key concurrently

The observer is not a worker and has no exclusive claim. It may lease the exact
same value while a worker holds it.

```csharp
static bool TryObserve(MemoryStore store, ReadOnlySpan<byte> key, out ulong checksum)
{
    checksum = 0;
    if (store.TryAcquire(key, out var lease) != StoreStatus.Success)
    {
        return false;
    }

    using (lease)
    {
        checksum = ComputeChecksum(lease.ValueSpan);
        return true;
    }
}
```

Pausing this observer retains only its lease record and that value generation.
Other readers of the same key and operations on other keys continue.

## 5. Remove only after application-level processing policy allows it

The application—not the store—decides when assigned work and observation permit
cleanup.

```csharp
var remove = store.TryRemove(key);
switch (remove)
{
    case StoreStatus.Success:
        // Logically absent with no protecting lease observed. Physical slot
        // reuse may already be complete or may finish cooperatively.
        break;

    case StoreStatus.RemovePending:
        // Logically absent now. Existing leases or bounded post-removal work may
        // remain; a final release or later remove/help will reclaim exactly once.
        break;

    case StoreStatus.NotFound:
        // Already absent.
        break;

    default:
        HandleStoreStatus(remove);
        break;
}
```

After logical removal, a new acquire does not succeed. A lease established
before removal continues projecting the exact immutable bytes. Republishing the
same key remains duplicate until that generation is safely reclaimed.

## 6. Handle bounded pressure explicitly

```csharp
var wait = new StoreWaitOptions(TimeSpan.FromMilliseconds(25), cancellationToken);
var status = store.TryAcquire(key, wait, out var lease);

// Distinct application decisions:
// NotFound       -> broker/application key is no longer current
// LeaseTableFull -> configured read-protection capacity is exhausted
// StoreBusy      -> this caller exhausted its local retry/contention budget
// OperationCanceled -> caller canceled before acquisition ordered
```

V2 wait options do not wait for a key or capacity to appear and never acquire a
global store-operation lock.

## 7. Recover a terminated participant explicitly

Run recovery from an authorized control path after the application has evidence
that a participant terminated. Unknown/live owners are preserved.

```csharp
var leaseStatus = store.TryRecoverLeases(
    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
    out var leaseReport);

var reservationStatus = store.TryRecoverReservations(
    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
    out var reservationReport);

Console.WriteLine(
    $"lease={leaseStatus}, recovered={leaseReport.RecoveredLeaseCount}, " +
    $"active={leaseReport.ActiveLeaseCount}, unsupported={leaseReport.UnsupportedLeaseCount}");

Console.WriteLine(
    $"reservation={reservationStatus}, " +
    $"recovered={reservationReport.RecoveredReservationCount}, " +
    $"active={reservationReport.ActiveReservationCount}, " +
    $"unsupported={reservationReport.UnsupportedReservationCount}");
```

Recovery restores only safely stale slots/records. It is not needed to unblock
unrelated healthy operations and does not run in a hidden thread or during
opening. If every participant record is occupied and no handle remains open,
opening returns `ParticipantTableFull`; recreate the store or use deployment
coordination appropriate to that total-process-loss scenario.

Keep `RecoverCurrentProcessLeases` false during normal operation; that mode is
safe while readers on any process acquire, project, use, and release leases. The
true value is an administrative test/controlled-shutdown override. Before using
it, stop new current-process acquisitions and drain all lease projection,
borrowed-span use, and release activity across **every** handle in the process
that is attached to this mapping. Keep those activities quiescent until recovery
returns. Merely draining the handle that calls recovery is insufficient, and the
library intentionally adds no hot-path gate to enforce this shutdown policy.

Keep `RecoverCurrentProcessReservations` false during normal operation as well;
normal recovery preserves an exact live Active reservation/publication owner.
Before selecting the true administrative override, quiesce `TryReserve`,
`TryPublish`, `TryPublishSegments`, reservation projection and borrowed writable
memory, `Advance`, `Commit`, `Abort`, and reservation disposal across every
current-process handle attached to this mapping; do not dispose those store
handles concurrently either. Maintain that quiescence until recovery returns.
Racing the override with current-process publication activity is outside the
supported result contract.

## 8. Inspect pressure without pausing data paths

```csharp
if (store.TryGetDiagnostics(out var snapshot) == StoreStatus.Success)
{
    ExportGauge("sms.free_slots", snapshot.FreeSlotCount);
    ExportGauge("sms.active_leases", snapshot.ActiveLeaseCount);
    ExportGauge("sms.pending_removals", snapshot.PendingRemovalCount);
    // V2 additive fields expose spill, retries, helping, and recovery pressure.
}
```

Snapshots may combine observations from nearby instants. They are safe during
live operations and do not make publishers/readers wait for a consistent global
snapshot.

## 9. Upgrade and rollback safely

- Do not point legacy and lock-free participants at the same live mapping. The
  library rejects the incompatible profile.
- For same-name cutover, drain/close all legacy handles, recreate layout 2.0,
  and republish application-owned data.
- For side-by-side deployment, use an explicitly different public store name and
  switch application/broker keys under application control.
- Rollback recreates v1.2; it never reinterprets v2 bytes.

## Application-owned broker sketch

```csharp
public interface IApplicationKeyBroker
{
    void PublishKey(ReadOnlySpan<byte> key);
    void Acknowledge(int workerId, ReadOnlySpan<byte> key);
    void ReportMissing(int workerId, ReadOnlySpan<byte> key);
    void ReportStoreFailure(int workerId, ReadOnlySpan<byte> key, StoreStatus status);
}
```

This interface is illustrative sample code and is not part of the
SharedMemoryStore package.

## Validated package sample

The repository sample can use either the source project (the default) or the
packed `SharedMemoryStore` 2.0.0 package. The package-consumer path is selected
with `-p:UsePackedSharedMemoryStore=true`; restore it from the directory
containing `SharedMemoryStore.2.0.0.nupkg`, then run the same built sample:

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/quickstart-package
dotnet restore samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj `
  -p:UsePackedSharedMemoryStore=true `
  --source artifacts/quickstart-package
dotnet build samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj -c Release `
  --no-restore -p:UsePackedSharedMemoryStore=true
dotnet run --project samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj -c Release `
  --no-build -p:UsePackedSharedMemoryStore=true -- --workers 6 --frames 12
dotnet run --project samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj -c Release `
  --no-build -p:UsePackedSharedMemoryStore=true -- --workers 12 --frames 24
```

On 2026-07-13, this end-to-end package path built with zero warnings and zero
errors on Windows x64/.NET 10. Both runs completed successfully: 12/12 frames
with 6 workers and 24/24 frames with 12 workers. In each run the worker and
independent-observer checksums matched, lease-protected removal returned
`RemovePending`, the bounded missing-key read returned `NotFound`, diagnostics
reported layout 2.0, and explicit lease/reservation recovery reported no stale
records.
