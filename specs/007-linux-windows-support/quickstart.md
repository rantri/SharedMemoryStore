# Quickstart: Linux, Windows, and Docker Support Validation

This guide defines the validation path for the feature. Commands are written so
they can be used by implementers and release reviewers after the feature is
built.

## Prerequisites

- .NET 10 SDK.
- PowerShell 7 (`pwsh`) on Linux and Windows for repository scripts.
- Docker Engine or Docker Desktop with Compose support for Docker validation.
- Sufficient shared-memory capacity for the configured Docker sample.

## 1. Validate the Active Feature Context

```powershell
Get-Content .specify/feature.json
Get-Content specs/007-linux-windows-support/spec.md
Get-Content specs/007-linux-windows-support/plan.md
```

Expected outcome:

- `.specify/feature.json` points to `specs/007-linux-windows-support`.
- The spec and plan describe Linux, Windows, and same-host Docker support.

## 2. Build and Test on Windows

```powershell
dotnet restore
dotnet build SharedMemoryStore.slnx -c Release
dotnet test SharedMemoryStore.slnx -c Release
pwsh ./scripts/validate-docs.ps1
pwsh ./scripts/validate-package-consumption.ps1
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Expected outcome:

- Build, tests, docs validation, package-consumption validation, and pack all
  pass.
- Existing Windows runtime behavior remains compatible.

## 3. Build and Test on Linux

```powershell
dotnet restore
dotnet build SharedMemoryStore.slnx -c Release
dotnet test SharedMemoryStore.slnx -c Release
pwsh ./scripts/validate-docs.ps1
pwsh ./scripts/validate-package-consumption.ps1
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Expected outcome:

- Valid store creation and opening do not return unsupported-platform outcomes.
- Package-consumption validation completes the first-use and advanced workflows.
- Scripts use portable paths and shell invocation.

## 4. Run Samples on Both Host Platforms

```powershell
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
```

Expected outcome:

- Samples exit successfully on Linux and Windows.
- Sample READMEs describe expected output and supported platform behavior.

## 5. Validate Docker Cross-Container Sharing

Use the Docker validation wrapper or the Docker sample once implemented:

```powershell
pwsh ./scripts/validate-docker-shared-memory.ps1
```

Equivalent direct sample command:

```powershell
docker compose -f samples/DockerSharedMemory/docker-compose.yml up --build --abort-on-container-exit --exit-code-from verifier
docker compose -f samples/DockerSharedMemory/docker-compose.yml down --volumes
```

Expected outcome:

- One container creates the store and publishes values.
- A second container opens the same store by name and reads the values.
- Active leases protect storage across containers.
- Diagnostics and explicit recovery produce documented outcomes.
- The validation runs at least 10,000 cross-container
  publish/acquire/release/remove cycles.

## 6. Validate Unsupported Container Configuration

Run the negative Docker profile once implemented:

```powershell
pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Isolated
```

Expected outcome:

- Isolated containers do not silently pass as a supported shared-store
  deployment.
- The failure identifies missing shared-resource capabilities or produces a
  documented public outcome such as not found, unsupported platform, access
  denied, mapping failed, or an approved environment-capability status.

## 7. Release Evidence

Before release, capture the following in release notes or maintainer docs:

- Windows validation result.
- Linux validation result.
- Docker same-host validation result.
- Unsupported scenario review.
- Compatibility review for any public API, status, layout, package metadata, or
  documentation change.
