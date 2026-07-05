# Disposal and Rollover Contract

## Package Impact

- Package id remains `SharedMemoryStore`.
- Target framework remains `net10.0`.
- Runtime dependencies remain .NET BCL only.
- Semantic version impact: patch-level reliability fix for corrected lifecycle
  outcomes when public shape is unchanged; minor pre-1.0 update if shared layout
  fields or public diagnostics are added for lifecycle identity.

## Disposal Lifecycle

The store handle lifecycle has three observable phases:

- `Open`: operations may access the mapped region after normal validation.
- `Disposing`: one caller has begun disposal and operations racing with it must
  either complete before resources are released or return documented disposal
  outcomes.
- `Disposed`: resources are released and no public operation may expose mapped
  memory.

`Dispose()` is idempotent. Multiple concurrent callers may call `Dispose()`;
exactly one releases owned resources and all callers complete without throwing
from public API boundaries.

## Public Operation Outcomes After Disposal

Required outcomes after disposal completes:

| Operation | Required outcome |
|-----------|------------------|
| `TryPublish` | `StoreStatus.StoreDisposed` |
| `TryReserve` | `StoreStatus.StoreDisposed`, default invalid reservation |
| `TryPublishSegments` | `StoreStatus.StoreDisposed`, `copiedBytes = 0` |
| `TryAcquire` | `StoreStatus.StoreDisposed`, default invalid lease |
| `TryRemove` | `StoreStatus.StoreDisposed` |
| `TryRecoverLeases` | `StoreStatus.StoreDisposed`, default report |
| `TryRecoverReservations` | `StoreStatus.StoreDisposed`, default report |
| `GetDiagnostics` | safe empty or last-known diagnostic snapshot without mapped-memory access |
| `ValueLease.IsValid` | `false` |
| `ValueLease.ValueSpan` | empty span |
| `ValueLease.DescriptorSpan` | empty span |
| `ValueLease.Release` | `StoreStatus.StoreDisposed` for a token tied to the disposed handle, or `InvalidLease` for a default token |
| `ValueReservation.IsValid` | `false` |
| `ValueReservation.GetSpan` | empty span |
| `ValueReservation.DangerousGetMemory` | empty memory |
| `ValueReservation.Advance` | `StoreStatus.StoreDisposed` for a token tied to the disposed handle, or `InvalidReservation` for a default token |
| `ValueReservation.Commit` | `StoreStatus.StoreDisposed` for a token tied to the disposed handle, or `InvalidReservation` for a default token |
| `ValueReservation.Abort` | `StoreStatus.StoreDisposed` for a token tied to the disposed handle, or `InvalidReservation` for a default token |

## Disposal Race Rules

- No public operation may expose `ObjectDisposedException`,
  `AbandonedMutexException`, mapped-memory access failures, or synchronization
  disposal failures to callers.
- Operations that acquired the lifecycle boundary before disposal may complete
  with their normal documented status.
- Operations that lose the disposal race return the post-disposal outcome for
  that operation.
- Span and memory projections must re-check lifecycle state before exposing
  mapped memory.
- Diagnostic snapshots after disposal must not access disposed resources.

## Probe Cursor Rollover

Slot and lease-record search cursors are implementation details but their
observable contract is:

- candidate indexes are always within configured capacity.
- capacity one remains valid for every operation.
- empty, partially full, and full tables return documented outcomes.
- arithmetic rollover never throws runtime overflow exceptions.
- cursor rollover does not skip available records permanently.
- full slot tables return `StoreStatus.StoreFull`.
- full lease tables return `StoreStatus.LeaseTableFull`.

The implementation must use bounded arithmetic equivalent to:

```text
start = unchecked(nextSearch++)
candidate = (start + step) modulo tableCapacity
```

The actual code may use unsigned arithmetic, masking for power-of-two counts, or
another tested equivalent. It must not rely on `Math.Abs` of signed values near
`int.MinValue`.

## Slot Lifecycle Identity

Slot lifecycle identity distinguishes current contents from stale handles.
Required captured locations:

- key index entry.
- active lease record.
- pending reservation token.
- value lease token.
- slot metadata.

Validation compares the full lifecycle identity, not only slot index.

Boundary rules:
- reclaim advances lifecycle identity before the slot becomes free.
- lifecycle advancement cannot throw overflow exceptions.
- stale leases and reservations fail after any reclaim or lifecycle boundary.
- if identity cannot advance safely, the slot is not reused and the store
  returns a deterministic failure.
- any shared layout field changes must be reflected in layout versioning,
  contract tests, docs, and release notes.

## Contract Tests

Required coverage:
- every public operation racing with disposal either completes normally or
  returns its documented disposed outcome.
- repeated and concurrent `Dispose()` calls do not throw.
- lease and reservation span or memory access after disposal is empty.
- slot probe cursor seeded near rollover continues producing valid candidates.
- lease-record probe cursor seeded near rollover continues producing valid
  candidates.
- capacity-one slot and lease tables remain deterministic.
- lifecycle identity seeded near a generation boundary advances without stale
  token acceptance.
- existing publish, reserve, acquire, remove, release, recovery, diagnostics,
  and package-consumption tests continue to pass.
