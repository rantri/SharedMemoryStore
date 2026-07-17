# Public API Contract

## Compatibility boundary

This feature is a deliberate breaking release. Every current distribution
creates and reads only mapped layout `2.0` (`SMS2`), resource protocol `2`, and
required-feature mask `7`. No current API exposes a profile, legacy-layout
selector, automatic layout detection that changes the requested behavior, or
fallback implementation.

The following numeric identities are independent of package versions:

| Identity | Value |
|---|---:|
| Mapped layout | `2.0` |
| Magic | `SMS2` |
| Resource protocol | `2` |
| Required features | `7` |
| Optional features | `0` |
| C ABI | `2.0` (`0x00020000`) |

An existing `SMS1` mapping, an unknown major layout, an incompatible required
feature mask, or a malformed `SMS2` header returns
`IncompatibleLayout` before directory, key, descriptor, payload, slot, lease,
or participant projection. The implementation never creates a parallel mapping
for the same public name.

## Common behavioral surface

All three language surfaces provide equivalent operations:

- calculate exact required mapped bytes;
- create-new, open-existing, or create-or-open a named store;
- publish one contiguous payload or an ordered sequence of segments;
- reserve an announced payload length, project writable memory, advance exact
  progress, commit, or abort;
- acquire a zero-copy read lease, project immutable descriptor and payload
  bytes, and release the lease;
- logically remove a key and cooperatively reclaim its generation;
- explicitly recover eligible abandoned leases and reservations;
- obtain protocol, capacity, participant, directory, contention, helping,
  recovery, and terminal-state diagnostics; and
- close or dispose process-local handles and tokens idempotently.

Keys, descriptors, and payloads are opaque byte sequences and may contain NUL.
Keys must be non-empty. Implementations use the canonical hash only to select
directory candidates and always confirm exact key bytes.

No distribution adds queue, broker, subscriber, acknowledgement, persistence,
or application-schema behavior.

## C# API

### Removed surface

The following public symbols are removed:

- `StoreProfile`;
- `SharedMemoryStoreOptions.Profile`;
- `MemoryStore.Profile`;
- `SharedMemoryStoreOptions.CreateLockFree(...)`; and
- the profile-aware `CalculateRequiredBytes(StoreProfile, ...)` overload.

There is no obsolete compatibility shim. Code that referenced those symbols
must migrate to the ordinary single-protocol helpers.

### Options and sizing

```csharp
namespace SharedMemoryStore;

public sealed class SharedMemoryStoreOptions
{
    public string Name { get; init; }
    public OpenMode OpenMode { get; init; }
    public long TotalBytes { get; init; }
    public int SlotCount { get; init; }
    public int MaxValueBytes { get; init; }
    public int MaxDescriptorBytes { get; init; }
    public int MaxKeyBytes { get; init; }
    public int LeaseRecordCount { get; init; }
    public int ParticipantRecordCount { get; init; } = 64;
    public bool EnableLeaseRecovery { get; init; }

    public static long CalculateRequiredBytes(
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount = 64);

    public static SharedMemoryStoreOptions Create(
        string name,
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount = 64,
        OpenMode openMode = OpenMode.CreateOrOpen,
        bool enableLeaseRecovery = false);
}
```

The ordinary helper always calculates and selects canonical layout `2.0`.
`SlotCount` and `ParticipantRecordCount` are each in `1..1,048,575`. Every
successfully opened handle owns one participant record before it may claim a
slot or lease. Participant exhaustion returns `ParticipantTableFull` without
changing existing handles.

### Protocol identity

```csharp
public readonly record struct StoreProtocolInfo(
    int LayoutMajorVersion,
    int LayoutMinorVersion,
    int ResourceProtocolVersion,
    ulong RequiredFeatures,
    ulong OptionalFeatures);

public sealed class MemoryStore : IDisposable
{
    public StoreProtocolInfo ProtocolInfo { get; }
}
```

`ProtocolInfo` is immutable for the handle and may be read without entering a
mapped operation. For this release it is exactly `(2, 0, 2, 7, 0)`.

All existing key-value, reservation, lease, recovery, wait, diagnostics, and
disposal method names remain the C# workflow surface, subject to the canonical
lock-free ordering and lifetime rules below.

## Native C ABI 2.0

### ABI rules

- `SMS_C_ABI_VERSION` is `0x00020000`.
- Exported functions use `extern "C"` and the platform C calling convention.
- ABI integers use fixed-width C types.
- Every extensible structure begins with `uint32_t struct_size` and
  `uint32_t abi_version`.
- ABI 2 functions reject a structure that is too small for the ABI 2 required
  prefix or whose ABI major differs.
- Byte pointers always carry `uint64_t` lengths. A pointer may be null only
  when its length is zero or the parameter is explicitly optional.
- Store, reservation, and lease tokens are opaque process-local handles. No C++
  standard-library type, exception, allocator ownership, mapped record pointer,
  or language-runtime object crosses the ABI.
- `sms_close_store` is thread-safe and idempotent. It rejects new calls, waits
  for the one logical teardown already in progress, and leaves the opaque
  allocation available for deterministic closed-status calls.
- `sms_destroy_store` releases that opaque allocation after logical close. It
  is caller-synchronized: no thread may enter any store ABI, including close,
  with the pointer once destruction begins.
- Every failing operation returns a canonical numeric status. Exceptions are
  contained and converted at the boundary.

ABI 1 clients and ABI 2 libraries are intentionally incompatible.

### Version, protocol, and layout queries

ABI 2 provides functions to:

- return the ABI version;
- return layout `2.0`, resource protocol `2`, required features `7`, optional
  features `0`, and the canonical record sizes;
- return every canonical shared-field offset used by conformance tests;
- calculate required bytes from slot, key, descriptor, payload, lease, and
  participant capacities; and
- query the validated layout dimensions and section offsets of an open handle.

The protocol query describes the `512`-byte store header, `64`-byte participant
record, `128`-byte primary bucket, `8`-byte overflow binding, `64`-byte lease
record, and `128`-byte value-slot record. The layout query includes participant,
primary directory, overflow directory, lease registry, slot metadata, key
storage, descriptor storage, and payload storage offsets, lengths, and strides.

### Store options

The ABI 2 store-options structure contains:

- UTF-8 public name and length;
- open mode;
- total mapped bytes;
- slot, value, descriptor, key, lease, and participant capacities; and
- the explicit recovery-enable flag.

`participant_record_count` is required and positive. Language helpers default
it to `64`; the ABI does not infer a missing field from an ABI 1 structure.

### Cancellation ownership

ABI 2 defines an opaque `sms_cancellation` process-local handle and matching
create, signal, query, and destroy functions. `sms_wait_options` may carry a
borrowed pointer to one cancellation handle in addition to its timeout.

- Creating the handle returns one caller-owned unsignaled source.
- Signaling is thread-safe, idempotent, and makes later budget checks observe
  cancellation with acquire semantics.
- A wait-options value borrows the handle only for the synchronous native call;
  the engine never persists it in mapped memory or beyond return.
- The caller must keep the handle alive until every call borrowing it returns.
- Destroying a handle while a call still borrows it is invalid caller behavior;
  language wrappers prevent that lifetime race.
- Cancellation affects only pre-ordering or bounded-cleanup paths documented by
  the canonical operation. It does not roll back an already ordered shared
  result and never latches corruption.

The C++ wrapper exposes a move-only cancellation source plus a non-owning token
used by `wait_options`. Python exposes a context-managed `CancellationSource`;
its `WaitOptions` holds a strong local reference while a call is entered. C#
continues to use its existing `CancellationToken`-backed wait options. These
objects are local adapters and have no shared protocol representation.

### Required symbol groups

ABI 2 retains the recognizable symbol groups for:

- store open, logical close, caller-synchronized handle destruction, layout
  query, and diagnostics;
- contiguous and segmented publication;
- acquire, lease validation, immutable byte projection, release, and token
  destruction;
- remove;
- reserve, reservation validation, lengths, writable projection, advance,
  commit, abort, and token destruction; and
- explicit lease and reservation recovery.

Changing to ABI 2 does not add public functions for participant or directory
mutation. Those are internal protocol mechanisms owned by a store handle.

Opaque reservation handles fence store ID, participant incarnation, slot
binding, and announced payload length. Opaque lease handles fence store ID,
participant incarnation, slot binding, and lease-record incarnation. Destroying
a live reservation performs a bounded best-effort abort; destroying a lease
performs its documented local cleanup without releasing a later incarnation.

## C++20 RAII API

Namespace `shared_memory_store` remains a typed wrapper over ABI 2.

```cpp
struct store_options {
    std::string name;
    open_mode mode{open_mode::create_or_open};
    std::int64_t total_bytes{};
    std::int32_t slot_count{};
    std::int32_t max_value_bytes{};
    std::int32_t max_descriptor_bytes{};
    std::int32_t max_key_bytes{};
    std::int32_t lease_record_count{};
    std::int32_t participant_record_count{64};
    bool enable_lease_recovery{};

    static std::int64_t calculate_required_bytes(
        std::int32_t slots,
        std::int32_t max_value,
        std::int32_t max_descriptor,
        std::int32_t max_key,
        std::int32_t leases,
        std::int32_t participants = 64);

    static store_options create(
        std::string name,
        std::int32_t slots,
        std::int32_t max_value,
        std::int32_t max_descriptor,
        std::int32_t max_key,
        std::int32_t leases,
        std::int32_t participants = 64,
        open_mode mode = open_mode::create_or_open,
        bool recovery = false);
};
```

The public `protocol_info` value contains layout major/minor, resource protocol,
and required/optional feature masks. `memory_store` exposes the immutable
protocol identity of a successful handle and the existing publish, segmented
publish, reserve, acquire, remove, recovery, and diagnostics workflows.

`memory_store`, `value_lease`, and `value_reservation` are move-only. Their
destructors are non-throwing and best-effort; explicit release, abort, and close
remain the deterministic lifecycle operations. Returned `std::span` values are
borrowed and must not be used after the exact lease/reservation lifetime ends.

One native store handle supports concurrent entered operations. Process-local
lifetime coordination rejects new calls during logical close and waits for
already entered calls. Concurrent close callers observe the same completed
teardown. C++ keeps a shared local control alive across operation snapshots,
calls logical close before detaching the public wrapper, and invokes
caller-synchronized handle destruction only after the final snapshot ends.
None of this coordination becomes mapped or process-wide operation
synchronization.

## Python 3 API

Package `shared_memory_store` remains a standard-library-only Python facade over
the ABI 2 native library bundled beside the package modules. Python never
implements shared-memory compare/exchange itself.

### Constants and value types

The package exports:

- `ABI_VERSION == 0x00020000`;
- `LAYOUT_MAJOR_VERSION == 2`;
- `LAYOUT_MINOR_VERSION == 0`;
- `RESOURCE_PROTOCOL_VERSION == 2`;
- `REQUIRED_FEATURES == 7` and `OPTIONAL_FEATURES == 0`;
- `OpenMode`, `StoreOpenStatus`, and `StoreStatus` with canonical numeric
  assignments;
- immutable `WaitOptions`, `StoreOptions`, `ProtocolInfo`, recovery reports,
  and `DiagnosticsSnapshot`; and
- `MemoryStore`, `ValueLease`, `ValueReservation`,
  `CancellationSource`, `calculate_required_bytes`, and `native_library_path`.

`RESOURCE_NAMING_VERSION` is retired in favor of the semantically accurate
`RESOURCE_PROTOCOL_VERSION` name.

### Options and sizing

```python
def calculate_required_bytes(
    *,
    slot_count: int,
    max_value_bytes: int,
    max_descriptor_bytes: int,
    max_key_bytes: int,
    lease_record_count: int,
    participant_record_count: int = 64,
) -> int: ...

@dataclass(frozen=True, slots=True, kw_only=True)
class StoreOptions:
    name: str
    total_bytes: int
    slot_count: int
    max_value_bytes: int
    max_descriptor_bytes: int
    max_key_bytes: int
    lease_record_count: int
    participant_record_count: int = 64
    open_mode: OpenMode = OpenMode.CREATE_OR_OPEN
    enable_lease_recovery: bool = False

    @classmethod
    def create(cls, name: str, *, ..., participant_record_count: int = 64,
               open_mode: OpenMode = OpenMode.CREATE_OR_OPEN,
               enable_lease_recovery: bool = False) -> "StoreOptions": ...
```

### Ownership surface

- `MemoryStore.open(options, wait=...)` returns `(StoreOpenStatus, store | None)`.
- Stores, leases, and reservations are context managers with idempotent
  `close()` methods.
- Store methods return explicit status values and optional owning tokens rather
  than raising for ordinary store outcomes.
- `ValueLease.value` and `.descriptor` are read-only zero-copy `memoryview`
  objects.
- `ValueReservation.buffer()`/`.view` returns writable zero-copy memory for the
  exact remaining reservation range.
- Direct views tracked by a wrapper are released when that wrapper is released,
  completed, recovered, or closed. Caller-created derived views remain subject
  to the same logical lifetime and must not be retained afterward.
- Finalizers are best-effort fallbacks, not the primary ownership contract.

The loader resolves only `shared_memory_store.dll` on Windows or
`libshared_memory_store.so` on Linux from the installed package. It verifies ABI
major `2`, layout `2.0`, resource protocol `2`, required features `7`, and the
canonical record sizes/offsets before returning the library. It never searches
the working directory, `PATH`, or a system library path.

Python may use narrow process-local locking or operation-entry accounting to
coordinate close and borrowed views. It must not implement shared synchronization
or serialize foreign participants; all mapped atomics execute in the native ABI
implementation.

## Status equivalence

Existing meaningful numeric assignments remain stable across C#, C, C++, and
Python.

### Open statuses

| Value | Status | Canonical use |
|---:|---|---|
| 0 | `Success` | Handle is active and owns one participant incarnation. |
| 1 | `AlreadyExists` | `CreateNew` found any existing mapping under the same name. |
| 2 | `NotFound` | `OpenExisting` found no live mapping. |
| 3 | `InvalidOptions` | Caller options or dimensions are invalid. |
| 4 | `IncompatibleLayout` | Retired, unknown, malformed, feature-incompatible, or dimension-mismatched mapping. |
| 5 | `UnsupportedPlatform` | Architecture or required platform mechanism is unsupported. |
| 6 | `InsufficientCapacity` | Total bytes cannot contain the requested canonical layout. |
| 7 | `AccessDenied` | Required resource access is denied. |
| 8 | `MappingFailed` | Mapping failed for another deterministic platform reason. |
| 9 | `StoreBusy` | Cold coordination or participant claim did not finish within the bound. |
| 10 | `OperationCanceled` | Open was canceled before a handle became active. |
| 11 | `ParticipantTableFull` | No reusable participant record exists for the new handle. |

### Operation statuses

`StoreStatus` values `0..22` retain their existing assignments. In particular:

- `DuplicateKey` is returned only for an exact ordered reservation or visible
  generation, never merely for a tentative atomic publication;
- `StoreFull` and `LeaseTableFull` require stable canonical capacity proofs;
- `RemovePending` means the key is logically absent while protection or bounded
  cleanup remains;
- `StoreBusy` means the caller's retry/help/revalidation budget expired;
- `OperationCanceled` applies only before the workflow ordering point; and
- `CorruptStore` is reserved for revalidated persistent structural corruption,
  not caller error, capacity, contention, cancellation, or a legal race.

Unknown native operation-status values map to the language's documented unknown
failure result. Unknown open-status values do not produce a handle.

## Ordering and lifetime equivalence

| Workflow | Public ordering point | Borrowed-memory lifetime |
|---|---|---|
| Explicit reserve | Exact `Initializing -> Reserved` CAS | Writable projection ends on commit, abort, recovery, token close, or store close. |
| Advance | Successful exact `BytesAdvanced` CAS | Previously returned writable projection ends at the next reservation lifecycle action. |
| Commit | Exact `Reserved -> Published` CAS | No reservation projection remains; immutable value becomes acquirable. |
| Contiguous/segmented publish | Exact internal `Reserved -> Published` CAS | Input bytes are caller-owned; no partial value becomes visible. |
| Acquire | Active lease publication followed by exact binding and `Published` revalidation | Descriptor/payload projection lasts through exact lease release/recovery or store close, including logical removal. |
| Remove | Exact `Published -> RemoveRequested` CAS | New acquires fail; existing valid lease projections remain protected. |
| Release | Exact `Active -> Releasing` lease CAS | That lease's projection ends immediately and cannot protect later reuse. |

Every public token fences the store incarnation and exact reusable-record
incarnations. A stale copy cannot project or mutate a later generation. Spans,
memory objects, pointers, and memoryviews are borrowed capabilities, not
serializable or cross-process tokens. No language can revoke an unsafe raw
pointer already retained contrary to the contract; callers must stop using it
at the documented lifetime boundary.

Close rejects new local operations, completes or boundedly hands off exact
owned state, retires the handle's participant incarnation, invalidates local
tokens/views, and unmaps only that handle. Other live handles continue.
