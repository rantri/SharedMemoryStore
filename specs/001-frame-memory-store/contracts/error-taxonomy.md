# Error Taxonomy Contract

Expected operational outcomes are represented as status values. They must not
throw exceptions during hot-path operations.

## StoreOpenStatus

- `Success`
- `AlreadyExists`
- `NotFound`
- `InvalidOptions`
- `IncompatibleLayout`
- `UnsupportedPlatform`
- `InsufficientCapacity`
- `AccessDenied`
- `MappingFailed`

## StoreStatus

- `Success`
- `DuplicateKey`
- `NotFound`
- `KeyTooLarge`
- `ValueTooLarge`
- `DescriptorTooLarge`
- `StoreFull`
- `LeaseTableFull`
- `InvalidLease`
- `LeaseAlreadyReleased`
- `RemovePending`
- `UnsupportedPlatform`
- `StoreDisposed`
- `CorruptStore`
- `AccessDenied`
- `UnknownFailure`

## Timing Contract

After initialization and warm-up, documented expected failures must return
within 1 ms in the steady-state benchmark configuration:
- duplicate key.
- missing key.
- oversized value.
- oversized descriptor.
- full store.
- invalid release.
- unsupported platform detected at create/open.

## Diagnostics Contract

Every non-success status increments a caller-readable diagnostic counter. The
library returns structured status data and snapshots only; consumers choose how
to log, trace, or export metrics.

## Exception Contract

Exceptions are reserved for:
- invalid use of APIs outside documented contracts when a status cannot be
  returned safely.
- object disposal patterns where .NET conventions require exceptions.
- unexpected runtime failures during initialization before the store can return
  a stable status.

Expected store pressure and lookup outcomes use status values, not exceptions.
