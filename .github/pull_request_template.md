# Pull Request

## Summary

- 

## Motivation

- 

## Impact

- Behavior impact:
- Public API impact:
- Package metadata impact:
- Runtime dependency impact:
- Compatibility or semantic-version impact:
- Security or support impact:
- Release note impact:

## Validation

- [ ] `scripts/validate-docs.ps1`
- [ ] `scripts/validate-package-consumption.ps1`
- [ ] `dotnet test -c Release`
- [ ] `dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package`
- [ ] `dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release`
- [ ] `dotnet run --project samples/FrameValue/FrameValue.csproj -c Release`

## Documentation

- [ ] README or docs updated when public behavior, package metadata, support, security, or compatibility changed.
- [ ] Contract docs updated when public API, status, layout, lifecycle, diagnostics, or portability behavior changed.
- [ ] CHANGELOG or release guidance updated when release-facing claims changed.

## Linked Issue or Rationale

- 
