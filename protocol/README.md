# SharedMemoryStore Protocol

This directory is the language-neutral compatibility boundary for the managed,
native, and Python distributions. It specifies the bytes in a mapped region and
the operating-system resources used to find, synchronize, own, and clean up that
region. Public language APIs may be idiomatic, but they must not reinterpret
these contracts.

## Current protocol identities

| Contract | Current identity | Canonical definition |
|---|---:|---|
| Mapped layout | `1.2` | [layout-v1.2.md](layout-v1.2.md) |
| Platform resource naming | `1` | [resource-naming-v1.md](resource-naming-v1.md) |
| Conformance manifest | `1` | [fixtures/v1.2/manifest.json](fixtures/v1.2/manifest.json) |
| Native C ABI | `1.0` | [native-c-api.md](../specs/008-cpp-python-implementations/contracts/native-c-api.md) |

Package versions are independent of all four identities. A package release
must declare the layout versions it can create and open, the resource-naming
version it implements, and its C ABI range when applicable.

## Layout version boundary

New stores created by this repository use layout major `1`, minor `2`. The
advertised read and create boundary for this feature is exactly `1.2`.

The two header numbers are not sufficient evidence of compatibility. An opener
must also validate the magic, major version, header and record sizes, configured
capacities, calculated section offsets and lengths, total-region bounds, and
every state value it may observe. A mapping with the right version numbers but
a different shape is incompatible. Likewise, a previously released minor
version is not implicitly readable: layout `1.2` enlarged the index, slot, and
lease records to add reuse epochs, so earlier record shapes are rejected rather
than partially interpreted.

Any change to mapped bytes, field meaning, state transitions, hashing, probing,
or visibility rules requires a new layout identity and updated fixtures. A
major version is required for a protocol redesign that cannot safely coexist
with the major-1 validation model. A minor increment still requires explicit
compatibility evidence; it does not promise that all older mapped shapes can be
opened.

Resource naming is versioned separately because two implementations with
identical mapped records still cannot interoperate if they derive different
mapping, lock, or owner resources. A resource-name or lock-participation change
requires a new resource-naming version and live cross-runtime tests.

The C ABI is also independent. ABI-only additions do not change mapped bytes,
and a layout change does not by itself authorize an ABI break.

## Canonical evidence

The JSON manifest pins exact record sizes and offsets, numeric states, public
status values, open modes, layout arithmetic, FNV-1a hashes, and platform-name
vectors. It stores 64-bit hashes as fixed-width hexadecimal strings so JSON
number precision cannot alter them. Representative mapped-region binaries are
offline conformance inputs only; they must never be opened as live mappings
because their owner process identifiers and platform lifecycle resources are
not live.

Changes to an executable constant or algorithm must update the narrative,
manifest, every language's static conformance tests, and the cross-runtime
matrix in one review.
