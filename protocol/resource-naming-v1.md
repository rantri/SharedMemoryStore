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

Both `.lock` and `.lifecycle` use a nonblocking POSIX record lock on byte range
`[0, 1)`, retried according to the caller's wait policy. This is the behavior
exposed by .NET `FileStream.Lock(0, 1)`; using `flock` is not compatible. Each
process must additionally serialize contenders through a process-local mutex
keyed by the absolute lock-file path because POSIX record locks alone do not
provide the required same-process ownership boundary. Release unlocks the byte
range before releasing the local mutex.

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

Owner updates occur while holding `.lifecycle`. A reader trims each line, splits
it into at most three colon-separated parts, and requires the first part to be a
positive decimal PID. It compares a start token only when all three parts are
present; legacy one- or two-part PID records therefore receive PID-only liveness
checking. The unique token is an ownership key, not a liveness value. Readers
discard blank, invalid-PID, and confirmed-dead records but conservatively retain
an owner when liveness cannot be determined. Updates write the complete set to
`.owners.tmp`, force mode `0600`, then atomically replace `.owners`; best-effort
cleanup removes a leftover temporary file.

When no live owner remains, stale cleanup removes `.region`, `.lock`, `.owners`,
and `.owners.tmp`. The `.lifecycle` file is deliberately retained because it is
the rendezvous used while cleanup is in progress and by later openers. Closing a
non-final handle removes only its owner record. Closing the final live handle
performs the same stale-resource deletion while holding the lifecycle lock.

The sidecar start token protects resource cleanup from PID reuse. Layout-1.2
lease and reservation records themselves contain only a PID; their explicit
recovery policy remains the separate, conservative layout-1.2 contract.
