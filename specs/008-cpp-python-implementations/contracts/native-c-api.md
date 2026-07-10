# Contract: Native C ABI

## ABI Rules

- Exported symbols use `extern "C"` and the platform's ordinary C calling
  convention.
- All integer fields use explicit-width C types.
- Every extensible input/output structure begins with `struct_size` and
  `abi_version`.
- Stores, leases, and reservations are opaque pointers owned by matching close
  or release functions.
- Byte pointers are paired with explicit lengths and may contain NUL bytes.
- A null pointer is allowed only when its paired length is zero or the parameter
  is explicitly optional.
- No exception, C++ standard-library type, allocator ownership, or thread-local
  error string crosses the ABI.
- Every operation returns the exact public status enum; optional diagnostics are
  returned through caller-allocated versioned structures.

## Required Symbol Groups

### Version and layout

- Query ABI and shared protocol versions.
- Calculate required mapped bytes with overflow detection.
- Return canonical record sizes and offsets for conformance tests.

### Store lifecycle

- Create/open from versioned options and bounded wait configuration.
- Close an opaque handle exactly once; null close is harmless.
- Query options/layout and diagnostics without exposing internal pointers.

### Values and leases

- Publish one contiguous payload and optional descriptor.
- Publish an array of byte segments as one committed payload.
- Acquire by key into an opaque lease.
- Query lease validity, descriptor pointer/length, and payload pointer/length.
- Release the lease with a status and destroy its process-local token.
- Remove a key with pending-removal behavior.

### Reservations

- Reserve announced payload length and immutable descriptor.
- Query validity, total, written, remaining, and writable pointer/length.
- Advance by an exact byte count, commit, or abort.
- Destroying a live reservation performs best-effort abort.

### Recovery

- Recover stale/current-process leases according to an explicit option and
  return scanned, recovered, active, unsupported, and failed counts.
- Recover pending reservations under the equivalent explicit policy.

## Threading and Lifetime

One store handle is safe for concurrent calls. All shared mutations and lease
registry changes participate in the common platform lock. A byte pointer returned
from a lease remains valid only while that lease and store are live. A writable
reservation pointer remains valid only until the next reservation operation,
completion, recovery, identity change, or store close.
