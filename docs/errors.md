# Statuses and failure handling

SharedMemoryStore reports expected outcomes as stable numeric status values.
Ordinary contention, capacity, cancellation, duplicate keys, missing keys, and
lifecycle races are not exceptions and do not poison the shared store.

The .NET, C ABI, C++, and Python surfaces use the same numeric assignments.
Language wrappers may use different casing, but must not translate one status
into another.

The assignments and fail-closed rules are normative in the
[public API](../specs/010-lock-free-only-multilang/contracts/public-api.md) and
[protocol conformance](../specs/010-lock-free-only-multilang/contracts/protocol-conformance.md)
contracts.

## Open statuses

| Code | Status | Meaning | Typical response |
|---:|---|---|---|
| 0 | `Success` | The handle opened and owns an active participant record. | Use the store. |
| 1 | `AlreadyExists` | `CreateNew` found an existing physical store. | Open it deliberately or choose another name. |
| 2 | `NotFound` | `OpenExisting` found no physical store. | Create it or fix the public name/scope. |
| 3 | `InvalidOptions` | A name, capacity, mode, or recovery option is invalid. | Correct caller input. |
| 4 | `IncompatibleLayout` | Existing bytes or requested dimensions do not match canonical SMS2. | Stop; use matching options or replace the deployment. |
| 5 | `UnsupportedPlatform` | The host lacks a required mapping, atomic, lock, or owner-evidence capability. | Move to a qualified host or disable the workflow. |
| 6 | `InsufficientCapacity` | `TotalBytes` cannot contain the requested layout. | Use `Create`/`calculate_required_bytes`. |
| 7 | `AccessDenied` | OS permissions prevent resource access. | Fix identity, scope, directory, or container permissions. |
| 8 | `MappingFailed` | The OS mapping could not be created or attached. | Inspect platform error evidence and resource health. |
| 9 | `StoreBusy` | Cold lifecycle coordination or participant claim exceeded the wait budget. | Retry according to caller policy. |
| 10 | `OperationCanceled` | Cancellation won before the handle became active. | Treat as caller cancellation. |
| 11 | `ParticipantTableFull` | No participant record can be safely claimed. | Close unused handles or recreate with more records. |

### Why `IncompatibleLayout` fails closed

An opener validates magic, layout and resource identities, feature masks,
header length, total bytes, capacities, section offsets and lengths, record
strides, store control, and observed states before projecting mapped data.

A noncurrent, unknown, truncated, zero, or malformed mapping is never treated
as an empty current store. The opener returns `IncompatibleLayout` without
reading key, descriptor, or payload bytes and without creating a parallel
mapping for the same public name.

## Operation statuses

| Code | Status | Meaning | Retry guidance |
|---:|---|---|---|
| 0 | `Success` | The documented ordering and cleanup work completed. | None. |
| 1 | `DuplicateKey` | The exact key is published, pending removal, or reserved. | Retry only after that lifecycle ends. |
| 2 | `NotFound` | No published value matches the exact key. | Publish it or treat it as absent. |
| 3 | `KeyTooLarge` | Key length exceeds the configured maximum. | Fix caller input or recreate with a larger maximum. |
| 4 | `ValueTooLarge` | Value length exceeds the configured maximum. | Split externally or recreate with a larger maximum. |
| 5 | `DescriptorTooLarge` | Descriptor length exceeds the configured maximum. | Reduce it or recreate. |
| 6 | `StoreFull` | No reusable slot generation is available. | Finish removals/releases or increase capacity. |
| 7 | `LeaseTableFull` | No reusable lease record is available. | Release leases or increase lease capacity. |
| 8 | `InvalidLease` | The token is malformed or does not match an active incarnation. | Do not retry that token. |
| 9 | `LeaseAlreadyReleased` | The exact lease lifetime already ended. | Stop using its views. |
| 10 | `RemovePending` | The key is logically absent; physical reclamation is still protected or bounded. | Release readers or let later helpers finish reclamation. |
| 11 | `UnsupportedPlatform` | The requested operation or owner classification is unavailable. | Do not assume an owner stale. |
| 12 | `StoreDisposed` | The process-local handle is closed or closing. | Open a new handle if needed. |
| 13 | `CorruptStore` | Revalidated shared state is structurally impossible or unsafe. | Stop using the store and preserve evidence. |
| 14 | `AccessDenied` | OS access failed during the operation. | Fix permissions; do not reinterpret as contention. |
| 15 | `UnknownFailure` | An unexpected runtime failure was contained. | Capture diagnostics and platform evidence. |
| 16 | `InvalidReservation` | The token does not match a pending slot generation. | Do not use its view. |
| 17 | `ReservationIncomplete` | Commit was attempted before exact announced progress. | Finish writing/advancing or abort. |
| 18 | `ReservationAlreadyCompleted` | Commit, abort, recovery, disposal, or close already ended the token. | Stop using its view. |
| 19 | `ReservationWriteOutOfRange` | Requested progress exceeds the announced payload. | Fix producer accounting. |
| 20 | `InvalidKey` | The key is empty or otherwise invalid. | Fix caller input. |
| 21 | `StoreBusy` | Bounded local retry, revalidation, helping, scan, or backoff did not order. | Retry according to caller policy. |
| 22 | `OperationCanceled` | Cancellation won before the operation's ordering point. | Treat as caller cancellation. |

## Capacity is not corruption

`StoreFull`, `LeaseTableFull`, and `ParticipantTableFull` report bounded
capacity. They never latch the store corrupt. A full slot table may contain
published values, pending reservations, generations protected by leases,
reclaiming records, or deliberately retired generations. Use diagnostics to
distinguish them.

`StoreBusy` is also nonterminal. Lock-free system-wide progress does not promise
that every caller finishes within a no-wait or finite budget.

## Duplicate, remove, and reuse races

Key absence and physical reuse are different facts:

- `RemovePending` proves the key is logically absent.
- Existing leases may continue reading their exact generation.
- New acquires return `NotFound` after logical removal.
- A new publish of that key may still see `DuplicateKey` until directory and
  slot reclamation safely complete.

This is an expected lifecycle race. Do not delete files, rewrite mapped state,
or classify it as corruption.

## Token failures

Lease and reservation tokens carry exact-incarnation evidence. The store checks
the store identity, participant incarnation, record index/incarnation, slot
index/generation, and relevant lengths before accessing a view or mutating
shared state.

`InvalidLease`, `InvalidReservation`, `LeaseAlreadyReleased`, and
`ReservationAlreadyCompleted` protect later reused generations. Once a token
ends, all direct and derived views from that token are invalid for application
use.

## Recovery outcomes

Recovery reports record-level classifications in addition to its operation
status:

- recovered: the exact stale incarnation was reclaimed;
- active: a current or live owner must be retained;
- unsupported: the host could not prove liveness safely;
- failed/inconsistent: state was unsafe to mutate; and
- changing: the record changed during classification and was not reclaimed.

Unsupported or ambiguous evidence is conservative. Never treat it as stale.
Recovery disabled in the store options returns `UnsupportedPlatform` with an
empty report when no scan is performed.

## Cancellation ordering

Cancellation affects only work that has not crossed its documented ordering
point. For example, publication visible to readers is not rolled back because a
token is signaled during bounded post-publication cleanup. The call returns the
status that describes the shared result.

C++ and Python cancellation handles are process-local and must remain alive
until every call borrowing them returns. C# `CancellationToken` follows the
same synchronous-call lifetime rule through `StoreWaitOptions`.

## Responding to `CorruptStore`

Corruption is latched only after an impossible observation is revalidated. Once
latched, operations fail closed rather than guessing at payload ownership.

Recommended response:

1. stop new work and keep the physical resources for investigation;
2. capture protocol identity, diagnostics, process identities, host/container
   namespace, permissions, and recent crash/recovery events;
3. do not patch mapped bytes in place;
4. restore values from an application-owned authoritative source into a fresh
   SMS2 store; and
5. investigate unsupported architectures, malicious writers, unsafe external
   mapping access, or implementation defects.

## Deployment replacement

An incompatible existing store is not a readable migration source. Stop work,
drain tokens, close every handle, remove or replace the physical store, create a
fresh SMS2 store with matching capacities, and republish authoritative values.
Use another public name for side-by-side operation.

## Related guides

- [Usage](usage.md)
- [Diagnostics](diagnostics.md)
- [Portability](portability.md)
- [Release migration](releases.md)
