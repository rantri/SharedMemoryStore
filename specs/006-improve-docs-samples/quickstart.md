# Quickstart: Validate Documentation and Samples Excellence

## Prerequisites

- .NET 10 SDK available on PATH.
- PowerShell available on PATH.
- Repository checked out on a platform supported by the current
  SharedMemoryStore validation scope.

## 1. Validate Documentation Inventory and Links

```powershell
scripts/validate-docs.ps1
```

Expected outcome:

- Required root docs, guide docs, sample READMEs, GitHub templates, package
  metadata, and contract links are present.
- Public docs have no unresolved placeholders.
- Relative Markdown links resolve.
- README, package metadata, changelog, release notes, support, security, and
  packaging guidance align.

## 2. Build the Solution

```powershell
dotnet build SharedMemoryStore.slnx -c Release
```

Expected outcome:

- Core package, tests, benchmarks, and sample projects build with the current
  public package surface.
- Stale type names, method names, option names, or status names in samples fail
  the build.

## 3. Run the Sample Ladder

```powershell
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
```

Expected outcome:

- Each sample completes successfully in the supported validation environment.
- Output shape matches the corresponding sample README.
- Each README explains audience, purpose, concepts demonstrated, prerequisites,
  run command, expected output, cleanup, related docs, and expected
  non-success statuses.

## 4. Validate Package Consumption

```powershell
scripts/validate-package-consumption.ps1
```

Expected outcome:

- A local package is created.
- A clean consumer project installs the package from the local source.
- The consumer workflow compiles and runs against the packaged public surface.
- Package README and metadata are included correctly.

## 5. Run the Test Suite

```powershell
dotnet test SharedMemoryStore.slnx -c Release
```

Expected outcome:

- Unit, contract, and integration tests pass.
- Documentation and samples remain aligned with the runtime behavior already
  covered by existing tests.

## 6. Pack the Release Artifact

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Expected outcome:

- Package creation succeeds.
- Package metadata, package README, release notes, and license information are
  aligned with public documentation.

## 7. Manual Documentation Review

Review the documentation against these checks:

- A first-time user can find purpose, non-goals, install path, minimal workflow,
  and next step in under 10 minutes.
- Every public workflow listed in `spec.md` FR-004 has a guide, outcome
  explanation, and example or sample link.
- Every outcome category listed in `spec.md` FR-006 appears in troubleshooting
  or feature documentation.
- Maintainer docs explain architecture, invariants, performance evidence rules,
  validation commands, and release documentation responsibilities.
- Performance and platform statements are scoped and evidence-bounded.
- Future C++ and Python notes remain future portability considerations, not
  delivered binding claims.

Expected outcome:

- Manual review finds no unsupported behavior claims, contradictory public
  statements, stale API/status names, or unclear reader journeys.
