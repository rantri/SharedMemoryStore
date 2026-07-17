# Packaging

SharedMemoryStore ships three independently versioned distributions. They are
not nested inside one another, but they implement one mapped SMS2 protocol.

| Ecosystem | Package | Version | Native ABI | Creates/reads |
|---|---|---:|---:|---|
| NuGet | `SharedMemoryStore` | `3.0.0` | n/a | SMS2 `2.0` |
| CMake | `SharedMemoryStore` | `1.0.0` | provides C ABI `2.0` | SMS2 `2.0` |
| Python wheel | `shared-memory-store` | `1.0.0` | requires C ABI `2.0` | SMS2 `2.0` |

Resource protocol `2`, required features `7`, optional features `0`, and the
qualified Windows/Linux x64 targets are declared in
[`protocol/compatibility.json`](../protocol/compatibility.json).

## NuGet package

The managed project is
[`src/SharedMemoryStore/SharedMemoryStore.csproj`](../src/SharedMemoryStore/SharedMemoryStore.csproj).
It targets `net10.0` and has no runtime `PackageReference`; the implementation
uses the .NET base class library.

Key package metadata:

| Property | Value |
|---|---|
| `PackageId` | `SharedMemoryStore` |
| `Version` | `3.0.0` |
| `GenerateDocumentationFile` | `true` |
| `PackageLicenseExpression` | `MIT` |
| `PackageReadmeFile` | `README.md` |
| Symbols | portable PDB in `.snupkg` |

Build and inspect locally:

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

The package contains the managed assembly, XML documentation, symbols in the
symbol package, license metadata, and repository README. It does not contain the
C++ or Python distributions.

The project `PackageReleaseNotes` state the single-protocol break, SMS2
identity, participant capacity, lock-free lifecycle, diagnostics, native/Python
versions, and required drain-close-recreate-republish deployment replacement.

## CMake package

The root project is `SharedMemoryStore` version `1.0.0`, requires CMake 3.20+
and C++20, and builds the platform native library. Public headers include:

- `shared_memory_store/c_api.h`: fixed-width C ABI `2.0`;
- `shared_memory_store/store.hpp`: move-only C++ RAII API; and
- protocol/status declarations required by consumers.

Configure, test, install, and consume:

```powershell
cmake -S . -B artifacts/native -DSMS_BUILD_TESTS=ON -DSMS_BUILD_SAMPLES=ON -DSMS_INSTALL=ON
cmake --build artifacts/native --config Release
ctest --test-dir artifacts/native -C Release --output-on-failure
cmake --install artifacts/native --config Release --prefix artifacts/native-install
```

Consumer CMake:

```cmake
find_package(SharedMemoryStore 1.0 CONFIG REQUIRED)
target_link_libraries(my_app PRIVATE SharedMemoryStore::shared_memory_store)
```

The install exports the shared target, headers, config/version files, and the
platform library with ABI/SOVERSION `2`. Optional static output does not change
the C ABI or mapped protocol.

A clean consumer must build from a directory outside the source tree using only
the install prefix. This catches undeclared include paths, source-tree runtime
loading, and missing transitive link requirements.

## Python wheel

[`pyproject.toml`](../pyproject.toml) defines `shared-memory-store` version
`1.0.0` for Python 3.10+. `scikit-build-core` builds the native library and
places it beside the Python modules:

```text
shared_memory_store/
  __init__.py
  _native.py
  store.py
  shared_memory_store.dll       # Windows wheel
  libshared_memory_store.so     # Linux wheel
```

The Python runtime surface uses only the standard library and `ctypes` after
installation. Build dependencies are not runtime dependencies.

Build and test a clean wheel:

```powershell
python -m pip install build
python -m build --wheel --sdist
python -m venv artifacts/python-consumer
artifacts/python-consumer/Scripts/python -m pip install (Get-ChildItem dist/*.whl | Select-Object -First 1)
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
```

Use `artifacts/python-consumer/bin/python` on Linux.

The loader searches only the installed package directory for the expected
platform library, loads it with platform-appropriate safe flags, and verifies C
ABI major `2` before exposing a store. It does not search the current directory,
`PATH`, arbitrary system directories, or the repository source tree.

The source distribution includes every CMake input, native source/header,
Python module, protocol compatibility declaration, license, and README needed to
rebuild the same wheel in isolation.

## Compatibility metadata

`protocol/compatibility.json` schema `3` separates distribution versions from
the one shared protocol:

```text
shared_protocol.layout.version = 2.0
shared_protocol.layout.magic = SMS2
shared_protocol.layout.resource_protocol = 2
shared_protocol.layout.required_features_mask = 7
shared_protocol.noncurrent_mapping_policy = reject-before-payload-access
```

Each distribution declares `creates_layouts: ["2.0"]` and
`reads_layouts: ["2.0"]`. The CMake entry declares C ABI `2.0`; the Python entry
declares required C ABI `2.0`.

Matching package versions are not required for interoperability. Matching
protocol identity, capacities, public resource name, platform namespace, and
application byte schema are required.

## Clean-consumer gates

Managed:

```powershell
pwsh ./scripts/validate-package-consumption.ps1 -Configuration Release
```

This packs NuGet `3.0.0`, creates an isolated console project and package cache,
installs only the produced package, builds the documented lifecycle, checks
protocol identity and participant exhaustion, and runs it.

Native:

```powershell
pwsh ./scripts/validate-native.ps1 -Configuration Release
```

The native gate configures from clean state, builds/tests, installs, and builds
an external `find_package` consumer.

Python:

```powershell
pwsh ./scripts/validate-python.ps1 -Configuration Release
```

The Python gate rebuilds wheel and sdist, installs into a clean environment,
clears source-tree import paths, validates adjacent ABI loading, tests wrong or
missing ABI rejection, and runs the installed sample.

## Release validation

A release is ready only when:

- package metadata and public documentation agree on versions;
- managed, native, Python, and nine ordered-pair interoperability suites pass on
  qualified Windows/Linux x64 hosts;
- the protocol manifest and compatibility declaration agree with all three
  implementations;
- every clean consumer runs without source-tree dependencies;
- symbols and native/Python binary inputs are complete; and
- release notes state breaking impact and deployment replacement steps.

The NuGet publishing workflow may publish the managed package independently.
CMake installation artifacts and Python wheels have their own publication and
signing policy; their presence in this repository does not imply registry
publication.

## Versioning rules

- A language API change advances that distribution's package version.
- A C ABI break advances the ABI major and native/Python package metadata.
- A mapped byte, state, hash, or memory-order incompatibility requires a new
  layout identity.
- A platform resource-name or cleanup incompatibility requires a new resource
  protocol.
- Optional additive behavior must not change the meaning of required SMS2
  fields.

## Deployment replacement

Packages do not convert a noncurrent mapping. Drain readers and writers, close
all handles, remove or replace the physical resources, create a fresh SMS2
store, and republish from application-owned authoritative data. Side-by-side
cutover uses another public store name.

## Related files

- [README](../README.md)
- [Release notes](releases.md)
- [Architecture](architecture.md)
- [Portability](portability.md)
- [Protocol overview](../protocol/README.md)
