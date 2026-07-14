# Compatibility Matrix Results

**Feature branch**: `codex/lock-free-csharp`
**Qualification time**: 2026-07-13 06:41 UTC
**Repository base commit**: `0cf7a43f9c39de1691b237a9761035339edd0964`
(`Prepare release 1.0.2`) plus the uncommitted feature-009 worktree
**Result**: PASS for the Windows x64 compatibility matrix; Linux-specific
owner-sidecar rows are NOT QUALIFIED on this host.

## Environment

| Item | Qualified value |
|---|---|
| Host OS | Microsoft Windows 11 Pro, version `10.0.26200`, build `26200`, 64-bit |
| Process architecture | x64 |
| Processor | 13th Gen Intel Core i9-13900, 24 cores / 32 logical processors |
| .NET SDK | `10.0.201` |
| Test host runtime | .NET `10.0.5`, x64 |
| Test adapter | xUnit VSTest Adapter `3.1.4+50e68bbb8b` |
| Current package under test | `SharedMemoryStore 2.0.0` |
| Released compatibility client | `SharedMemoryStore 1.0.2` loaded from its packed NuGet assembly in an isolated `AssemblyLoadContext` |
| Native prerequisites | C++ interop agent present and executed; Python executable and C-ABI DLL present and executed |

PASS below means that the scenario actually executed on this host. NOT
QUALIFIED means that the test has an explicit platform guard and could not
exercise the claimed platform behavior here. Two Linux-only test methods return
early rather than reporting an xUnit skip, so the raw xUnit totals include them
as passed; this report deliberately does not count them as qualified passes.

## Result matrix

| Matrix lane | Executed evidence | Result |
|---|---|---|
| Current C# 2.0, layout-v1.2-only lifecycle | 12/12 legacy integration cases passed: cross-process publication, exact large value/descriptor, multi-reader leases, remove/reuse, direct reservation ingest, segmented publication, stale reservation fencing, and concurrent acquire/release/duplicate publication | PASS |
| Layout-v1.2 cross-runtime ordered pairs | 9/9 producer/consumer pairs passed for .NET, C++, and Python in both directions, including same-runtime pairs | PASS |
| Current C# 2.0, layout-v2-only lifecycle | 16/16 cases passed: publication races and bounds, 1/6/12 same/distributed-key readers, paused observer progress, 12-process lease/remove/reclaim, collision churn, and live diagnostics | PASS |
| Public/package/profile contract surface | 107/107 contract cases passed, including unchanged legacy signatures/status numbers, explicit v2 selection, layout identities, package metadata, and XML documentation | PASS |
| Existing v1.2 opened as v2 | 2/2 Windows x64 `OpenExisting`/`CreateOrOpen` header-first oversized-view cases returned `IncompatibleLayout`; existing legacy handle remained usable | PASS |
| Existing v2 opened as v1.2 | 2/2 Windows x64 `OpenExisting`/`CreateOrOpen` header-first oversized-view cases returned `IncompatibleLayout`; existing v2 handle remained usable | PASS |
| Opposite-profile `CreateNew` | 2/2 directions returned `AlreadyExists` for the same public name | PASS |
| V2 participant open/close lifecycle | 2/2 scenarios passed: capacity is consumed per handle and reusable after close; final close permits a new same-name `CreateNew` lifecycle | PASS |
| Released C# 1.0.2 against a live v2 mapping | 9/9 requested-view/open-mode combinations passed (`smaller`, `equal`, `oversized` x `CreateNew`, `OpenExisting`, `CreateOrOpen`). All attempts failed closed and the v2 payload remained readable/writable afterward | PASS |
| Default/current legacy helper against v2 | 1/1 passed: default and legacy helpers did not auto-select or reinterpret an existing v2 mapping | PASS |
| Same-name upgrade and rollback | 1/1 passed: live opposite profiles rejected one another; after all handles closed, v1.2 -> v2 -> v1.2 recreation succeeded; values did not leak across layouts and had to be republished | PASS |
| Current packed 2.0.0 consumption | Direct package-consumption script passed: clean `net10.0` project restored only from the local package, then exercised legacy publish/acquire/remove/direct-ingest/segments/recovery/disposal and explicit v2 publish/acquire/remove/profile/protocol/participant-capacity behavior. Its xUnit wrapper also passed 1/1 | PASS |
| Broker-key sample | 2/2 processes passed with 6 and 12 workers. Broker dispatch remained outside the KV store; processed/checksum counts, pending removal, missing key, and explicit zero-recovery results matched | PASS |
| C++ v1.2-only rejection of SMS2 | Actual C++ agent executed all 3 open modes, returned the required fail-closed status, never damaged payload visibility, and left a second C# v2 opener usable | PASS |
| Python v1.2-only rejection of SMS2 | Actual Python/C-ABI agent executed all 3 open modes, returned the required fail-closed status, never damaged payload visibility, and left a second C# v2 opener usable | PASS |
| Compatibility manifest | 1/1 passed: package, layout, resource protocol, C ABI, C++, and Python identities/support sets remain independently versioned | PASS |
| Linux v2 live-owner sidecar during current legacy incompatible open | Linux-only case could not execute on Windows | NOT QUALIFIED |
| Linux v2 live-owner sidecar during released-1.0.2 incompatible open | Linux-only case could not execute on Windows | NOT QUALIFIED |
| Full Linux x64 compatibility matrix | No Linux x64 runtime was used in this task | NOT QUALIFIED |

No compatibility row failed. Across the commands below, xUnit reported 172
passing cases. Of those, 170 actually executed their qualified scenario on
Windows x64 and two were Linux-guarded early returns. The direct package script
is additional executable evidence rather than an extra xUnit case.

## Package and native artifact hashes

These hashes bind the exact artifacts used or produced during this matrix. The
current package hash is from the final direct package-consumption run.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `artifacts/package/SharedMemoryStore.2.0.0.nupkg` | 100,313 | `C3B4E3441219FF0C5FBA63F89FD2EACC85C16F8E1DD845E334E35D572857D84D` |
| `artifacts/package/SharedMemoryStore.2.0.0.snupkg` | 44,078 | `EF78FDC0A9FAFDC593EEECA5AE5F88940026BE925D764C263D22FB0D81C0E7BE` |
| `%USERPROFILE%/.nuget/packages/sharedmemorystore/1.0.2/sharedmemorystore.1.0.2.nupkg` | 48,991 | `A3DB3D86AFD89CECC932D1A8D307A18A842A6CE7913C8C9107C4B404B8821100` |
| `artifacts/native-win/sms_cpp_interop_agent.exe` | 230,912 | `0BE45C93BE88AAF853B2981C1A8BA0F998CE73861540049DB2A166A45997574D` |
| `src/python/shared_memory_store/shared_memory_store.dll` | 1,588,736 | `88349499AA87CA6107B047C9C8ED36E39C16F74DE17AC948F615DFBBE96035BA` |

NuGet archives contain generated ZIP metadata, so a later repack can have a
different byte hash even when compiled inputs are unchanged. Requalification
must record the newly produced package hash rather than assume this value.

## Exact commands and counts

All commands ran from the repository root with configuration `Release`.

1. Pack gate -- completed successfully and produced both `.nupkg` and
   `.snupkg`:

   ```powershell
   dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/compatibility/current-package --no-restore
   ```

2. Full public/package/profile contract project -- 107 passed, 0 failed:

   ```powershell
   dotnet test tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj -c Release --no-restore --logger "console;verbosity=normal"
   ```

3. Same-name mixed-profile and v2 participant lifecycle -- xUnit reported 9
   passed; 8 Windows scenarios executed and one Linux-only case was not
   qualified:

   ```powershell
   dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~LockFreeProfileOpenIntegrationTests" --logger "console;verbosity=normal"
   ```

4. Released-1.0.2, participant-capacity, upgrade/rollback, and default-profile
   package matrix -- xUnit reported 13 passed; 12 Windows scenarios executed
   and one Linux-only case was not qualified:

   ```powershell
   dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~LockFreePackageIntegrationTests" --logger "console;verbosity=normal"
   ```

5. Broker-key sample validation -- 2 passed, 0 failed:

   ```powershell
   dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~LockFreeSampleValidationTests" --logger "console;verbosity=normal"
   ```

6. Packed-consumer xUnit wrapper -- 1 passed, 0 failed:

   ```powershell
   dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~PackageConsumptionIntegrationTests" --logger "console;verbosity=normal"
   ```

7. Direct clean packed-consumer validation -- exit code 0; all printed legacy
   and v2 assertions reached success:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate-package-consumption.ps1 -Configuration Release
   ```

8. Current C# layout-v1.2 lifecycle matrix -- 12 passed, 0 failed:

   ```powershell
   $classes = @('CrossPlatformStoreIntegrationTests','PublishIntegrationTests','MultiReaderAcquireIntegrationTests','RemoveReuseIntegrationTests','ZeroCopyIngestIntegrationTests','SegmentedFrameIntegrationTests','ReservationReuseSafetyIntegrationTests','AcquireReleaseConcurrencyTests')
   $filter = $classes | ForEach-Object { "FullyQualifiedName~SharedMemoryStore.IntegrationTests.$_." }
   dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-restore --filter ($filter -join '|') --logger "console;verbosity=normal"
   ```

9. Current C# layout-v2 lifecycle matrix -- 16 passed, 0 failed:

   ```powershell
   $classes = @('LockFreePublishIntegrationTests','LockFreeMultiReaderIntegrationTests','LockFreeBroadcastLeaseIntegrationTests','LockFreeChurnIntegrationTests','LockFreeDiagnosticsIntegrationTests')
   $filter = $classes | ForEach-Object { "FullyQualifiedName~SharedMemoryStore.IntegrationTests.$_." }
   dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-restore --filter ($filter -join '|') --logger "console;verbosity=normal"
   ```

10. Native/Python SMS2 rejection and manifest -- 3 passed, 0 failed. The C++
    and Python theory cases both executed rather than returning at their
    prerequisite guard:

    ```powershell
    dotnet test tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj -c Release --no-restore --filter "FullyQualifiedName~LockFreeLayoutRejectionTests" --logger "console;verbosity=normal"
    ```

11. Layout-v1.2 .NET/C++/Python 3x3 ordered exchange matrix -- 9 passed, 0
    failed:

    ```powershell
    dotnet test tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CoreExchangeMatrixTests" --logger "console;verbosity=normal"
    ```

## Conclusions

- The current package preserves a usable layout-v1.2 profile and requires
  explicit opt-in for layout 2.0.
- Same-name mixed layouts fail closed before projecting an incompatible-sized
  view; `CreateNew` retains its established `AlreadyExists` meaning.
- Released C# 1.0.2, C++ 0.1, and Python 0.1 cannot accidentally participate in
  v2 data operations. Their rejection attempts leave the live v2 store usable.
- Upgrade and rollback are correctly modeled as drain, close, recreate, and
  application-owned republish. No bytes cross a recreated layout boundary.
- The broker-key sample validates the intended separation: external code sends
  keys to workers while SharedMemoryStore remains a bounded shared-memory KV
  store.
- Windows x64 compatibility is qualified by this run. Linux x64 compatibility,
  especially v1-compatible owner-sidecar preservation, still requires a native
  Linux qualification run and must not be inferred from these results.
