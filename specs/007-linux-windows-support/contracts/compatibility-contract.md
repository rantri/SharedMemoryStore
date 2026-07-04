# Contract: Compatibility and Release Impact

## Scope

This contract controls compatibility review for platform-support changes.

## Public API Compatibility

The implementation should preserve the current public API for ordinary store
usage. Any new public option, status, diagnostic property, or report member must
be treated as a public contract change and reviewed for semantic version impact.

## Status Compatibility

Existing meanings of these public outcomes must remain stable:

- `StoreOpenStatus.Success`
- `StoreOpenStatus.AlreadyExists`
- `StoreOpenStatus.NotFound`
- `StoreOpenStatus.InvalidOptions`
- `StoreOpenStatus.IncompatibleLayout`
- `StoreOpenStatus.UnsupportedPlatform`
- `StoreOpenStatus.InsufficientCapacity`
- `StoreOpenStatus.AccessDenied`
- `StoreOpenStatus.MappingFailed`
- `StoreOpenStatus.StoreBusy`
- `StoreOpenStatus.OperationCanceled`
- `StoreStatus.Success`
- `StoreStatus.UnsupportedPlatform`
- `StoreStatus.StoreBusy`
- `StoreStatus.OperationCanceled`
- `StoreStatus.AccessDenied`
- `StoreStatus.CorruptStore`

If an environment-capability outcome is added, migration notes and contract
tests must explain how it differs from unsupported platform, access denied,
invalid options, and mapping failure.

## Layout Compatibility

The existing shared-memory layout should remain unchanged unless owner-liveness,
cleanup, or cross-platform metadata cannot be represented safely outside the
layout. If layout changes are required:

- Increment and document the layout version according to existing layout rules.
- Reject incompatible existing mappings deterministically.
- Update shared-memory layout contracts and tests.
- Add migration or cleanup guidance.

## Windows Compatibility

Existing Windows workflows must remain passing:

- Basic publish/acquire/release/remove/reuse.
- Direct reservation ingest.
- Segmented publishing.
- Diagnostics.
- Lease and reservation recovery.
- Disposal races.
- Package consumption.
- Samples and documentation validation.

## Documentation and Metadata Compatibility

The release must update:

- README platform statement.
- `docs/portability.md`.
- Sample READMEs and Docker sample docs.
- `CHANGELOG.md`.
- `docs/releases.md`.
- Package release notes.
- Public XML comments affected by platform behavior.

## Release Readiness

The feature is not releasable until Linux, Windows, and Docker validation
evidence is captured in release notes or maintainer documentation.
