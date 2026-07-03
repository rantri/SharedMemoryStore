# Public API Change Inventory

## Renamed Primary Type

- `SharedMemoryStore.SharedMemoryStore` is replaced by `SharedMemoryStore.MemoryStore`.
- The root namespace and NuGet package ID remain `SharedMemoryStore`.
- Public examples should import `using SharedMemoryStore;` and reference `MemoryStore` directly.

## Status Additions

- `StoreStatus.InvalidKey`: empty keys are invalid input.
- `StoreStatus.StoreBusy`: shared synchronization was not acquired within the selected wait policy.
- `StoreStatus.OperationCanceled`: cancellation was observed before shared synchronization was acquired.
- `StoreOpenStatus.StoreBusy`: open/create equivalent for synchronization contention.
- `StoreOpenStatus.OperationCanceled`: open/create equivalent for cancellation before synchronization acquisition.

## Reservation API Changes

- `ValueReservation.GetMemory(int)` is removed from the public API.
- `ValueReservation.GetSpan(int)` remains the immediate write path while the reservation is active.
- `Advance`, `Commit`, and `Abort` add wait-policy overloads.

## Wait Policy Additions

- `StoreWaitOptions.Default`: one-second bounded default.
- `StoreWaitOptions.NoWait`: immediate busy result on contention.
- `StoreWaitOptions.Infinite`: explicit opt-in indefinite wait.
- Wait-policy overloads were added for open/create, publish, reserve, segmented publish, acquire, remove, recovery, diagnostics, lease release, and reservation token operations.

## Options and Diagnostics

- `SharedMemoryStoreOptions.Create(...)` derives `TotalBytes` for ordinary configurations.
- `SharedMemoryStoreOptions.Validate(...)` and instance `Validate()` return public validation details.
- Diagnostics failure counts are aggregate-first through `DiagnosticsSnapshot.GetFailureCount(StoreStatus)`.
- Per-status failure-count convenience properties are removed in favor of aggregate access.

## Migration Notes

1. Replace aliases such as `using Store = SharedMemoryStore.SharedMemoryStore;` with direct `MemoryStore` usage or `using Store = SharedMemoryStore.MemoryStore;`.
2. Replace reservation memory usage with immediate `GetSpan` writes followed by `Advance`.
3. Replace diagnostics convenience property reads with `snapshot.GetFailureCount(StoreStatus.SomeStatus)`.
4. Handle `InvalidKey`, `StoreBusy`, and `OperationCanceled` outcomes where appropriate.
