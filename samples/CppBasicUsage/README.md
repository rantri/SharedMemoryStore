# C++ Basic Usage Sample

## Purpose and Audience

This sample is the smallest C++20 consumer of the installed
`SharedMemoryStore::SharedMemoryStore` target. It demonstrates the native RAII
surface over the canonical SMS2 engine and C ABI 2.

## Concepts Demonstrated

- participant-aware `store_options::create`;
- `memory_store::try_create_or_open`;
- protocol identity `2.0`, resource protocol `2`, feature mask `7`;
- opaque binary publication;
- move-only `value_lease`, borrowed `std::span`, and explicit release; and
- automatic store close through RAII.

## Prerequisites

- CMake 3.20 or newer.
- A C++20 compiler on a qualified x86-64 Windows or Linux host.
- Either this repository or an installed `SharedMemoryStore` CMake package
  version `1.0.0` with C ABI `2.0`.

## Run

Repository build:

```powershell
cmake -S . -B artifacts/native -DSMS_BUILD_SAMPLES=ON
cmake --build artifacts/native --config Release --target shared_memory_store_cpp_sample
```

The sample directory can also be configured as a standalone project against an
install prefix:

```powershell
cmake -S samples/CppBasicUsage -B artifacts/cpp-sample -DCMAKE_PREFIX_PATH=artifacts/native-install
cmake --build artifacts/cpp-sample --config Release
```

## Expected Output

```text
protocol: 2.0 resource=2 features=7
value bytes: 3
```

## Expected Non-Success Statuses

- `unsupported_platform`: mapped atomics or required platform facilities are
  unavailable.
- `incompatible_layout`: an existing mapping is not canonical SMS2.
- `participant_table_full`: every participant record is occupied.
- `store_busy`: a bounded cold-open or local progress budget expired.

The sample returns a nonzero code on any unexpected status.

## Cleanup

The process-specific name isolates runs. The lease is released explicitly and
the RAII store closes before exit.

## Related Documentation

- [Samples](../../docs/samples.md)
- [Getting started](../../docs/getting-started.md)
- [Usage](../../docs/usage.md)
- [Packaging](../../docs/packaging.md)
- [Portability](../../docs/portability.md)

## Scope Boundaries and Non-Goals

This sample does not demonstrate reservations, segmented publication, explicit
recovery, or cross-runtime orchestration. It uses only installed public headers
and the exported CMake target.
