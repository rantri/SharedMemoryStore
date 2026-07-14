# Platform Resource Naming Version 1

Mapped-layout compatibility is not enough for live interoperability. Every
participant must derive the same mapping, synchronization, ownership, and
lifecycle resources from the same public store name and must participate in the
same lock protocol.

Public names are nonblank .NET-compatible strings of 1 through 240 UTF-16 code
units and must not contain NUL. No Unicode normalization is performed. Hashing
uses the UTF-8 encoding of the public name. Sanitization is defined over UTF-16
code units to reproduce the managed baseline exactly, including two replacement
characters for a supplementary Unicode scalar represented by a surrogate pair.

## Windows

The memory-mapped region name is the public name unchanged.

The synchronization name is:

```text
<scope>SharedMemoryStore-<sanitized-public-name>
```

`scope` is `Global\` when the public name begins with `Global\` using an
ordinal, case-insensitive comparison; otherwise it is `Local\`. The scope text
inside the public name is not removed before sanitization. For each UTF-16 code
unit, keep a Unicode letter, a decimal digit, `-`, or `_`; replace every other
unit with `_`. Thus dots and separators are replaced, while BMP letters such as
`é` and `共` remain. The exact vectors are in the fixture manifest.

All shared operations use the derived named mutex. An abandoned mutex is
treated as acquired so the caller can validate shared state under mutual
exclusion. No-wait, bounded, infinite, cancellation, access-denied, and disposed
outcomes are converted to public statuses. Closing one participant closes only
its mapping and mutex handles; Windows kernel object lifetime removes named
objects after the final handle closes.

## Linux paths

The root is `/dev/shm/SharedMemoryStore` when `/dev/shm` exists. Otherwise it is
`SharedMemoryStore` below the operating system temporary directory. The root
must be a real directory rather than a symbolic link or reparse point and is
forced to mode `0700`.

The resource fragment is:

```text
sms-<readable>-<digest>
```

To form `readable`, process UTF-16 code units in order. Keep only ASCII letters,
ASCII digits, `-`, `_`, and `.`; replace every other unit with `_`. Trim leading
and trailing `_` and `.`. Use `store` if nothing remains, then truncate to the
first 80 code units. `digest` is the lowercase hexadecimal encoding of the
first 8 bytes of SHA-256 over the UTF-8 public name. The digest is taken from the
unsanitized, untruncated public name and prevents sanitized-name collisions.

Four paths use this fragment:

| Suffix | Purpose |
|---|---|
| `.region` | Mapped data file |
| `.lock` | Ordinary store-operation synchronization |
| `.owners` | Live region-owner sidecar |
| `.lifecycle` | Serializes owner registration, stale cleanup, create/open, and final close |

Every file is opened or created read/write with mode `0600`, and existing modes
are forced back to `0600` when touched.

## Linux locking

Both `.lock` and `.lifecycle` use a nonblocking record lock on byte range
`[0, 1)`, retried according to the caller's wait policy. Current C# and native
implementations issue Linux `F_OFD_SETLK` open-file-description locks and fail
closed as `UnsupportedPlatform` when that command is unavailable. OFD locks
conflict with other descriptors in the same PID, including independently loaded
managed assemblies and native modules, and with traditional `F_SETLK` locks.
They therefore remain mutually exclusive across processes with released v1
clients while avoiding the traditional rule that closing any sibling descriptor
releases all locks owned by that process. Using `flock` for either interoperable
resource is not compatible.

One wrapper may still be called by several local threads on the same descriptor,
so it uses a non-reentrant local gate before entering the kernel. Release unlocks
the byte range before releasing that gate. If explicit unlock fails, the wrapper
closes/retires its descriptor before reopening the local gate because close is
the OFD-lock release boundary. Concurrent use of a released implementation that
still uses process-associated `F_SETLK` and a current OFD implementation inside
one OS process is unsupported: closing any descriptor can invalidate the old
implementation's process-associated lock. Cross-process compatibility and
same-process coexistence among current OFD implementations remain supported.

This prohibition applies to the interoperable `.lock` and `.lifecycle`
resources. A current C# participant may also hold `flock` on its own private
per-owner liveness anchor as described below. That anchor is not a replacement
for either protocol record lock and no foreign participant is required to lock
it.

Retries observe cancellation and bounded time using a monotonic clock. The
managed baseline retries at intervals no longer than 10 milliseconds or the
remaining timeout. A foreign implementation may use a shorter interval but
must preserve no-wait and bounded-wait outcomes.

## Linux owner sidecar and cleanup

One open store handle registers one owner line:

```text
<decimal-pid>:<process-start-token>:<unique-token>
```

The normal start token is `proc-` plus field 22 (`starttime`) from
`/proc/<pid>/stat`. If procfs cannot be read, the managed fallback is `utc-`
plus the process start time in .NET UTC ticks. The unique token is a lowercase
32-hex-digit GUID without separators. Owner readers also tolerate legacy
PID-only lines conservatively, but writers emit all three fields.

Current C# packages add one private liveness artifact for each managed owner:

```text
<resource-fragment>.owners.anchor.<32-hex-unique-token>
```

The artifact is mode `0600`; its suffix is the unchanged third field of the
owner line. The managed process holds an exclusive open-description `flock` for
the lifetime of its mapped view. This is an additive managed safety extension,
not a new owner-line field or a requirement on resource-protocol-1 C++, Python,
or older C# participants. Those participants neither create nor interpret the
artifact.

The canonical anchor name is the exact `.owners` path plus `.anchor.` and the
owner token rendered as 32 lowercase hexadecimal digits. Anchor reconciliation
is scoped to that one store: names with any other prefix, suffix length, case,
or token syntax are malformed for this purpose and are never selected for
automatic deletion.

While holding `.lifecycle`, a current C# reader classifies a valid referenced
anchor before consulting PID state: a contended lock is authoritative evidence
that the owner is live, including when its PID is hidden by another PID
namespace; an acquirable lock is stale. A missing anchor preserves the normal
PID/start-token rule for implementations that do not create anchors. Access
failure, a symbolic link, a directory, or any other ambiguous probe is retained
conservatively. A same-process registry and a separately opened probe descriptor
make local probing explicit rather than relying on process-scoped lock behavior.

Owner updates occur while holding `.lifecycle`. A reader trims each line, splits
it into at most three colon-separated parts, and requires the first part to be a
positive decimal PID. It compares a start token only when all three parts are
present; legacy one- or two-part PID records therefore receive PID-only liveness
checking. The unique token is an ownership key, not a liveness value. Readers
discard blank, invalid-PID, and confirmed-dead records but conservatively retain
an owner when liveness cannot be determined. Updates write the complete set to
`.owners.tmp`, force mode `0600`, then atomically replace `.owners`; best-effort
cleanup removes a leftover temporary file.

An owner-sidecar rewrite that excludes an unlocked owner commits before its
anchor is deleted. On orderly managed close, the mapped view is released first;
the anchor is unlocked only after the exact owner line is committed absent or a
finalized exact-owner release marker has been atomically published. Process
termination closes the anchor descriptor and releases `flock` automatically, so
the next lifecycle operation can classify the stale line and remove a canonical
artifact only when its independent probe proves deletion safe.

While holding `.lifecycle`, a current C# lifecycle operation performs an
advisory orphan-anchor sweep only after the replacement `.owners` sidecar has
committed. It derives the referenced-token set from canonical three-field lines
in that committed sidecar and considers only canonical anchor names for this
store. Each unreferenced candidate is opened through a separate descriptor with
`O_NOFOLLOW` and verified to be a regular file before a nonblocking exclusive
`flock` is attempted. The candidate is deleted only while that separate lock is
held. Referenced or locked anchors, ambiguous probes, non-regular files,
symbolic links, directories, malformed names, and artifacts that cannot be
enumerated, opened, inspected, locked, or deleted because of an access error
are retained conservatively. The sweep is cold lifecycle repair for a crash
between anchor creation and owner-line publication; it is never a key-value
operation path.

A finalized release-marker fallback records permission to release the local
anchor; it does not assert that the owner line or anchor pathname has already
been removed. Either artifact can remain until a later lifecycle operation
reconciles the marker, commits the filtered sidecar, and repeats the conservative
anchor sweep.

When no live owner remains, current stale cleanup removes `.region`, `.owners`,
`.owners.tmp`, and applicable release-marker artifacts. It does not blindly
remove every per-owner anchor. Exact anchors are deleted by orderly owner
release or by the post-commit sweep only when they are canonical, unreferenced,
regular files whose lock is acquirable; every uncertain artifact remains for a
later reconciliation or operator diagnosis. The empty mode-`0600` `.lock` and
`.lifecycle` files are deliberately retained as stable rendezvous inodes; they
contain no store-generation state. Ordinary synchronization is disposed before
mapped-region cleanup can enter `.lifecycle`, so even an older participant that
deletes a no-owner `.lock` cannot strand a current closing descriptor on an
obsolete inode. Closing a non-final handle commits removal of only its exact
owner record and then attempts the same safe anchor cleanup. Closing the final
live handle performs stale data-resource deletion while holding the lifecycle
lock and remains subject to the same conservative anchor rules.

The sidecar start token protects resource cleanup from PID reuse. Layout-1.2
lease and reservation records themselves contain only a PID; their explicit
recovery policy remains the separate, conservative layout-1.2 contract.
