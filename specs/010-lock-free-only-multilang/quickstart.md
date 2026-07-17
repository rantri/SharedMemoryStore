# Quickstart: Validate the Lock-Free-Only Multi-Language Store

This guide is the executable acceptance path for feature 010. It validates one
SMS2 protocol through the managed, native, Python, and mixed-runtime public
surfaces. Run from the repository root on a supported x64 host.

## Prerequisites

- .NET 10 SDK
- CMake 3.20+ with an x64 C++20 compiler
- Python 3.10+
- PowerShell 7
- Docker for the container interoperability row
- On Linux release qualification: `strace`, `/proc`, and a filesystem/kernel
  supporting the documented OFD-lock and owner-evidence contract

## 1. Static single-protocol checks

```powershell
rg -n "StoreProfile|layout-v1\.2|SMS1" src protocol samples benchmarks scripts tests
dotnet build SharedMemoryStore.slnx -c Release
```

Expected after implementation: product paths contain no public profile selector
or creatable SMS1 path. Any intentional retired-layout magic is confined to a
fail-closed rejection test/helper and is documented there.

## 2. Managed suite and package consumer

```powershell
dotnet test SharedMemoryStore.slnx -c Release --no-restore
pwsh ./scripts/validate-package-consumption.ps1 -Configuration Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release --no-build
```

Expected: every managed unit, contract, integration, interop,
linearizability, and sample test passes; the clean consumer creates SMS2 without
a profile option; the package is version 3.0.0.

## 3. Native C ABI/C++ package

```powershell
pwsh ./scripts/validate-native.ps1 -Configuration Release
```

Expected: CMake configures only on a qualified x64 toolchain, raw mapped-atomic
and SMS2 protocol tests pass, C ABI 2.0 and RAII tests pass, installation works,
and a clean `find_package` consumer runs against the installed library.

## 4. Python wheel and clean import

```powershell
pwsh ./scripts/validate-python.ps1 -Configuration Release
```

Expected: the wheel contains Python modules plus the adjacent ABI-2 native
library, imports outside the source tree, exposes SMS2/mask 7, and passes store,
lifetime, threading, recovery, diagnostics, and installed-package tests without
a third-party runtime dependency.

## 5. All nine ordered runtime pairs

```powershell
pwsh ./scripts/validate-interoperability.ps1 -Configuration Release -Stress `
  -StressValueCount 1000 -StressLifecycleCycleCount 10000
```

Expected: C#→C#, C#→C++, C#→Python, C++→C#, C++→C++, C++→Python,
Python→C#, Python→C++, and Python→Python all exchange exact bytes and pass
reservation, lease, pending removal, reuse, contention, crash recovery, and
diagnostics scenarios using one SMS2 mapping.

## 6. Container and platform evidence

```powershell
pwsh ./scripts/validate-interoperability.ps1 -Configuration Release -Docker -Stress
pwsh ./scripts/validate-lock-free-os.ps1 -Command all -Configuration Release
```

Run the second command independently on Windows x64 and inside the qualified
Linux x64 environment. Expected: creation authority, owner lifecycle, raw
memory ordering, held-cold-lock/no-hot-lock traces, pause/crash recovery,
samples, and packaging all pass.

## 7. Documentation and final release gate

```powershell
pwsh ./scripts/validate-docs.ps1
$runId = "010-$(Get-Date -AsUTC -Format yyyyMMddTHHmmssZ)"
$platform = if ($IsWindows) { 'windows-x64' } else { 'linux-x64' }
foreach ($tier in @('pr', 'nightly', 'release')) {
  pwsh ./scripts/run-lock-free-qualification.ps1 -Tier $tier `
    -OutputDirectory "artifacts/010-qualification/$runId/$tier" `
    -EvidenceRunId $platform
}
```

Expected: the compatibility manifest advertises only SMS2 for current
distributions, migration says drain/close/recreate/republish, every required
gate passes, and no qualification predicate depends on executing a legacy
engine.

Run the loop on both qualified hosts using the same clean commit and `$runId`,
then place the two platform trees under the reserved paths shown above. After
an independent reviewer produces the revision-bound JSON review described in
`release-qualification.md`, generate and revalidate the cross-platform rollup:

```powershell
pwsh ./scripts/finalize-lock-free-qualification.ps1 -RunId $runId `
  -CodeReviewPath artifacts/reviews/010-code-review.json
```

## Migration smoke

1. Create an old mapping with a preserved historical binary.
2. Close all old handles.
3. Attempt to open it with each current distribution and observe
   `IncompatibleLayout` without payload access.
4. Let the old lifecycle remove it or explicitly remove it using the documented
   application deployment procedure.
5. Create the same public name using any current distribution.
6. Republish application-owned values and open/read them from the other two
   distributions.

No step converts mapped bytes or permits old and current writers to share one
live mapping.
