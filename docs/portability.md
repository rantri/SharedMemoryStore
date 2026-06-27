# Portability

The current package targets `.NET 10` and validates named memory-mapped files on
Windows x64 first. The shared-memory layout is the interoperability contract for
future C++ and Python implementations, but those bindings are not currently
implemented.

The detailed source is the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).

Future implementations must use the same little-endian field encoding, 8-byte
alignment, state-value assignments, key hashing, exact byte-key equality, slot
generation, lease registry, and remove/reuse state machine.

Layout compatibility follows semantic versioning. A major layout-version change
requires migration notes and contract-test updates. Minor compatible additions
must preserve existing field offsets and state semantics.

## Current Baseline

- Runtime package: `SharedMemoryStore` `0.1.0`.
- Target framework: `net10.0`.
- First validated platform: Windows x64 named memory-mapped files.
- Current language implementation: C#.
- Future audience: C++ and Python implementations or bindings that conform to
  the documented layout and lifecycle contracts.

## Unsupported Scenarios

- Current public docs do not claim C++ or Python bindings exist.
- Broad Linux, macOS, container, or network-distributed shared-memory support is
  not claimed by this package version.
- Platforms without reliable named mapping or owner-liveness checks return
  deterministic unsupported statuses for affected operations.
- Application-specific schemas, including frame metadata, are not parsed by the
  core store.

## Compatibility Rules

- Public API, layout, lifecycle, error, diagnostics, and package metadata are
  compatibility contracts.
- Breaking public API or layout changes require semantic-version review,
  migration notes, and contract-test updates.
- Documentation that changes a compatibility promise must update
  [CHANGELOG.md](../CHANGELOG.md) and [Release preparation](releases.md).
