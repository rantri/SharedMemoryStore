# SharedMemoryStore

SharedMemoryStore is a bounded, cross-process shared-memory key-value store for
opaque binary keys, descriptors, and payloads. Its .NET, C++, and Python
distributions implement the same lock-free SMS2 mapped protocol on Windows and
Linux x64.

The store is intentionally small in scope. It publishes named values, provides
zero-copy reservations and read leases, removes and reuses generations, exposes
explicit recovery, and reports bounded diagnostics. Queueing, routing,
subscriptions, persistence, serialization, and application schemas belong in
the application layer.

## Current releases and protocol

| Distribution | Version | Runtime surface |
|---|---:|---|
| NuGet `SharedMemoryStore` | `3.0.0` | `net10.0`, C# |
| CMake `SharedMemoryStore` | `1.0.0` | C++20 and C ABI `2.0` |
| Python `shared-memory-store` | `1.0.0` | Python 3.10+ over the packaged C ABI `2.0` library |

All three create and read exactly one shared protocol:

```text
magic=SMS2 layout=2.0 resource-protocol=2 required-features=7 optional-features=0
```

Package versions are independent from the mapped protocol identity. The
canonical byte layout, resource names, feature masks, and distribution matrix
live in [protocol/README.md](protocol/README.md) and
[protocol/compatibility.json](protocol/compatibility.json).

## Core properties

- Fixed capacities are chosen before creation: slots, value bytes, descriptor
  bytes, key bytes, lease records, and participant records.
- Every open handle owns one participant record. The default capacity is `64`;
  exhaustion returns `ParticipantTableFull` without disturbing existing
  handles.
- Publish, reserve, commit, acquire, release, remove, reclaim, recovery help,
  and diagnostics do not acquire a process-owned or store-wide OS lock.
- Cold create, open, participant registration, close, and final resource cleanup
  use bounded platform coordination.
- Keys are exact opaque byte sequences. Hashes select candidates, but equality
  always checks the complete key bytes.
- Leases and reservations are generation-fenced. Their mapped views are valid
  only while the exact token and store handle remain alive.
- The qualified atomic contract is little-endian x86-64 with naturally aligned
  lock-free 64-bit atomics.

## C# quick start

Install the package:

```powershell
dotnet add package SharedMemoryStore --version 3.0.0
```

Create options with the ordinary participant-aware helper:

```csharp
using SharedMemoryStore;

var options = SharedMemoryStoreOptions.Create(
    $"orders-{Guid.NewGuid():N}",
    slotCount: 128,
    maxValueBytes: 64 * 1024,
    maxDescriptorBytes: 64,
    maxKeyBytes: 64,
    leaseRecordCount: 256,
    participantRecordCount: 64,
    openMode: OpenMode.CreateNew,
    enableLeaseRecovery: true);

StoreOpenStatus opened = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
if (opened != StoreOpenStatus.Success || store is null)
{
    throw new InvalidOperationException($"Open failed: {opened}");
}

using (store)
{
    byte[] key = [0x01, 0x00, 0x02];
    byte[] value = [0x10, 0x00, 0x20];

    if (store.TryPublish(key, value) != StoreStatus.Success)
    {
        throw new InvalidOperationException("Publish failed.");
    }

    if (store.TryAcquire(key, out ValueLease lease) != StoreStatus.Success)
    {
        throw new InvalidOperationException("Acquire failed.");
    }

    using (lease)
    {
        Console.WriteLine(Convert.ToHexString(lease.ValueSpan));
    }

    StoreStatus removed = store.TryRemove(key);
    if (removed is not (StoreStatus.Success or StoreStatus.RemovePending))
    {
        throw new InvalidOperationException($"Remove failed: {removed}");
    }

    Console.WriteLine(store.ProtocolInfo); // (2, 0, 2, 7, 0)
}
```

`TryRemove` makes the key logically absent at its ordering point. It returns
`RemovePending` when a live lease or bounded cleanup still delays physical slot
reuse; a later release, remove, or allocation-pressure helper may finish the
reclamation.

## C++ quick start

Build or install the CMake package, then consume it with:

```cmake
find_package(SharedMemoryStore CONFIG REQUIRED)
target_link_libraries(my_app PRIVATE SharedMemoryStore::shared_memory_store)
```

```cpp
#include <shared_memory_store/store.hpp>
#include <array>

using namespace shared_memory_store;

auto options = store_options::create(
    "orders", 128, 64 * 1024, 64, 64, 256, 64,
    open_mode::create_or_open, true);

memory_store store;
if (memory_store::try_create_or_open(options, store) != open_status::success) {
    return 1;
}

const std::array<std::byte, 2> key{std::byte{1}, std::byte{2}};
const std::array<std::byte, 3> value{std::byte{7}, std::byte{8}, std::byte{9}};
if (store.try_publish(key, value) != status::success) return 2;

value_lease lease;
if (store.try_acquire(key, lease) != status::success) return 3;
auto borrowed = lease.value();
return lease.release() == status::success ? 0 : 4;
```

`memory_store`, `value_lease`, and `value_reservation` are move-only. Their
destructors are best-effort fallbacks; explicit close, release, and abort remain
the deterministic lifecycle operations.

## Python quick start

Build and install the platform wheel, then use the context-managed API:

```python
from shared_memory_store import MemoryStore, OpenMode, StoreOpenStatus, StoreOptions, StoreStatus

options = StoreOptions.create(
    "orders",
    slot_count=128,
    max_value_bytes=64 * 1024,
    max_descriptor_bytes=64,
    max_key_bytes=64,
    lease_record_count=256,
    participant_record_count=64,
    open_mode=OpenMode.CREATE_OR_OPEN,
    enable_lease_recovery=True,
)

open_status, store = MemoryStore.open(options)
if open_status is not StoreOpenStatus.SUCCESS or store is None:
    raise RuntimeError(f"open failed: {open_status}")

with store:
    if store.publish(b"order-1", b"payload\x00") is not StoreStatus.SUCCESS:
        raise RuntimeError("publish failed")
    acquire_status, lease = store.acquire(b"order-1")
    if acquire_status is not StoreStatus.SUCCESS or lease is None:
        raise RuntimeError(f"acquire failed: {acquire_status}")
    with lease:
        print(bytes(lease.value))
```

Python loads only the native library packaged beside its modules. A clean wheel
consumer must not depend on the repository source tree or `PYTHONPATH`.

## Waits, progress, and cancellation

Every operation accepts a no-wait, finite, or infinite policy. C# uses
`StoreWaitOptions`, C++ uses `wait_options`, and Python uses `WaitOptions`.
Finite budgets cover local retry, revalidation, helping, backoff, and any cold
lifecycle wait for that call. Cancellation wins only before the operation's
documented ordering point; it never rolls back an already published shared
result.

`StoreBusy` means the call exhausted its bounded progress budget. It is a
retryable contention result, not evidence of corruption and not proof that a
global lock was held.

## Recovery and diagnostics

Recovery is explicit and conservative. Enable it in the options, then call the
lease or reservation recovery API. A record is reclaimed only when its exact
participant incarnation and owner identity are safely stale. Live, changing,
unsupported, or inconsistent evidence is retained and reported.

Diagnostics report the immutable five-field protocol identity plus shared
capacity, slot, lease, reservation, participant, and directory facts. CAS
retries, helping, contention exhaustion, invalid/stale tokens, recovery
attempts, owner classifications, and status counts are local to the calling
runtime or handle unless documented otherwise.

See [docs/diagnostics.md](docs/diagnostics.md) for the complete distinction.

## Moving an existing deployment to SMS2

There is no in-place conversion and no current client reads a noncurrent mapping
to migrate it. Use application-owned authoritative data:

1. stop publishers and prevent new readers;
2. drain leases and reservations;
3. close every process-local handle;
4. remove or replace the old physical store;
5. create a fresh SMS2 store with the intended participant-aware capacities;
6. republish authoritative values; and
7. start current clients.

A side-by-side cutover uses a distinct public store name. An incompatible or
malformed existing mapping returns `IncompatibleLayout` before payload access.

## Build and validation

Managed:

```powershell
dotnet build SharedMemoryStore.slnx -c Release
dotnet test SharedMemoryStore.slnx -c Release
pwsh ./scripts/validate-package-consumption.ps1 -Configuration Release
```

Native and Python:

```powershell
pwsh ./scripts/validate-native.ps1 -Configuration Release
pwsh ./scripts/validate-python.ps1 -Configuration Release
```

The repository also contains a deterministic protocol manifest, nine ordered
runtime-pair tests, raw visibility and crash checkpoints, package-consumer
tests, and Windows/Linux qualification gates.

## Documentation and samples

- [Documentation index](docs/index.md)
- [Getting started](docs/getting-started.md)
- [Concepts](docs/concepts.md)
- [Byte encoding](docs/byte-encoding.md)
- [Usage and lifetimes](docs/usage.md)
- [Statuses and recovery](docs/errors.md)
- [Diagnostics](docs/diagnostics.md)
- [Lifecycle](docs/lifecycle.md)
- [Integration](docs/integration.md)
- [Performance scope](docs/performance.md)
- [Architecture](docs/architecture.md)
- [Packaging](docs/packaging.md)
- [Portability](docs/portability.md)
- [Examples](docs/examples.md)
- [Sample catalog](docs/samples.md)
- [Maintainer guide](docs/maintainers.md)
- [Release and migration notes](docs/releases.md)
- [Changelog](CHANGELOG.md)
- [C# basic usage](samples/BasicUsage/README.md)
- [Frame value](samples/FrameValue/README.md)
- [Zero-copy ingest](samples/ZeroCopyIngest/README.md)
- [Hosted integration](samples/HostedServiceIntegration/README.md)
- [C++ basic usage](samples/CppBasicUsage/README.md)
- [Python basic usage](samples/PythonBasicUsage/README.md)
- [Docker shared-memory validation](samples/DockerSharedMemory/README.md)
- [Broker-key sample](samples/LockFreeBrokerKeys/README.md)

## License and project policy

SharedMemoryStore is licensed under the [MIT License](LICENSE). See
[CONTRIBUTING.md](CONTRIBUTING.md), [SUPPORT.md](SUPPORT.md), and
[SECURITY.md](SECURITY.md) before contributing or reporting an issue.
