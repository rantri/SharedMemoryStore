# Contract: C++ API

Namespace `shared_memory_store` exposes RAII wrappers over the C ABI.

## Public Types

- Strong enums for open mode, open status, and operation status with numeric
  values identical to the C and managed contracts.
- `store_options` with a required-capacity factory and validation helpers.
- `wait_options` supporting default, no-wait, bounded, and infinite waits.
- Move-only `memory_store`, `value_lease`, and `value_reservation` types.
- Non-owning byte views represented with `std::span`.
- Versioned recovery reports and diagnostics value types.

## Behavior

- Ordinary operation failures return status/result objects and do not throw.
- Construction/configuration programmer errors may use standard exceptions in
  the C++ wrapper; nothing throws across the C ABI.
- Destructors close handles and best-effort release/abort active tokens.
- Moving transfers ownership and leaves the source invalid.
- Lease payload and descriptor spans are read-only and invalid after release or
  owning store close.
- Reservation spans are writable and invalid after advance, commit, abort,
  recovery, or owning store close.
- String helpers encode store names as UTF-8; keys/descriptors/payloads remain
  opaque bytes.
