# Sample Validation Matrix

This matrix records the runnable sample ladder, README contract coverage, and
command validation expectations.

## Sample Ladder

| Order | Sample | Audience | Concepts demonstrated | Run command | Expected success shape | Status |
|-------|--------|----------|-----------------------|-------------|------------------------|--------|
| 1 | `samples/BasicUsage` | First-time consumer | Store options, create/open, canonical integer key bytes, fixed descriptor bytes, publish, acquire, lease release, remove, reuse, diagnostics | `dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release` | Open succeeds, publish/acquire/release/remove/reuse succeed, value bytes are visible, free slot count is printed | Ready |
| 2 | `samples/FrameValue` | Intermediate consumer | Descriptor metadata, opaque payload bytes, multiple readers, `RemovePending`, slot reuse, frame neutrality | `dotnet run --project samples/FrameValue/FrameValue.csproj -c Release` | Frame descriptor round-trips, two readers see same value, removal is pending until readers release, non-frame value reuses storage | Ready |
| 3 | `samples/ZeroCopyIngest` | Advanced producer | Reservation, chunked writes, exact advance, commit, abort, segmented publish, pipeline adapter, reader acquire | `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release` | Stream commit succeeds, reader output matches bytes, abort hides partial value, segmented and pipeline publish paths succeed | Ready |
| 4 | `samples/HostedServiceIntegration` | Application owner | Optional lifecycle wrapper, option validation, health diagnostics, explicit recovery, shutdown cleanup | `dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release` | Start, publish, health, lease recovery, reservation recovery, and stop complete with success statuses | Ready |

## README Contract Coverage

| Sample README | Purpose and audience | Concepts | Prerequisites | Run command | Expected output | Non-success statuses | Cleanup | Related docs | Non-goals |
|---------------|----------------------|----------|---------------|-------------|-----------------|----------------------|---------|--------------|-----------|
| `samples/BasicUsage/README.md` | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered |
| `samples/FrameValue/README.md` | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered |
| `samples/ZeroCopyIngest/README.md` | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered |
| `samples/HostedServiceIntegration/README.md` | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered | Covered |

## Focused Commands

| Command | Purpose | Expected output shape |
|---------|---------|-----------------------|
| `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- socket` | Direct length-prefixed stream reservation ingest only | `stream commit: Success` |
| `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- pipeline` | Pipeline adapter to `TryPublishSegments` | `pipeline publish: Success`, copied bytes, reader acquire/release/remove success |
| `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- reader` | Seed and acquire a stored value | Seed success, reader acquire success, descriptor/value bytes, release/remove success |
| `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- segmented` | Segmented `ReadOnlySequence<byte>` publication | Segmented publish success, copied bytes, reader acquire/release/remove success |
| `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- abort` | Abort cleanup for an incomplete reservation | Reserve success, abort success, acquire returns `NotFound` |

## Final Validation Results

| Validation item | Result |
|-----------------|--------|
| `dotnet build SharedMemoryStore.slnx -c Release` builds sample projects | Passed with 0 warnings and 0 errors. |
| BasicUsage command output matches README | Passed. Output showed publish/acquire/release/remove/reuse success and `free slots: 1`. |
| FrameValue command output matches README | Passed. Output showed frame descriptor round-trip, two readers, `RemovePending`, reuse, and non-frame byte length. |
| ZeroCopyIngest command output matches README | Passed. Output showed direct commit/read/remove, abort cleanup with `NotFound`, segmented publish/read/remove, and pipeline publish/read/remove. |
| HostedServiceIntegration command output matches README | Passed. Output showed start, publish, healthy diagnostics, lease recovery, reservation recovery, and stop success. |
| `scripts/validate-docs.ps1` checks README contract sections and sample links | Passed. |
| `scripts/validate-package-consumption.ps1` validates package-source first-use workflow | Passed. |
