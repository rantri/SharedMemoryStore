# Portability

The qualified SMS2 target is little-endian x86-64 on Windows and Linux. All
three distributions share the same mapped layout, 64-bit atomic contract,
resource protocol, owner-classification rules, and status values.

Qualification requirements are normative in the
[protocol conformance](../specs/010-lock-free-only-multilang/contracts/protocol-conformance.md)
and
[interoperability](../specs/010-lock-free-only-multilang/contracts/interoperability-and-validation.md)
contracts.

## Support matrix

| Host | Managed `3.0.0` | C++ `1.0.0` / ABI `2.0` | Python `1.0.0` | Qualification |
|---|---|---|---|---|
| Windows x64 | supported | supported | supported | build, lifecycle, crash/recovery, interop, package |
| Linux x64 | supported | supported | supported | build, lifecycle, crash/recovery, interop, package, Docker |
| 32-bit process | rejected | rejected | rejected through native ABI | unsupported |
| non-x64 architecture | rejected unless separately qualified | rejected unless separately qualified | follows native library | unsupported by this release |
| big-endian host | rejected | rejected | follows native library | unsupported |

An unsupported process returns `UnsupportedPlatform` before creating or opening
mapped data whenever the required platform property can be checked first.

## Why x86-64 is explicit

SMS2 depends on naturally aligned cross-process 64-bit atomics with the memory
ordering pinned by the protocol manifest. Language-level “atomic” support is not
enough; the implementation must demonstrate that the actual generated
operations are lock-free, correctly aligned, visible across mapped processes,
and safe under pause/crash schedules.

Adding another architecture requires executable evidence for every managed and
native atomic used by the protocol, not merely a successful compile.

## Windows resources

The public name identifies the memory-mapped region. Resource protocol `2`
derives one cold synchronization name in `Local\` or `Global\` scope according
to the canonical rules in
[`resource-naming-v2.md`](../protocol/resource-naming-v2.md).

Operational requirements:

- every process must resolve the same session/global scope;
- identities must have compatible mapping and mutex access;
- services and interactive users must choose scope deliberately; and
- endpoint security policy must allow named mapping operations.

Windows kernel object lifetime removes the mapping and cold synchronization
resource after their final handles close. An already-open store's hot operations
do not acquire the named cold mutex.

## Linux resources

Linux uses `/dev/shm/SharedMemoryStore` when `/dev/shm` is available, otherwise a
guarded directory below the OS temporary path. The root must be a real directory
with safe ownership and mode. Resource protocol `2` derives a readable fragment
plus SHA-256 suffix and creates:

- `.region`: mapped data;
- `.lock`: stable cold-open rendezvous;
- `.lifecycle`: owner reconciliation and final cleanup;
- `.owners`: exact live-owner sidecar;
- private `.owners.anchor.<guid>` files; and
- finalized `.owners.released.<guid>.ready` close markers.

Files and directories are verified as non-symbolic-link objects of the expected
type. Linux requires open-file-description record locks for the cold lifecycle.
If the kernel/filesystem cannot provide them, open fails with
`UnsupportedPlatform` rather than silently weakening the contract.

The stable lock files may remain after the final store closes; the data region
and owner evidence are removed only after exact final-owner cleanup.

## Containers and PID namespaces

Cross-container sharing is supported only when all participants intentionally
share:

- the same IPC/mapped resource namespace;
- the same resource root mount;
- compatible user/group identity and file modes;
- compatible PID namespace or canonical PID namespace identity evidence; and
- the same public name and store capacities.

A default-isolated container must fail clearly; it must not appear to join a
different store with the same text name.

SMS2 records PID namespace identity in the header and participant record.
Linux owner anchors provide additional liveness evidence across PID namespace
boundaries. Mixed or unproven namespace evidence is retained conservatively and
is never assumed stale.

See [the Docker sample](../samples/DockerSharedMemory/README.md) for supported,
recovery, contention, disposal-race, clean-consumer, and isolation validation
modes.

## Owner identity and PID reuse

A PID alone is not ownership evidence. Participant identity includes process
start evidence, open sequence, participant generation, and namespace identity.
Linux additionally uses a privately locked owner anchor and exact sidecar line.

Recovery may reclaim a participant or token only after conservative owner
classification and unchanged-state revalidation. Live, unsupported,
inconsistent, or changing evidence is retained.

## Filesystems and mounts

Linux deployments must preserve the semantics of regular files, memory mapping,
atomic rename, flush, `flock`, and open-file-description record locks. Avoid
network filesystems, synthetic mounts, or bind mounts that do not preserve
those guarantees.

The resource root should not be writable by unrelated users. SharedMemoryStore
is designed for trusted same-host participants with OS-level access to the same
resources. It does not defend against a malicious process that can legitimately
modify the mapped bytes.

## Application byte portability

The library treats keys, descriptors, and payloads as opaque bytes. Cross-
language applications must define their own canonical representation:

- integer width and little-endian encoding;
- floating-point format when used;
- text encoding and normalization policy;
- structure/version tags;
- optional/checksum semantics; and
- ownership of schema evolution.

Embedded NUL is valid. Never use platform-sized C/C++ structures or process
pointers as shared application payloads.

## Runtime and package boundaries

The NuGet package contains only the managed implementation. The CMake package
contains the C ABI/native library and C++ headers. The Python wheel contains the
Python modules plus an adjacent platform native library.

Python does not search the current directory or system library paths. A wheel
for one OS/architecture cannot be moved to another target. Native and Python
artifacts must be rebuilt for each qualified target even though the mapped
bytes remain identical.

## Wait and clock behavior

Wait budgets are process-local and use monotonic elapsed-time measurement. They
do not store wall-clock timestamps in mapped state. Scheduler latency and
container CPU limits can still cause a finite call to return `StoreBusy` even
while the system as a whole makes progress.

Cancellation handles/tokens are local runtime objects. They are never written
to shared memory and are not portable across processes.

## Crash and cleanup behavior

Hot state transitions are generation-fenced and helpable. A process crash may
leave a published transition that another participant can complete after exact
validation. It cannot leave a process-owned hot mutex that every participant
must acquire.

Cold owner cleanup is platform-specific:

- Windows relies on kernel handle lifetime.
- Linux reconciles exact owner lines, locked anchors, and finalized release
  markers under the lifecycle lock.

Neither platform promises durable persistence across reboot. The store is IPC
state, not an application database.

## Deployment replacement

A current runtime rejects a noncurrent or malformed mapping before payload
access. There is no portable in-place conversion. Stop work, drain tokens, close
all handles, remove or replace the physical resources, create fresh SMS2
resources, and republish from an application-owned authoritative source.

## Qualification commands

```powershell
dotnet test SharedMemoryStore.slnx -c Release
pwsh ./scripts/validate-native.ps1 -Configuration Release
pwsh ./scripts/validate-python.ps1 -Configuration Release
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile All -Configuration Release
```

`-Profile` in the Docker command selects a validation scenario; it is not a
store protocol selector.

## Related guides

- [Architecture](architecture.md)
- [Packaging](packaging.md)
- [Errors](errors.md)
- [Security policy](../SECURITY.md)
- [Resource protocol 2](../protocol/resource-naming-v2.md)
