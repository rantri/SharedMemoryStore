# Getting Started

This guide selects one of the .NET, native C++, or Python distributions and
gets a clean consumer to a complete create/open, publish, acquire, release,
remove, and close workflow. All three use layout `1.2` and resource naming `1`.

## Prerequisites

- .NET SDK compatible with `net10.0` for the managed package and cross-runtime
  orchestration.
- CMake 3.20 or newer and a C++20 compiler for the native distribution.
- Python 3.10 or newer plus a PEP 517 build frontend for a source wheel build.
- PowerShell 7 (`pwsh`) for repository validation scripts.
- Linux or Windows for ordinary runtime and development workflows.
- Docker Engine or Docker Desktop only when validating same-host container
  sharing.

The managed package version is `1.0.1`; the native and Python distributions are
independently versioned `0.1.0`. If an artifact has not been published to its
ecosystem feed, build it locally from the repository.

| Consumer | Artifact | Public entry point |
|----------|----------|--------------------|
| .NET | NuGet `SharedMemoryStore` `1.0.1` | `MemoryStore` |
| C++ | CMake `SharedMemoryStore` `0.1.0` | `shared_memory_store::memory_store` |
| Python | wheel `shared-memory-store` `0.1.0` | `shared_memory_store.MemoryStore` |

## .NET: Create a Local Package Source

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Create a clean consumer project:

```powershell
dotnet new console -f net10.0 -n SharedMemoryStore.Tryout -o artifacts/tryout
dotnet add artifacts/tryout/SharedMemoryStore.Tryout.csproj package SharedMemoryStore --source artifacts/package
```

## .NET Minimal Workflow

Replace `artifacts/tryout/Program.cs` with this program:

```csharp
using SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = $"sms-start-{Guid.NewGuid():N}",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 2,
    MaxValueBytes = 64,
    MaxDescriptorBytes = 16,
    MaxKeyBytes = 16,
    LeaseRecordCount = 4,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, 64, 16, 16, 4)
};

var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
Console.WriteLine(openStatus);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    return 1;
}

using (store)
{
    var key = new byte[] { 1, 2, 3 };
    Console.WriteLine(store.TryPublish(key, [4, 5, 6], [9]));
    Console.WriteLine(store.TryAcquire(key, out var lease));
    Console.WriteLine(lease.ValueLength);
    Console.WriteLine(lease.Release());
    Console.WriteLine(store.TryRemove(key));
    Console.WriteLine(store.TryPublish(key, [7]));
}

return 0;
```

Run it:

```powershell
dotnet run --project artifacts/tryout/SharedMemoryStore.Tryout.csproj -c Release
```

Expected status path:

```text
Success
Success
Success
3
Success
Success
Success
```

## C++: Build and Consume the CMake Package

Build the shared library, dependency-free native tests, and basic sample:

```powershell
cmake -S . -B artifacts/native-build -DSMS_BUILD_TESTS=ON -DSMS_BUILD_SAMPLES=ON
cmake --build artifacts/native-build --config Release
ctest --test-dir artifacts/native-build -C Release --output-on-failure
cmake --install artifacts/native-build --config Release --prefix artifacts/native-install
```

The installed package exports `SharedMemoryStore::SharedMemoryStore` for
`find_package(SharedMemoryStore CONFIG REQUIRED)`. The C++ sample uses
`store_options::create`, `memory_store::try_create_or_open`, `try_publish`, and
a move-only `value_lease`:

```powershell
cmake -S samples/CppBasicUsage -B artifacts/cpp-sample -DCMAKE_PREFIX_PATH=artifacts/native-install
cmake --build artifacts/cpp-sample --config Release
```

[`scripts/validate-native.ps1`](../scripts/validate-native.ps1) combines the
native build, tests, installation, and clean CMake consumer check.

## Python: Build and Install a Wheel

The Python distribution packages the platform native library beside its
modules. Build a wheel, install it into a clean environment, and run the sample:

```powershell
python -m pip install build
python -m build --wheel
python -m venv artifacts/python-consumer
artifacts/python-consumer/Scripts/python -m pip install (Get-ChildItem dist/*.whl | Select-Object -First 1)
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
```

On Linux, replace `Scripts/python` with `bin/python`. Installation from a built
wheel does not require a compiler. Do not run the sample by adding
`src/python` to `PYTHONPATH`: the package intentionally loads only its adjacent,
version-checked native library.

## Open One Store from Multiple Runtimes

Processes interoperate only when they run on the same supported host and use
the same public store name, capacities, total mapped bytes, layout version, and
resource-naming rules. Keep the creator alive while a second runtime opens with
the equivalent `OpenExisting` mode. Keys, descriptors, and payloads are opaque
bytes and must not be converted through text. Use the compatibility metadata in
[`protocol/compatibility.json`](../protocol/compatibility.json) when combining
independently released versions.

The test-only JSON-lines agents and ordered-pair suite live under
[`tests/SharedMemoryStore.InteropTests/`](../tests/SharedMemoryStore.InteropTests/).
Their presence does not replace per-platform release evidence.

## Next Steps

- Use [Concepts](concepts.md) for the package vocabulary before advanced
  workflows.
- Use [Byte encoding](byte-encoding.md) when replacing sample byte literals
  with application string, integer, GUID, descriptor, or payload conventions.
- Use [Usage](usage.md) for the full consumer workflow.
- Use [Examples](examples.md) for basic values, frame-shaped values, direct
  ingest, segmented payloads, waits, and diagnostics snippets.
- Use [Errors](errors.md) when an operation returns a non-success status.
- Use [Diagnostics](diagnostics.md) to inspect capacity pressure and failure
  counters.
- Use [Lifecycle](lifecycle.md) to understand ownership, lease release, removal,
  stale recovery, and disposal.
- Use [Samples](samples.md) for the complete runnable sample ladder.
- Use [Packaging](packaging.md) for native installation, wheel contents, and
  independent versioning.
- Use [Portability](portability.md) before mixing runtimes or containers.
- Use [samples/BasicUsage/README.md](../samples/BasicUsage/README.md) for a
  runnable repository sample.
- Use [samples/ZeroCopyIngest/README.md](../samples/ZeroCopyIngest/README.md)
  for direct reservation and segmented publish workflows.
- Use [samples/DockerSharedMemory/README.md](../samples/DockerSharedMemory/README.md)
  when validating same-host Docker container participation.
- Use [samples/CppBasicUsage/README.md](../samples/CppBasicUsage/README.md) for
  the native RAII sample.
- Use [samples/PythonBasicUsage/README.md](../samples/PythonBasicUsage/README.md)
  for the installed-wheel sample.
