# Contract: Platform Runtime Support

## Scope

This contract defines the public runtime behavior for Linux and Windows support.
It applies to ordinary same-host package usage outside containers and to the
same public store workflows validated by package-consumption tests.

## Supported Environments

- Windows environments with required named shared-resource and synchronization
  capabilities.
- Linux environments with required same-host shared-memory, synchronization,
  owner-liveness, permission, cleanup, and capacity capabilities.
- Platforms outside Linux and Windows remain unsupported unless a later feature
  adds them.

## Required Workflows

Each supported environment must pass these workflows with the same public API
and documented statuses:

- Create or open a named store.
- Publish immutable value bytes and optional descriptor bytes.
- Acquire multiple read leases for the same value.
- Release or dispose leases.
- Remove values with and without active leases.
- Reuse slots after final release.
- Reserve, advance, commit, abort, and recover direct-ingest reservations.
- Publish segmented payloads.
- Read diagnostics and failure counts.
- Recover stale leases and stale reservations safely.
- Dispose stores and handle disposal races.

## Open-Mode Outcomes

- `CreateNew` succeeds only when no live compatible store resource exists.
- `CreateNew` returns `AlreadyExists` when a live compatible resource exists.
- `OpenExisting` succeeds only when a live compatible resource exists.
- `OpenExisting` returns `NotFound` when no live compatible resource exists.
- `CreateOrOpen` creates a new resource when none exists and opens an existing
  compatible resource otherwise.
- Incompatible layout, insufficient capacity, access denied, unsupported
  capabilities, and mapping failures must use the documented public outcomes.

## Synchronization Outcomes

- Public operations must honor default, no-wait, bounded wait, infinite wait,
  and cancellation policies.
- Contention must produce `StoreBusy` or `OperationCanceled` according to the
  public wait policy.
- Abandoned owners must be normalized into documented validation or recovery
  behavior.
- Raw platform synchronization exceptions must not leak through public store
  methods.

## Owner Recovery Outcomes

- Current-process owners may be recovered only when the caller opts into
  current-process recovery.
- Other live owners must remain active and continue protecting storage.
- Stale owners may be recovered when liveness can be evaluated safely.
- Unsupported or unsafe owner records must be reported and must not be reclaimed
  aggressively.

## Resource Naming

- The public `SharedMemoryStoreOptions.Name` remains the consumer-facing store
  identity.
- Platform-visible resource names must be deterministic for the same public name
  and compatible options.
- Resource names must be sanitized to prevent collisions, path traversal, or
  invalid platform resource identifiers.

## Documentation Requirements

README, portability docs, lifecycle docs, diagnostics docs, samples, package
metadata, changelog, and release notes must describe Linux and Windows support
consistently for the package version that ships this feature.
