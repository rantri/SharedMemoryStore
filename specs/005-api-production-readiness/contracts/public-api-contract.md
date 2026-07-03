# Contract: Public API Identity and Compatibility

## Primary Identity

The production primary store type is:

```csharp
namespace SharedMemoryStore;

public sealed class MemoryStore : IDisposable
{
    public static StoreOpenStatus TryCreateOrOpen(
        SharedMemoryStoreOptions options,
        out MemoryStore? store);
}
```

Consumers import the root package namespace and reference `MemoryStore`
directly. Public examples must not require aliases to avoid a namespace/type
collision.

## Breaking Migration

The previous pre-release primary type:

```csharp
SharedMemoryStore.SharedMemoryStore
```

is replaced by:

```csharp
SharedMemoryStore.MemoryStore
```

Migration notes must cover:
- Type rename from `SharedMemoryStore` to `MemoryStore`.
- Any namespace changes, if implementation work discovers additional public
  namespace cleanup is required.
- Any renamed statuses, diagnostics members, options members, reservation
  members, or integration members.
- The target package version and semantic version impact.

## Semantic Version Impact

This feature changes public API names and behavior before broad production
release. Per the constitution, breaking public API corrections require a major
contract step. The production-readiness release must document the change as the
1.0 public API contract or another explicitly approved major version.

## Public Example Contract

Every public quickstart, README snippet, sample, and package-consumption test
must compile with this shape:

```csharp
using SharedMemoryStore;

var options = SharedMemoryStoreOptions.Create(
    name: "example-store",
    slotCount: 128,
    maxValueBytes: 4096,
    maxDescriptorBytes: 128,
    maxKeyBytes: 128,
    leaseRecordCount: 128);

var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
```

Exact helper names may change during implementation, but examples must use the
final production API without aliases.

## Compatibility Tests

Contract tests must verify:
- Public examples compile in a new consumer project.
- The primary public type has no namespace/type collision.
- Package metadata, XML docs, release notes, and README use the final identity.
- Obsolete or removed pre-release names are documented in migration notes.
