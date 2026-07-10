# Contract: Cross-Runtime Interoperability

## Static Conformance

Every implementation must consume the same fixture manifest and prove:

- exact record sizes and every field offset.
- numeric layout, state, status, and open-mode assignments.
- layout-calculation and overflow vectors.
- FNV-1a key hashes and exact byte equality.
- resource-name vectors including Unicode, punctuation, scope prefixes, and
  maximum-length names.
- parsing and normalized description of empty, published, pending reservation,
  pending removal, and reused-slot binary fixtures.

## Live Agent Protocol

Each runtime provides a test-only JSON-lines subprocess agent with equivalent
commands: open/create, publish, segmented publish, acquire/read/release, remove,
reserve/write/advance/commit/abort, recover, diagnostics, hold lock, close, and
crash. Binary arguments and results use base64; status names and numbers are
both emitted. Agents write protocol responses only, never library diagnostics.

## Required Matrix

For each supported OS, run every ordered producer-to-consumer pair among C#,
C++, and Python. The producer remains alive while the consumer opens the same
store. Verify bytes and then reverse mutation ownership through remove and
republish.

Pair scenarios also cover:

- pending reservation invisibility and duplicate blocking before commit.
- remove while a foreign lease is active, final release, and slot reuse.
- abrupt lease and reservation owner termination followed by explicit recovery.
- no-wait and bounded-wait contention against a foreign lock owner.
- mismatched capacity and incompatible layout rejection.
- three simultaneous Linux owners with non-final and final close cleanup.
- concurrent same-key publication with one success.

## Fixture Safety

Binary fixtures are offline parse/emit inputs, never live mappings. Live replay
would carry fake PIDs and omit platform ownership resources.
