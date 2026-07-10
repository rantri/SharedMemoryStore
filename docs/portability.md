# Portability

The repository contains .NET 10, C++20, and Python 3.10+ implementations for
ordinary same-host workflows on 64-bit little-endian Linux and Windows.
Same-host Linux Docker participation requires deployment configuration that
exposes compatible shared-memory, synchronization, owner-liveness, permission,
and capacity capabilities. Layout `1.2` and resource naming `1` are the common
interoperability boundary; similar public APIs alone are insufficient.

Detailed sources:

- [shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [ingest-layout.md](../specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [public-api-contract.md](../specs/005-api-production-readiness/contracts/public-api-contract.md)
- [protocol/README.md](../protocol/README.md)
- [resource-naming-v1.md](../protocol/resource-naming-v1.md)
- [compatibility.json](../protocol/compatibility.json)
- [interoperability.md](../specs/008-cpp-python-implementations/contracts/interoperability.md)

## Current Baseline

- Managed distribution: NuGet `SharedMemoryStore` `1.0.1`, targeting
  `net10.0`.
- Native distribution: CMake `SharedMemoryStore` `0.1.0`, C++20, C ABI `1.0`.
- Python distribution: `shared-memory-store` `0.1.0`, Python 3.10 or newer,
  using `ctypes` over the bundled native library.
- Shared identities: layout `1.2`, resource naming `1`, little-endian 64-bit
  process model.
- Implementation targets: Linux and Windows. Release validation for each
  distribution and ordered runtime pairing must be recorded separately.
- Managed supported container profile: Linux-based same-host Docker containers
  with shared IPC and compatible owner-liveness, permissions, and shared-memory
  capacity. Native/Python container claims require their own recorded
  cross-runtime evidence.

## Layout Compatibility

Every implementation uses the same little-endian field encoding, 8-byte
alignment, state-value assignments, key hashing, exact byte-key equality, slot
lifecycle identity, lease registry, reservation progress, and remove/reuse
state machine. Static fixtures pin exact record sizes, offsets, arithmetic,
hashes, status values, and resource-name vectors.

Layout compatibility follows semantic versioning. A major layout-version change
requires migration notes and contract-test updates. Minor compatible additions
must preserve existing field offsets and state semantics.

Layout minor version `2` stores slot lifecycle identity as generation plus reuse
epoch in slot metadata, index entries, and lease records. Older mappings with
the prior record sizes are rejected as incompatible rather than interpreted with
partial identity.

## Reservation Portability

`SlotPublishing` is the language-neutral pending reservation state. During that
state, `SharedSlotMetadata.Reserved` stores bytes advanced by the producer,
`ValueLength` stores the announced payload length, and `PublisherProcessId`
identifies the owner for explicit recovery. Commit must validate slot
generation and exact progress before transitioning to `SlotPublished`.

Every implementation treats writable reservation memory as valid only while
the slot remains pending and full lifecycle identity matches.
Scatter/gather committed values are out of scope for this layout; segmented
publish copies into one contiguous slot value.

## Language Distribution Boundaries

The .NET implementation owns its managed protocol mechanisms and does not load
the native library. The native implementation owns one C++ protocol core and
the Windows/Linux adapters. Its exported C ABI uses explicit-width integers,
versioned structures, opaque handles, and caller-owned buffers. The public C++
API adds move-only RAII and `std::span` views without making the C++ ABI the
Python boundary.

The Python package calls C ABI `1.0` through standard-library `ctypes`. A wheel
places `shared_memory_store.dll` or `libshared_memory_store.so` beside the
Python modules. The loader does not search the current directory, `PATH`, or a
system library path, and it rejects incompatible ABI or protocol metadata.
Python lease views are read-only; reservation views are writable only for the
reservation lifetime.

## Trusted Same-Host Boundary

The direct ingest API exposes writable shared-memory bytes to producers before
commit. The security model assumes only trusted same-host services can open the
mapping and participate in the store. Deployment is responsible for OS ACLs,
service identity, process isolation, and package distribution.

SharedMemoryStore validates lifecycle state, slot generation, key ownership, and
reader visibility, but no distribution defends against a malicious in-boundary
writer that intentionally corrupts mapped bytes, forges metadata, or ignores
the public API.

## Platform Resource Model

Windows uses named operating-system memory mappings and named synchronization.
An explicit `Global\` mapping name uses global synchronization in managed
`1.0.1` and native `0.1.0`. All participants must implement compatible
resource-naming version `1` behavior. Ordinary unqualified and explicit
`Local\` names retain session-local synchronization.
Linux uses deterministic files in a shared runtime memory location such as
`/dev/shm`, with names derived from the public store name and a collision
prevention hash. Docker containers participate in the Linux model only when
their IPC and process-liveness configuration lets all participants see the same
resources and classify owners safely.

The managed and native implementations must derive the same resources and
participate in the same lock. Python inherits the native behavior rather than
reimplementing it.

The Linux resource directory is owner-only (`0700`), and region,
synchronization, owner, and lifecycle files are owner-only (`0600`). Cooperating
host processes must therefore run as the same Unix identity. Containers must
share a compatible identity as well as IPC and process-liveness namespaces.

## Unsupported Scenarios

- macOS is not currently supported.
- 32-bit processes, big-endian hosts, and architectures without recorded
  conformance evidence are not current release targets.
- Cross-host shared memory, distributed-cache behavior, persistence across host
  restart, Windows containers, and default-isolated Docker containers are not
  supported by these distributions.
- Platforms without reliable named mapping or owner-liveness checks return
  deterministic unsupported statuses for affected operations.
- Application-specific schemas, including frame metadata, are not parsed by the
  core store.
- Protection against malicious writers with legitimate in-boundary access is
  outside the package scope.

## Compatibility Rules

- Public API, layout, lifecycle, error, diagnostics, and package metadata are
  compatibility contracts.
- NuGet, CMake, and Python versions advance independently. Compatibility is
  determined from layout, resource naming, and C ABI declarations rather than
  matching package version numbers.
- Breaking public API or layout changes require semantic-version review,
  migration notes, and contract-test updates.
- A release must not convert target-platform metadata into a validation claim
  until native tests, package consumption, and the relevant ordered runtime
  pairs have passed on that platform.
- Documentation that changes a compatibility promise must update
  [CHANGELOG.md](../CHANGELOG.md), [Maintainers](maintainers.md), and
  [Release preparation](releases.md).
