# Packaging and Migration Contract

## Release identities

This feature publishes independently versioned distributions over one shared
protocol.

| Distribution | Release version | Native ABI | Creates/reads | Resource protocol | Required features |
|---|---:|---:|---:|---:|---:|
| NuGet `SharedMemoryStore` | `3.0.0` | N/A | layout `2.0` only | `2` | `7` |
| CMake `SharedMemoryStore` | `1.0.0` | provides ABI `2.0` | layout `2.0` only | `2` | `7` |
| Python `shared-memory-store` | `1.0.0` | requires ABI `2.0` | layout `2.0` only | `2` | `7` |

The Linux shared-library ABI soname is `2`. Package versions, C ABI version,
mapped layout, resource protocol, and feature masks are separate identities and
must all appear in release metadata and the compatibility manifest.

No current distribution advertises layout `1.2` as creatable, readable, or
selectable. Historical specifications and source-control history may retain
descriptions of the retired protocol, but current package metadata, samples,
current guidance, and compatibility declarations do not.

## Managed NuGet package 3.0.0

The managed package:

- is built from `src/SharedMemoryStore/SharedMemoryStore.csproj`;
- targets `net10.0`;
- has no runtime dependency beyond the .NET base class library and required OS
  facilities;
- contains XML documentation and portable symbols;
- packs the repository README at the package root;
- exposes only the single-protocol API in `public-api.md`; and
- states prominently that `3.0.0` removes legacy/profile APIs and cannot open a
  retired mapping.

Package release notes, `CHANGELOG.md`, current documentation, and the
machine-readable compatibility manifest must agree on version `3.0.0`, layout
`2.0`, resource protocol `2`, required features `7`, supported platforms, and
the destructive migration boundary.

### Managed clean-consumer gate

From a clean temporary directory, validation must:

1. pack `SharedMemoryStore` `3.0.0` in Release configuration;
2. create a new `net10.0` console application;
3. install only the locally produced package;
4. compile against the ordinary `Create` and `CalculateRequiredBytes` APIs with
   no profile symbol;
5. assert protocol identity `(2, 0, 2, 7, 0)`;
6. execute publish/acquire/release/remove/reuse, segmented publication, direct
   reservation commit/abort, recovery, diagnostics, participant exhaustion,
   and post-disposal outcomes; and
7. prove no source-project reference or repository build output satisfies the
   consumer accidentally.

## Native CMake package 1.0.0

The native distribution:

- requires CMake 3.20 or newer and a C++20 compiler;
- supports qualified Windows x64 and Linux x64 targets only;
- builds the ABI 2 shared library and may build the optional static library;
- exports `SharedMemoryStore::SharedMemoryStore` and, when enabled,
  `SharedMemoryStore::SharedMemoryStoreStatic`;
- installs `shared_memory_store/c_api.h` and
  `shared_memory_store/store.hpp`;
- installs CMake package configuration declaring package `1.0.0`, ABI `2.0`,
  layout `2.0`, resource protocol `2`, and required features `7`;
- gives the installed Linux shared library `SOVERSION 2`; and
- links no undeclared broker, service, managed runtime, or background worker.

The build must fail clearly rather than selecting a blocking atomic fallback
when aligned 64-bit atomics are not always lock-free. Unsupported architecture,
byte order, kernel, filesystem, or cold-lock facilities produce the documented
unsupported result and never select layout `1.2`.

### Native clean-consumer gate

Validation must:

1. configure and build shared and optional static artifacts in Release mode;
2. run native protocol, atomic, lifecycle, store, recovery, diagnostics, C ABI,
   and package tests;
3. install into a clean prefix;
4. configure an unrelated CMake project using only `find_package` and the
   exported target;
5. compile without repository-private headers or source paths;
6. assert package, ABI, layout, resource, feature, and soname identities; and
7. execute the complete minimal lifecycle through both the C++ RAII wrapper and
   C ABI.

Installed headers and binaries must agree exactly on ABI major. A stale ABI 1
header paired with an ABI 2 binary, or the inverse, is a validation failure.

## Python package 1.0.0

The Python distribution:

- is named `shared-memory-store` version `1.0.0`;
- requires Python 3.10 or newer;
- uses `scikit-build-core` only as a build dependency;
- requires no third-party Python runtime package;
- ships one ABI 2 native shared library directly beside its Python modules;
- produces platform- and architecture-specific Windows x64 and Linux x64
  wheels with a generic supported Python 3 ABI tag;
- loads only that adjacent packaged artifact;
- validates ABI `2.0`, layout `2.0`, resource protocol `2`, required features
  `7`, and record/offset conformance before first use; and
- exposes the context-managed API in `public-api.md`.

The source distribution includes the root CMake project, all native sources and
public headers, Python modules, current compatibility metadata, license, and
README so a wheel can be rebuilt without access to unlisted checkout files. It
contains no compiled native binary.

### Python clean-consumer gate

Validation must:

1. run Python tests against a freshly staged package plus freshly built ABI 2
   native artifact;
2. build exactly one wheel and one source distribution;
3. inspect the wheel for the expected same-platform native library and reject
   missing or opposite-platform binaries;
4. inspect the source distribution for every required build input and no native
   binary;
5. build a second wheel from that source distribution;
6. install the rebuilt wheel without dependencies into a fresh virtual
   environment;
7. clear `PYTHONPATH`, change to an unrelated directory, and prove imports do
   not resolve from repository sources;
8. assert Python package `1.0.0`, ABI `2.0`, and protocol `(2, 0, 2, 7, 0)`; and
9. run the Python sample and the full Python lifecycle, recovery, diagnostics,
   participant-exhaustion, view-invalidation, and packaged-library-location
   tests.

An installed wheel must fail with an actionable import/load error if its
adjacent native library is missing, wrong-architecture, ABI 1, or protocol
incompatible. It must not search for a replacement library elsewhere.

## Cross-distribution release gate

Package-level success is insufficient without interoperability. Release
validation on Windows x64 and Linux x64 must build the exact managed, native,
and Python artifacts under consideration and run:

- all nine ordered producer-to-consumer runtime pairs;
- mixed-runtime reservation, publication, lease, removal, and reuse;
- exact collision and overflow churn;
- participant capacity and participant close/recovery;
- bounded/no-wait behavior and verification that a held operation-lock resource
  cannot block an already-open steady-state data operation;
- pause, termination, explicit recovery, PID reuse, and supported Linux
  PID-namespace/container scenarios;
- required-feature, malformed-layout, terminal-corruption, and retired-layout
  rejection before payload access; and
- clean samples using installed artifacts rather than source-tree binaries.

The compatibility manifest is declarative metadata, not evidence. A platform
or ordered runtime pair is qualified only when its release test completes and
its artifacts, logs, and protocol identities correspond to the release inputs.

## Migration from the retired layout

There is no in-place conversion, compatibility reader, automatic fallback,
dual-layout engine, or mixed-layout writer mode. Migration is an application
data operation and uses this exact sequence:

1. **Drain**: stop new application publication, reservation, acquisition, and
   removal work. Finish or explicitly abort every reservation and release every
   lease. Preserve the authoritative application-owned data needed to republish.
2. **Close**: close every store, lease, and reservation wrapper in every process
   and container. Verify no live owner remains. A current package is not used to
   inspect or extract retired-layout payload bytes.
3. **Recreate**: remove the retired physical mapping only after the complete
   drain and final close, then create a new canonical store under the intended
   public name with the required layout-2.0 capacities and participant count.
   Physical creation starts from a new mapping; observing an all-zero or retired
   header never authorizes conversion.
4. **Republish**: publish values from the application's authoritative source or
   migration snapshot through a current runtime. Validate exact counts and
   checksums through at least one other current runtime before restoring normal
   traffic.

If any old handle remains during recreation, creation must fail rather than
splitting participants across old and new physical resources. If a current
runtime encounters the retired mapping before recreation, it returns
`IncompatibleLayout` before payload access and leaves the mapping unchanged.

Applications that need a staged cutover may create a distinct public store name
and republish application-owned data there, but the library still does not read
the retired mapping or choose between protocols. The application owns routing
and cutover; this is not fallback behavior.

## Failure and rollback policy

- A failed migration keeps traffic stopped until the application either
  completes recreation/republish or restores from its own authoritative data.
- Current packages never recreate layout `1.2`, silently downgrade required
  features, or reopen a retired mapping.
- Rolling back application code does not convert mapped bytes. Any historical
  binary that requires the retired protocol must be isolated from current
  participants and use a separately coordinated application-data restore; it
  cannot join a live canonical store.
- Store names may be reused only after the old generation has no live owner and
  platform lifecycle cleanup has safely retired its data resources.

## Documentation and metadata consistency

The following must agree before release:

- package project versions and release notes;
- C ABI macro, installed soname, and CMake package variables;
- Python project version, module `__version__`, and required ABI;
- protocol fixture manifest and `protocol/compatibility.json`;
- README, getting-started, packaging, portability, errors, release, sample, and
  migration guidance; and
- clean-consumer and cross-runtime test expectations.

Static inspection must find one current creatable/readable layout, no public
profile selector, no legacy engine in produced artifacts, and no documentation
claim that current packages can open or convert a retired mapping.

