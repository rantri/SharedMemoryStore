# Contract: Docker Container Sharing

## Scope

This contract defines same-host Docker container support for SharedMemoryStore.
It does not define cross-host sharing, service discovery, persistence,
orchestration, or distributed-cache behavior.

## Supported Container Profile

A Docker deployment is supported when all participating containers:

- Run on the same Docker host.
- Can access the same shared-memory resources for the selected store name.
- Can coordinate through the same synchronization resources.
- Can evaluate owner liveness for recovery scenarios or receive safe
  unsupported/unsafe recovery outcomes.
- Have compatible permissions for shared resources.
- Have enough shared-memory capacity for the requested store size.

The validation profile should use Docker IPC namespace sharing, process
namespace sharing where owner-liveness tests require it, and an adequate shared
memory size.

## Required Cross-Container Workflows

The Docker validation path must prove:

- Container A creates a store and publishes a value.
- Container B opens the same store by name and acquires the value.
- Active leases held by one container protect storage from reuse by another
  container until release.
- Republish after final release succeeds.
- Diagnostics are readable and meaningful from a participating container.
- Explicit recovery handles stale container owners safely.
- At least 10,000 cross-container publish/acquire/release/remove cycles complete
  without premature reuse or undocumented failure.

## Unsupported Container Profiles

The package does not claim support when:

- Containers use isolated shared-memory resources but expect cross-container
  visibility.
- Containers isolate process liveness in a way that makes recovery unsafe.
- Shared memory capacity is smaller than the configured store size.
- Permissions prevent a participant from opening the region or synchronization
  resource.
- Containers run on different hosts.

Unsupported or restricted profiles must be documented and should produce
`NotFound`, `UnsupportedPlatform`, `AccessDenied`, `MappingFailed`, or a
semantically reviewed environment-capability outcome rather than corrupting data.

## Sample and Validation Requirements

The repository must provide a Docker validation path, either as a sample,
script, or both. The validation path must:

- Build or use the current package under test.
- Start at least two cooperating containers.
- Use one container as a creator/writer and another as a reader/verifier.
- Exercise lease protection and recovery.
- Fail clearly when Docker is missing or required container capabilities are not
  configured.

## Documentation Requirements

Container documentation must state:

- The support boundary is same-host shared-memory participation.
- Which Docker namespace and memory-capacity configuration is required.
- Which scenarios are unsupported.
- How to validate a deployment before relying on the package.
