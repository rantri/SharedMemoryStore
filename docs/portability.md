# Portability

The current package targets `.NET 10` and supports ordinary same-host runtime
and development workflows on Linux and Windows. Same-host Linux Docker
containers are supported when deployment configuration exposes the required
shared-memory, synchronization, owner-liveness, permission, and capacity
capabilities. The shared-memory layout is the interoperability contract for
future C++ and Python implementations, but those bindings are not currently
implemented.

Detailed sources:

- [shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [ingest-layout.md](../specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [public-api-contract.md](../specs/005-api-production-readiness/contracts/public-api-contract.md)

## Current Baseline

- Runtime package: `SharedMemoryStore` `1.0.0`.
- Target framework: `net10.0`.
- Supported host platforms: Linux and Windows.
- Supported container profile: Linux-based same-host Docker containers with
  shared IPC and compatible owner-liveness, permissions, and shared-memory
  capacity.
- Current language implementation: C#.
- Future audience: C++ and Python implementations or bindings that conform to
  the documented layout and lifecycle contracts.

## Layout Compatibility

Future implementations must use the same little-endian field encoding, 8-byte
alignment, state-value assignments, key hashing, exact byte-key equality, slot
lifecycle identity, lease registry, reservation progress, and remove/reuse
state machine.

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

Future implementations must treat writable reservation memory as valid only
while the slot remains pending and full lifecycle identity matches.
Scatter/gather committed values are out of scope for this layout; segmented
publish copies into one contiguous slot value.

## Trusted Same-Host Boundary

The direct ingest API exposes writable shared-memory bytes to producers before
commit. The security model assumes only trusted same-host services can open the
mapping and participate in the store. Deployment is responsible for OS ACLs,
service identity, process isolation, and package distribution.

SharedMemoryStore validates lifecycle state, slot generation, key ownership, and
reader visibility, but it does not defend against a malicious in-boundary writer
that intentionally corrupts mapped bytes, forges metadata, or ignores the public
API. Future implementations or bindings must document the same trust boundary.

## Platform Resource Model

Windows uses named operating-system memory mappings and named synchronization.
Linux uses deterministic files in a shared runtime memory location such as
`/dev/shm`, with names derived from the public store name and a collision
prevention hash. Docker containers participate in the Linux model only when
their IPC and process-liveness configuration lets all participants see the same
resources and classify owners safely.

## Unsupported Scenarios

- Current public docs do not claim C++ or Python bindings exist.
- macOS is not currently supported.
- Cross-host shared memory, distributed-cache behavior, persistence across host
  restart, Windows containers, and default-isolated Docker containers are not
  supported by this package version.
- Platforms without reliable named mapping or owner-liveness checks return
  deterministic unsupported statuses for affected operations.
- Application-specific schemas, including frame metadata, are not parsed by the
  core store.
- Protection against malicious writers with legitimate in-boundary access is
  outside the package scope.

## Compatibility Rules

- Public API, layout, lifecycle, error, diagnostics, and package metadata are
  compatibility contracts.
- Breaking public API or layout changes require semantic-version review,
  migration notes, and contract-test updates.
- Documentation that changes a compatibility promise must update
  [CHANGELOG.md](../CHANGELOG.md), [Maintainers](maintainers.md), and
  [Release preparation](releases.md).
