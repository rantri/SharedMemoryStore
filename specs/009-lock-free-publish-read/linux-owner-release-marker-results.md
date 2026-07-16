# Linux Owner-Release Marker Qualification

## Current cold-open transaction supersession (2026-07-14)

The original runs below established bounded release-marker fallback after a
mapped handle existed. A later structural review found that failed-open safety
also requires cross-process coordination before any mapping: otherwise an older
client can expose a zero region before entering its lifecycle lock and a newer
opener can mistake those bytes for initialization authority.

The current implementation acquires `.lifecycle` and then `.lock` before
mapping, retains both through physical-disposition-aware header work and
participant registration, and releases them before any failed-open mapped-owner
cleanup. Only the physical creator initializes a zero header. The former test
named "Public failed open after mapping uses the bounded marker path" has been
replaced by `OpenBlockedBeforeMappingDoesNotPublishOwnerOrReleaseMarker`; the
current five-test Linux owner-release-marker suite passed 5/5. The expanded
profile/open matrix and exact-once cleanup tests also passed as part of the full
295/295 Linux Integration aggregate.

Each admitted mapped handle locks a private regular-file owner anchor before
committing its sidecar line. Cleanup opens candidate anchors independently with
`O_NOFOLLOW`, requires regular-file metadata and an unlocked proof, and treats
unavailable or ambiguous `statx` evidence conservatively. Tests cover crashes
before sidecar commit, durable release-marker reconciliation, malformed and
special-file artifacts, and the blocked-before-mapping window, which publishes
neither an owner line nor a marker. A 12-process cold-open fan-out is bounded by
the caller's original finite wait budget. These worktree results are diagnostic;
the immutable final Linux and release evidence paths remain the qualification
authority.

The historical results below remain useful evidence for marker durability,
mode, reconciliation, and bounded close behavior; their failed-open ordering is
not the current protocol.

**Date**: 2026-07-13
**Historical result**: PASS on Linux x64 for the pre-FR-056 protocol

## Environment

- Host: Windows x64 with Docker Engine 29.3.1, Linux containers
- Image: `mcr.microsoft.com/dotnet/sdk:10.0`
- Image ID: `sha256:4207e009b0b8c470b08db499ab86c33fcf29a2bd2849ab44c251ff1c560a0ecf`
- Repository digest: `mcr.microsoft.com/dotnet/sdk@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5`
- SDK: 10.0.301
- Test runtime: .NET 10.0.9, x64
- Isolation: repository bind-mounted read-only; source copied to an ephemeral
  2 GiB container tmpfs, so Linux build outputs did not modify the host tree

The installed WSL SDK was not qualified because its workload-manifest resolver
failed with `MSB4242` before project evaluation. The Docker result below is the
Linux-x64 qualification evidence.

## Command

```powershell
$repo=(Get-Location).Path
docker run --rm --mount "type=bind,source=$repo,target=/src,readonly" --tmpfs /work:exec,size=2g -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc 'tar -C /src --exclude=.git --exclude=bin --exclude=obj --exclude=artifacts -cf - . | tar -C /work -xf - && dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter FullyQualifiedName~LinuxOwnerReleaseMarkerIntegrationTests --logger "console;verbosity=normal"'
```

## Results

The initial historical three-test run passed 3/3:

| Test | Duration | Result |
|---|---:|---|
| Concurrent blocked releases publish distinct private markers without loss | 292 ms | PASS |
| Final-owner marker permits `CreateNew` while the releasing process remains alive | 256 ms | PASS |
| Held `.lifecycle` bounds dispose, preserves sibling use, and removes only the exact ghost | 268 ms | PASS |

After adding malformed-finalized-marker fail-closed coverage, the historical run
at that checkpoint passed 4/4 in 1.5517 seconds. That pre-FR-056 suite then added
the old failed-after-mapping path and passed 5/5 in 2.7571 seconds:

| Test | Duration | Result |
|---|---:|---|
| Concurrent blocked releases publish distinct private markers without loss | 323 ms | PASS |
| Final-owner marker permits `CreateNew` while the releasing process remains alive | 261 ms | PASS |
| Public failed open after mapping uses the bounded marker path (historical; superseded) | 873 ms | PASS |
| Malformed finalized marker rejects open and remains present | 1 ms | PASS |
| Held `.lifecycle` bounds dispose, preserves sibling use, and removes only the exact ghost | 259 ms | PASS |

All marker files observed by the tests had Unix mode `0600`. The ordinary
`.lock` remained independently acquirable while `.lifecycle` was held. The
eight concurrent disposals completed in one bounded interval and produced eight
distinct exact-owner markers; the next opener reconciled all of them without a
lost release.

A neighboring Linux regression run filtered to
`LinuxOwnerReleaseMarkerIntegrationTests`,
`LockFreeProfileOpenIntegrationTests`, and
`MultiStoreLifecycleIntegrationTests` passed 17/17 with no skips in one second.

## Historical conclusion

This checkpoint established that Linux close/open-failure region teardown no
longer had an infinite lifecycle-lock wait. Its mapped-after-failure ordering is
superseded by the current blocked-before-mapping cold transaction above. The
durability conclusion remains applicable: a permitted close fallback publishes
a replayable exact-owner marker through same-directory temporary-file rename,
and reconciliation orders raw owner-line removal plus atomic sidecar rewrite
before marker deletion.
