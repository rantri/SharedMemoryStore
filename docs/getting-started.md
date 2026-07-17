# Getting started

Choose the language package that fits the caller. The .NET, C++, and Python
surfaces are independently versioned, but all of them create and read the same
SMS2 layout `2.0` and resource protocol `2`.

| Caller | Distribution | Current version | Primary store type |
|---|---|---:|---|
| C# | NuGet `SharedMemoryStore` | `3.0.0` | `MemoryStore` |
| C++ | CMake `SharedMemoryStore` | `1.0.0` | `shared_memory_store::memory_store` |
| Python | wheel `shared-memory-store` | `1.0.0` | `shared_memory_store.MemoryStore` |

The native and Python packages use C ABI `2.0`. Package versions do not select
the mapped layout.

## Prerequisites

- Windows x64 or Linux x64 on a little-endian host.
- .NET SDK compatible with `net10.0` for C#.
- CMake 3.20+ and a C++20 compiler for C++ or a source-built Python wheel.
- Python 3.10+ and a PEP 517 frontend such as `build` for Python packaging.
- All processes that share a public store name must use identical capacity and
  recovery options.

Every successful handle reports protocol identity `(2, 0, 2, 7, 0)`:
layout major/minor, resource protocol, required features, optional features.

## C#

Install the package:

```powershell
dotnet new console -n SmsQuickstart
dotnet add SmsQuickstart package SharedMemoryStore --version 3.0.0
```

Use ordinary participant-aware creation:

```csharp
using SharedMemoryStore;

SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
    "quickstart",
    slotCount: 16,
    maxValueBytes: 4096,
    maxDescriptorBytes: 64,
    maxKeyBytes: 64,
    leaseRecordCount: 32,
    participantRecordCount: 8,
    openMode: OpenMode.CreateOrOpen,
    enableLeaseRecovery: true);

StoreOpenStatus open = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
if (open != StoreOpenStatus.Success || store is null)
{
    throw new InvalidOperationException($"Open failed: {open}");
}

using (store)
{
    byte[] key = "frame-1"u8.ToArray();
    byte[] value = [1, 0, 2, 0, 3];

    StoreStatus publish = store.TryPublish(key, value);
    if (publish != StoreStatus.Success)
    {
        throw new InvalidOperationException($"Publish failed: {publish}");
    }

    StoreStatus acquire = store.TryAcquire(key, out ValueLease lease);
    if (acquire != StoreStatus.Success)
    {
        throw new InvalidOperationException($"Acquire failed: {acquire}");
    }

    using (lease)
    {
        Console.WriteLine(Convert.ToHexString(lease.ValueSpan));
    }

    Console.WriteLine(store.TryRemove(key));
    Console.WriteLine(store.ProtocolInfo);
}
```

Run the repository sample with:

```powershell
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
```

## C++

Configure, build, and install the package from a checkout:

```powershell
cmake -S . -B artifacts/native -DSMS_BUILD_TESTS=ON -DSMS_BUILD_SAMPLES=ON -DSMS_INSTALL=ON
cmake --build artifacts/native --config Release
ctest --test-dir artifacts/native -C Release --output-on-failure
cmake --install artifacts/native --config Release --prefix artifacts/native-install
```

An external consumer uses the installed config package:

```cmake
find_package(SharedMemoryStore 1.0 CONFIG REQUIRED)
target_link_libraries(my_app PRIVATE SharedMemoryStore::shared_memory_store)
```

Minimal C++:

```cpp
#include <shared_memory_store/store.hpp>
#include <array>

using namespace shared_memory_store;

auto options = store_options::create(
    "quickstart", 16, 4096, 64, 64, 32, 8,
    open_mode::create_or_open, true);

memory_store store;
if (memory_store::try_create_or_open(options, store) != open_status::success) {
    return 1;
}

const std::array<std::byte, 2> key{std::byte{1}, std::byte{2}};
const std::array<std::byte, 3> value{std::byte{3}, std::byte{4}, std::byte{5}};
if (store.try_publish(key, value) != status::success) return 2;

value_lease lease;
if (store.try_acquire(key, lease) != status::success) return 3;
auto view = lease.value();
return lease.release() == status::success ? 0 : 4;
```

The borrowed span ends with the lease lifetime. Explicitly release tokens and
close stores when the outcome matters; destructors are best-effort cleanup.

## Python

Build and install a wheel into a clean environment:

```powershell
python -m pip install build
python -m build --wheel
python -m venv artifacts/python-consumer
artifacts/python-consumer/Scripts/python -m pip install (Get-ChildItem dist/*.whl | Select-Object -First 1)
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
```

On Linux use `artifacts/python-consumer/bin/python`. The installed package loads
only its adjacent `shared_memory_store.dll` or `libshared_memory_store.so`; do
not add `src/python` to `PYTHONPATH` for a package-consumer test.

Minimal Python:

```python
from shared_memory_store import MemoryStore, OpenMode, StoreOpenStatus, StoreOptions, StoreStatus

options = StoreOptions.create(
    "quickstart",
    slot_count=16,
    max_value_bytes=4096,
    max_descriptor_bytes=64,
    max_key_bytes=64,
    lease_record_count=32,
    participant_record_count=8,
    open_mode=OpenMode.CREATE_OR_OPEN,
    enable_lease_recovery=True,
)

opened, store = MemoryStore.open(options)
if opened is not StoreOpenStatus.SUCCESS or store is None:
    raise RuntimeError(f"open failed: {opened}")

with store:
    if store.publish(b"frame-1", b"\x01\x00\x02") is not StoreStatus.SUCCESS:
        raise RuntimeError("publish failed")
    acquired, lease = store.acquire(b"frame-1")
    if acquired is not StoreStatus.SUCCESS or lease is None:
        raise RuntimeError(f"acquire failed: {acquired}")
    with lease:
        print(bytes(lease.value))
```

`memoryview` objects from leases and reservations are zero-copy borrowed views.
Do not retain direct or derived views after the owning token or store ends.

## Sharing one store across languages

Every participant must agree on:

- public name and `OpenMode` intent;
- total bytes and every configured capacity;
- participant-record count;
- recovery enablement and host namespace assumptions; and
- the canonical little-endian byte encoding chosen by the application.

The library treats keys, descriptors, and payloads as bytes. It does not encode
integers, text, schemas, or ownership metadata for the application. Embedded
NUL bytes are valid.

## Common first-use outcomes

- `AlreadyExists`: `CreateNew` found an existing physical store.
- `NotFound`: `OpenExisting` found no physical store.
- `IncompatibleLayout`: an existing mapping or requested capacity does not
  match SMS2. No payload bytes were projected.
- `ParticipantTableFull`: every configured participant record is active or not
  yet safely reusable.
- `StoreBusy`: the cold open budget or a hot local retry/help budget expired.
- `UnsupportedPlatform`: the host cannot provide the required mapping, atomic,
  or owner-classification contract.

See [errors.md](errors.md) for operation statuses and [usage.md](usage.md) for
reservations, removal, recovery, waits, and disposal.

## Moving a noncurrent deployment

A current client does not convert an existing mapping. Stop writers and
readers, drain tokens, close every handle, remove or replace the physical store,
create a fresh SMS2 store, and republish from application-owned authoritative
data. Use a distinct public name for a side-by-side cutover.

## Next steps

- [Usage](usage.md)
- [Examples](examples.md)
- [Diagnostics](diagnostics.md)
- [Architecture](architecture.md)
- [Packaging](packaging.md)
- [Portability](portability.md)
