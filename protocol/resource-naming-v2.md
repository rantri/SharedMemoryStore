# Shared Resource Protocol 2

Resource protocol 2 defines the physical identity, cold synchronization,
ownership evidence, and cleanup used by every SMS2 implementation. Hot store
operations never enter these operating-system synchronization resources.

Public names are nonblank strings of 1 through 240 UTF-16 code units, contain
no NUL, and are encoded as strict UTF-8 without Unicode normalization. The
canonical vectors are in [the v2 manifest](fixtures/v2.0/manifest.json).

## Windows resources

The memory-mapped region name is the public name unchanged. The cold
synchronization name is:

```text
<scope>SharedMemoryStore-<sanitized-public-name>
```

`scope` is `Global\` when the public name begins with `Global\` by an ordinal,
case-insensitive comparison; otherwise it is `Local\`. The scope text in the
public name remains part of the sanitized suffix. Sanitization processes UTF-16
code units: letters, decimal digits, `-`, and `_` are retained and every other
unit becomes `_`.

The named mutex is acquired before physical create/open and remains held through
mapping inspection, creator-only initialization or complete validation, and
participant registration. Windows kernel lifetime removes both named resources
after their final handles close.

## Linux resources

The root is `/dev/shm/SharedMemoryStore` when `/dev/shm` exists; otherwise it is
`SharedMemoryStore` below the operating-system temporary directory. The root is
a real directory, never a symbolic link, and is mode `0700`.

The resource fragment is:

```text
sms-<readable>-<digest>
```

`readable` is formed from UTF-16 code units by retaining ASCII letters, ASCII
digits, `-`, `_`, and `.`, replacing every other unit with `_`, trimming leading
and trailing `_` and `.`, substituting `store` when empty, and truncating to 80
code units. `digest` is the lowercase hexadecimal form of the first eight bytes
of SHA-256 over the strict UTF-8 public name.

| Suffix | Purpose |
|---|---|
| `.region` | Mapped data file |
| `.lock` | Stable cold-open rendezvous |
| `.owners` | Exact live-owner sidecar |
| `.lifecycle` | Owner reconciliation, physical create/open, and final cleanup |
| `.owners.anchor.<guid>` | Private live-owner anchor |
| `.owners.released.<guid>.ready` | Finalized bounded-close release marker |

Directories and files are created with mode `0700` and `0600` respectively.
Existing objects are verified to be the expected non-symbolic-link type before
use.

Both `.lifecycle` and `.lock` use a nonblocking record lock on byte range
`[0, 1)`, retried within the caller's one wait/cancellation budget. Implementations
use `F_OFD_SETLK` and return `UnsupportedPlatform` if it is unavailable. A
process-local non-reentrant gate serializes calls sharing one descriptor. The
lock byte range is released before that local gate; an unlock failure retires
the descriptor before reopening the gate.

Cold-open ordering is:

1. acquire `.lifecycle`;
2. reconcile finalized release markers and conservatively filter stale owners;
3. decide physical create/open disposition and open the persistent `.lock` inode;
4. acquire `.lock`;
5. create or map the region at its actual capacity;
6. create and lock the private owner anchor, then atomically append the exact
   owner line;
7. perform creator-only SMS2 initialization or complete existing-header
   validation;
8. register the participant record through `Registering` to `Active`;
9. release `.lock`, then `.lifecycle`.

Failure cleanup releases both gates and the ordinary synchronization descriptor
before releasing mapping/owner state that may re-enter lifecycle coordination.
The `.lock` and `.lifecycle` files remain stable rendezvous inodes even with no
live store.

Only a physical creator may clear and initialize the mapped region. An opener
never treats an existing zero or malformed header as empty. `CreateNew` reports
`AlreadyExists` for an existing physical store; `OpenExisting` reports
`NotFound` when none exists; a noncurrent or incompatible header is rejected
before payload access. The caller's original budget covers this entire cold
transaction.

## Linux owner evidence

Each handle commits one line:

```text
decimal-pid:proc-start-token:32-lowercase-hex-owner-guid
```

Before publishing it, the handle creates its exact mode-`0600` anchor path and
holds an exclusive open-description `flock` until its mapped view is gone and
owner release is safely recorded. A lifecycle reader opens the referenced
anchor separately with `O_NOFOLLOW`: lock contention proves the owner live even
across PID namespaces; successful lock acquisition proves it stale. Missing or
ambiguous evidence falls back to exact PID/start-token/namespace classification
and is retained conservatively when liveness cannot be proven.

Close unmaps first and waits at most 250 milliseconds to remove its ordinal-exact
owner line under `.lifecycle`. If that cannot complete, it writes the exact line
to a unique flushed temporary file and atomically renames it to:

```text
<fragment>.owners.released.<owner-guid>.ready
```

Under `.lifecycle`, an opener or releaser reconciles finalized markers before
liveness filtering: validate filename/content GUID equality, remove only the
ordinal-exact line, atomically replace `.owners`, then delete the marker. Replay
after a crash is idempotent. Malformed markers fail the cold operation closed.

After the sidecar replacement commits, orphan-anchor cleanup enumerates only
canonical names for this store. It removes an unreferenced anchor only while a
separately opened, regular, non-symbolic-link descriptor holds a nonblocking
exclusive `flock`. Referenced, locked, malformed, non-regular, or ambiguous
artifacts and all access errors are retained conservatively. Final-owner cleanup
deletes the data region and exact marker artifacts only after the empty owner
set commits.

## Hot data paths

After open, publish, segmented publish, reserve/advance/commit/abort, acquire,
projection, release, remove, reclaim, recovery, and diagnostics use only mapped
64-bit atomics, immutable bytes, bounded scans, and helpable descriptors. No
named mutex, semaphore, record lock, or private anchor is reachable from these
paths.
