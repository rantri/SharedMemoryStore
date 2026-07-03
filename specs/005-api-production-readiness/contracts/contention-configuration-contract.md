# Contract: Contention, Cancellation, and Configuration

## Wait Policy

Every public operation that can wait on shared synchronization accepts or uses a
documented `StoreWaitOptions` policy.

Expected public shape:

```csharp
public readonly record struct StoreWaitOptions(
    TimeSpan Timeout,
    CancellationToken CancellationToken = default)
{
    public static StoreWaitOptions Default { get; }
    public static StoreWaitOptions NoWait { get; }
    public static StoreWaitOptions Infinite { get; }
}
```

Exact constructor and factory names may change during implementation, but the
contract must expose:
- A documented bounded default.
- A finite timeout path.
- A no-wait path.
- An explicit infinite wait path for consumers that intentionally want it.
- Cancellation before synchronization is acquired.

`StoreWaitOptions.Default.Timeout` is one second. Tests for bounded waits must
allow the selected timeout plus 250 milliseconds of scheduler tolerance before
treating the operation as late.

## Operation Coverage

The wait policy applies to:
- `MemoryStore.TryCreateOrOpen`.
- `TryPublish`.
- `TryReserve`.
- `TryPublishSegments`.
- `TryAcquire`.
- `TryRemove`.
- `TryRecoverLeases`.
- `TryRecoverReservations`.
- `TryGetDiagnostics` or an equivalent status-returning diagnostics API.
- Existing `GetDiagnostics` convenience behavior, if retained.
- `ValueLease.Dispose` or explicit release behavior.
- `ValueReservation.Advance`, `Commit`, `Abort`, and `Dispose`.

## Contention Outcomes

For `StoreStatus` operations:
- Timeout, no-wait contention, or busy synchronization returns `StoreBusy`.
- Cancellation before acquisition returns `OperationCanceled`.
- Store disposal while waiting returns `StoreDisposed`.
- Abandoned synchronization is handled according to existing reliability
  contracts and must not be reported as success unless shared state is still
  safe.

For `StoreOpenStatus` operations:
- Timeout, no-wait contention, or busy synchronization returns the open-status
  equivalent of `StoreBusy`.
- Cancellation before acquisition returns the open-status equivalent of
  `OperationCanceled`.
- Invalid wait policy returns `InvalidOptions`.

For diagnostics:
- The wait-aware diagnostics API returns `StoreStatus.Success`,
  `StoreStatus.StoreBusy`, `StoreStatus.OperationCanceled`, or
  `StoreStatus.StoreDisposed` and writes the snapshot to an `out` parameter.
- A retained convenience `GetDiagnostics()` may call the default wait policy,
  but it must not be the only public diagnostics path that can report busy or
  canceled acquisition.

## Mutation Rules

- If synchronization is not acquired, the operation must not mutate shared store
  state.
- If cancellation is observed before acquisition, the operation must not mutate
  shared store state.
- If timeout expires before acquisition, the operation must not mutate shared
  store state.
- If the operation acquires synchronization and then detects store disposal, it
  returns the lifecycle outcome documented for that operation.

## Configuration Contract

`SharedMemoryStoreOptions` must provide a valid-by-construction path for common
configuration and a public validation path for diagnostics.

Required behavior:
- Undefined `OpenMode` values are invalid options.
- Empty, whitespace, null-character, and too-long names are invalid options.
- Slot count, value bytes, key bytes, lease records, and total bytes are
  required and positive unless derived by a helper.
- Descriptor bytes may be zero but not negative.
- Required byte calculations detect overflow.
- `TotalBytes` smaller than the calculated layout returns insufficient capacity
  or validation detail that clearly distinguishes it from malformed options.

## Key Validation Contract

Public key validation distinguishes:
- Empty or null-equivalent key: `InvalidKey`.
- Key length greater than `MaxKeyBytes`: `KeyTooLarge`.
- Missing stored value for a valid key: `NotFound`.

## Tests

Contract and integration tests must:
- Hold the shared mutex from another owner and prove each public operation
  returns the documented busy or timeout outcome within the selected wait limit.
- Cancel before acquisition and prove cancellation is reported distinctly from
  busy, timeout, disposed, and validation outcomes.
- Validate default wait behavior is bounded and documented.
- Verify the default timeout is one second with 250 milliseconds of scheduler
  tolerance.
- Reject invalid `OpenMode` values supplied by casts, configuration binding, or
  deserialization.
- Verify size helpers match layout calculations and prevent consumer-side
  duplication of layout constants.
- Verify empty and oversized keys return distinct outcomes across all public
  entry points.
