# Getting Started

This guide gets a clean .NET project from package installation to a complete
create/open, publish, acquire, release, remove, and dispose workflow.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- PowerShell for repository validation scripts.
- Windows x64 for the current named memory-mapped-file validation target.

The package is prerelease `0.1.0`. If it has not been published to a package
feed, build a local package source from the repository.

## Create a Local Package Source

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Create a clean consumer project:

```powershell
dotnet new console -f net10.0 -n SharedMemoryStore.Tryout -o artifacts/tryout
dotnet add artifacts/tryout/SharedMemoryStore.Tryout.csproj package SharedMemoryStore --source artifacts/package
```

## Minimal Workflow

Replace `artifacts/tryout/Program.cs` with this program:

```csharp
using SharedMemoryStore;
using Store = SharedMemoryStore.SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = $"sms-start-{Guid.NewGuid():N}",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 2,
    MaxValueBytes = 64,
    MaxDescriptorBytes = 16,
    MaxKeyBytes = 16,
    LeaseRecordCount = 4,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, 64, 16, 16, 4)
};

var openStatus = Store.TryCreateOrOpen(options, out var store);
Console.WriteLine(openStatus);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    return 1;
}

using (store)
{
    var key = new byte[] { 1, 2, 3 };
    Console.WriteLine(store.TryPublish(key, [4, 5, 6], [9]));
    Console.WriteLine(store.TryAcquire(key, out var lease));
    Console.WriteLine(lease.ValueLength);
    Console.WriteLine(lease.Release());
    Console.WriteLine(store.TryRemove(key));
    Console.WriteLine(store.TryPublish(key, [7]));
}

return 0;
```

Run it:

```powershell
dotnet run --project artifacts/tryout/SharedMemoryStore.Tryout.csproj -c Release
```

Expected status path:

```text
Success
Success
Success
3
Success
Success
Success
```

## Next Steps

- Use [Usage](usage.md) for the full consumer workflow.
- Use [Errors](errors.md) when an operation returns a non-success status.
- Use [Diagnostics](diagnostics.md) to inspect capacity pressure and failure
  counters.
- Use [Lifecycle](lifecycle.md) to understand ownership, lease release, removal,
  stale recovery, and disposal.
- Use [samples/BasicUsage/README.md](../samples/BasicUsage/README.md) for a
  runnable repository sample.
