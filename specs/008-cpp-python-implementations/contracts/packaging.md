# Contract: Packaging and Compatibility

## C++ Distribution

- CMake builds a shared library, optional static library, public C and C++
  headers, tests, and samples.
- Install rules use standard runtime, library, archive, and include destinations.
- An exported `SharedMemoryStore::SharedMemoryStore` target is consumable through
  `find_package` from a clean external CMake project.
- Installed public artifacts declare native package, ABI, and layout versions.

## Python Distribution

- A PEP 517 source build compiles the native library and installs it beside the
  Python modules.
- Platform wheels contain the shared library and require no compiler at install
  time.
- Because the native library does not use the CPython ABI, one wheel per OS and
  architecture may support all declared Python 3 versions.
- The wheel is tested after installation into a clean environment; importing
  from the source tree must not mask missing packaged files.

## Versioning

- NuGet, native, and Python package versions advance independently.
- The C ABI has its own major/minor version.
- Every package release declares readable and creatable layout versions and the
  resource-naming version.
- Breaking mapped-layout, resource, public API, or C ABI changes require
  semantic-version review, migration notes, updated fixtures, and cross-version
  compatibility tests.
