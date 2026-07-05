# Phase 0 Research: API Production Readiness

## Decision: Rename the primary store type to `MemoryStore`

**Rationale**: The current public identity is `SharedMemoryStore.SharedMemoryStore`,
which makes examples awkward and creates a namespace/type collision. Keeping the
package ID and root namespace as `SharedMemoryStore` preserves discoverability,
while renaming the concrete type to `MemoryStore` gives consumers a natural
usage shape:

```csharp
using SharedMemoryStore;

var status = MemoryStore.TryCreateOrOpen(options, out var store);
```

The package is still pre-broad-release, so a breaking correction is acceptable
when it is documented, covered by package-consumption tests, and released as the
production API contract step.

**Alternatives considered**:
- Keep the concrete type named `SharedMemoryStore`: rejected because the
  namespace/type collision remains.
- Move the type to `SharedMemoryStore.Core`: rejected because it adds a less
  natural namespace for the first API consumers use.
- Rename the namespace and keep the type: rejected because the package name and
  documentation already establish `SharedMemoryStore` as the root namespace.
- Add a broad facade interface instead of renaming the type: rejected because it
  does not solve example clarity and would add speculative abstraction.

## Decision: Remove general public retained writable `Memory<byte>` reservation access

**Rationale**: A retained `Memory<byte>` can be used after `Commit`, `Abort`,
`Dispose`, store disposal, or slot reuse. That violates the reservation
lifetime contract and can mutate committed or reused storage. The production
contract should expose writable reservation access through stack-scoped
`Span<byte>` views and explicit write/advance methods only. `Span<byte>` cannot
be stored on the heap by safe C# consumers, so it matches the intended "write
now, advance now" reservation lifecycle.

The basic API should not keep an advanced trusted writable-memory escape hatch
for the production release. If a future feature proves a need for retained
store-owned memory, it must be added as a separate advanced contract with clear
caller-owned lifetime risk and tests.

**Alternatives considered**:
- Keep plain `ValueReservation.GetMemory()` and document the hazard: rejected because
  basic examples could still retain a mutable handle that corrupts future data.
- Return `Memory<byte>` from a validating `MemoryManager`: rejected because
  validating every later `Span` or `Pin` against reservation lifecycle is subtle
  and keeps a dangerous primitive in the primary surface.
- Copy reservation bytes into private buffers on commit: rejected because it
  undermines the zero-copy ingest design and changes performance expectations.

## Decision: Add explicit wait policy and contention outcomes

**Rationale**: Public operations currently wait indefinitely on shared
synchronization. Production services need bounded behavior for request paths,
health checks, background workers, and shutdown. Introduce a `StoreWaitOptions`
value with timeout and cancellation fields, and add overloads for every public
operation family that can wait on the shared mutex or lifecycle gate.

Default public behavior should be bounded and documented. Consumers that want
legacy indefinite waiting must opt into an explicit infinite policy. Timed-out
or immediately contended operations return `StoreBusy`; canceled operations
return `OperationCanceled` for status-returning APIs. Open/create operations
use equivalent `StoreOpenStatus` outcomes.

**Alternatives considered**:
- Keep indefinite default waiting and only add optional overloads: rejected
  because the default package behavior would remain unsuitable for production
  callers that do not notice the overloads.
- Throw `TimeoutException` or `OperationCanceledException` from all APIs:
  rejected because the existing public shape uses deterministic status enums.
- Return `UnknownFailure` for wait failures: rejected because contention must be
  distinguishable from validation, capacity, lifecycle, and unexpected failure.

## Decision: Strengthen option validation and expose valid-by-construction helpers

**Rationale**: Consumers should not duplicate layout formulas or discover
invalid configuration late. Keep `SharedMemoryStoreOptions.CalculateRequiredBytes`
and add a valid-by-construction helper that derives `TotalBytes` from logical
capacities. Add public validation details for invalid names, invalid open modes,
missing or nonpositive capacities, inconsistent total bytes, and overflow. The
open path must reject undefined `OpenMode` values instead of silently matching a
fallback branch.

**Alternatives considered**:
- Keep only `StoreOpenStatus.InvalidOptions`: rejected because production
  configuration failures need actionable detail before opening a store.
- Make all options mutable and validated lazily: rejected because it preserves
  late failure and confusing configuration binding behavior.
- Hide layout sizing completely: rejected because advanced consumers still need
  deterministic capacity planning.

## Decision: Add `InvalidKey` for empty or null-equivalent keys

**Rationale**: Empty keys and oversized keys are different caller mistakes. The
current validation reports both as `KeyTooLarge`, which is misleading and
conflicts with the status taxonomy requirements. Add `InvalidKey` for empty
keys and keep `KeyTooLarge` for keys whose length exceeds the configured limit.

**Alternatives considered**:
- Treat empty key as not found: rejected because the key is invalid input, not a
  missing stored value.
- Keep `KeyTooLarge`: rejected because the name describes only one of the two
  conditions.

## Decision: Make diagnostics aggregate-first and prune misleading convenience names

**Rationale**: `DiagnosticsSnapshot.GetFailureCount(StoreStatus)` is stable and
works for current and future statuses. Per-status properties such as
`UnknownFailureFailures` and duplicated aliases such as `FailedCommitCount`
create brittle public names that will churn as statuses evolve. The production
API should keep capacity, lifecycle, index-health, and recovery summary
properties, but remove or obsolete per-status failure-count convenience members
that duplicate the aggregate API.

**Alternatives considered**:
- Keep all convenience names for source compatibility: rejected because this is
  the last low-cost time to remove clunky public API before the production
  contract.
- Replace aggregate access with a dictionary: rejected because it can allocate,
  exposes collection-shape compatibility concerns, and is less direct for
  metrics exporters.

## Decision: Keep core package free of hosting dependencies and avoid broad interfaces

**Rationale**: The concrete store remains the primary low-level API. Broad
interfaces that mirror every method would freeze implementation details and
would not match most application test boundaries. Optional hosting support can
be useful, but it must be a separate adapter package or sample with narrow
lifecycle and health behavior. The core package must remain usable without
Microsoft.Extensions dependencies.

**Alternatives considered**:
- Add `ISharedMemoryStore` to the core package: rejected because it mirrors the
  concrete type and exposes low-level lease and reservation details poorly for
  application mocks.
- Add hosting dependencies to the core package: rejected by the constitution and
  by the package's low-level cross-language direction.
- Skip all integration guidance: rejected because production consumers still
  need a documented pattern for health, graceful shutdown, and cleanup.
