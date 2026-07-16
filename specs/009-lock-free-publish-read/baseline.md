# Legacy Release Baseline

**Captured**: 2026-07-13
**Branch**: `codex/lock-free-csharp`
**Source commit**: `0cf7a43f9c39de1691b237a9761035339edd0964`
**Environment**: Microsoft Windows 10.0.26200, x64, .NET SDK 10.0.201

The working tree contained only feature-009 specification/context changes when
this baseline was executed; production and existing test sources matched the
source commit.

## Release tests

Command:

```powershell
dotnet test SharedMemoryStore.slnx -c Release --nologo
```

Result: PASS, 226 passed, 0 failed, 0 skipped.

| Suite | Passed | Duration reported by test host |
|---|---:|---:|
| SharedMemoryStore.UnitTests | 62 | 1 s |
| SharedMemoryStore.ContractTests | 43 | 200 ms |
| SharedMemoryStore.IntegrationTests | 49 | 8 s |
| SharedMemoryStore.InteropTests | 72 | 2 s |

## Release package

Command:

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release --nologo --no-restore
```

Result: PASS.

| Artifact | SHA-256 |
|---|---|
| `SharedMemoryStore.1.0.2.nupkg` | `7061A7CC16761E3920DD94BB527468AD96BA6D4B0CA038727503CA03423D2E59` |
| `SharedMemoryStore.1.0.2.snupkg` | `4D07328C95B7D960DC06BF7669D11196D58F45BE5A782D156B3F5753ABFC5B9F` |

## Public contract snapshot

The legacy package exposes:

- `MemoryStore` with the two `TryCreateOrOpen` overloads; simple/segmented
  publish; reservation; acquire/release; remove; lease/reservation recovery;
  diagnostics; wait-policy overloads; and disposal.
- `SharedMemoryStoreOptions` with the existing five-dimension
  `CalculateRequiredBytes(...)` and `Create(...)` signatures.
- `OpenMode`, `StoreOpenStatus` numeric values 0-10, and `StoreStatus` numeric
  values 0-22.
- Value-type `ValueReservation`, `ValueLease`, `StoreWaitOptions`, recovery
  options/reports, and `DiagnosticsSnapshot`.
- Mapped layout 1.2/resource naming 1 and package version 1.0.2.

Existing contract/API reflection tests are the executable detailed snapshot and
must remain green for the legacy profile throughout the feature.

## Post-facade compatibility checkpoint

**Captured**: 2026-07-13 after T013/T016 platform extraction

The legacy profile was rerun after introducing the engine-neutral facade,
opaque public-token handles, the CAS lifetime gate, and actual-capacity mapping
discovery:

| Gate | Result |
|---|---:|
| Legacy contract tests | 43 passed |
| Unit tests (legacy plus facade/lifetime contracts) | 71 passed |
| Legacy integration tests | 49 passed |
| Interop tests | 72 passed |
| Package-consumption integration | 2 passed |
| Release pack | succeeded |

There were no failures or skips. The existing five-argument sizing helper,
`Create(...)`, layout-v1.2 behavior, resource protocol, and status numeric
assignments remain unchanged.
