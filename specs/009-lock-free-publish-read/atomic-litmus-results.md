# Layout-v2 mapped atomic qualification

Date: 2026-07-13

## Development host

- OS: Windows 10.0.26200
- Architecture/RID: x64 / `win-x64`
- .NET SDK: 10.0.201
- .NET runtime: 10.0.5
- Configuration: Release

## Command

```powershell
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~MappedAtomicLitmusIntegrationTests --nologo
```

## Result

- Passed: 4
- Failed: 0
- Skipped: 0
- Duration: 11 seconds

The aligned mapped publication, cross-view CAS, and two-word sequentially
consistent Dekker tests all passed across child processes. The forbidden
old/old Dekker outcome was not observed. The x64 development gate therefore
allows local layout-v2 implementation to continue.

This result does not replace the separate Windows/Linux x64 release gate in
T086.
