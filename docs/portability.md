# Portability

The current package targets `.NET 10` and validates named memory-mapped files on
Windows x64 first. The shared-memory layout is the interoperability contract for
future C++ and Python implementations, but those bindings are not currently
implemented.

The detailed source is the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).

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

## Current Baseline

- Runtime package: `SharedMemoryStore` `1.0.0`.
- Target framework: `net10.0`.
- First validated platform: Windows x64 named memory-mapped files.
- Current language implementation: C#.
- Future audience: C++ and Python implementations or bindings that conform to
  the documented layout and lifecycle contracts.

## Reservation Portability

`SlotPublishing` is the language-neutral pending reservation state. During that
state, `SharedSlotMetadata.Reserved` stores bytes advanced by the producer,
`ValueLength` stores the announced payload length, and `PublisherProcessId`
identifies the owner for explicit recovery. Commit must validate slot
generation and exact progress before transitioning to `SlotPublished`.

Future C++ and Python implementations must treat writable reservation memory as
valid only while the slot remains pending and full lifecycle identity matched.
Scatter/gather committed values are out of scope for this layout; segmented
publish copies into one contiguous slot value.

## Trusted Same-Host Boundary

The direct ingest API exposes writable shared-memory bytes to producers before
commit. The security model assumes only trusted same-host services can open the
mapping and participate in the store. Deployment is responsible for operating
system ACLs, service identity, process isolation, and package distribution.

SharedMemoryStore validates lifecycle state, slot generation, key ownership, and
reader visibility, but it does not defend against a malicious in-boundary writer
that intentionally corrupts mapped bytes, forges metadata, or ignores the public
API. Future C++, Python, or other bindings must document the same trust boundary
instead of implying protection from hostile processes that already have mapping
access.

## Unsupported Scenarios

- Current public docs do not claim C++ or Python bindings exist.
- Broad Linux, macOS, container, or network-distributed shared-memory support is
  not claimed by this package version.
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
  [CHANGELOG.md](../CHANGELOG.md) and [Release preparation](releases.md).
