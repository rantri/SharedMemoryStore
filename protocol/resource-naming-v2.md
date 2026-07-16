# Shared Resource Protocol 2

Resource protocol 2 changes synchronization participation without changing
the physical identity derived from a public store name. This deliberate reuse
makes layout 1.2 and layout 2.0 discover one another and fail closed rather
than creating two unrelated stores under one name.

## Physical resources

The mapping and lifecycle names/paths are exactly those specified by
[`resource-naming-v1.md`](resource-naming-v1.md):

- the same Windows named mapping and named synchronization object;
- the same Linux `.region`, `.owners`, `.lifecycle`, and operation-lock paths.

An opener maps enough of an existing region at its actual capacity to inspect
the magic and header before projecting caller-requested dimensions. `CreateNew`
reports AlreadyExists for either layout. A different requested layout reports
IncompatibleLayout before any directory, slot, descriptor, or payload access.

## Cold synchronization

Layout-v2 create, zero-header initialization, complete header validation, and
participant registration occur while the existing named synchronization
resource is held. This preserves serialization with already released v1
clients. Participant retirement is an aligned-atomic layout-v2 participant
protocol transition and does not enter that named resource. The mapping is not
Ready during creation, and no handle is returned until all sections have been
initialized and the participant record is Active.

Physical creation ownership, rather than `OpenMode` or an observed zero magic,
authorizes initialization. A cold-open attempt records whether its physical
mapping call created a new region or opened an existing one. Only the former may
clear and initialize the header. An existing zero header is never written by an
opener: `CreateOrOpen` reports `StoreBusy`, because an older creator may still be
between mapping and synchronization acquisition under resource protocol 1,
while `OpenExisting` reports `IncompatibleLayout`. `CreateNew` reports
`AlreadyExists` without mapping the existing payload.

On Windows, the named mutex is acquired before the physical mapping is created
or opened and remains held through header work and participant registration. On
Linux, `.lifecycle` is acquired first; release-marker reconciliation and stale
data-resource deletion complete before the persistent `.lock` inode is opened
and acquired. The mapping,
private owner-anchor lock, owner-line commit, header work, and participant
registration then occur while both gates are held. Release order is `.lock`
followed by `.lifecycle`. Failed-open cleanup first releases both gates, then
disposes the ordinary synchronization descriptor, and only then disposes the
mapping/owner registration that may re-enter lifecycle coordination. Current
cleanup retains `.lock` as a stable empty rendezvous file. Together these rules
prevent active and reopening participants from splitting across an unlinked and
replacement inode.

The caller's original wait and cancellation budget covers the complete cold
transaction, including both gates, mapping, header work, and participant
registration. A deadline or cancellation observed before mapping or owner
publication prevents those side effects.

Linux owner registration and cleanup remain protected by `.lifecycle`. Every
open layout-v2 handle writes one live v1-compatible owner line:

```text
decimal-pid:proc-start-or-utc-token:32-hex-guid
```

This prevents an older opener from deleting a live SMS2 region as stale. Close
retires the ordinary descriptor before removing only the exact handle's line;
final-owner cleanup follows the existing resource protocol and retains the
stable `.lock`/`.lifecycle` rendezvous files.

Each current managed Linux owner also creates the private mode-`0600` path

```text
<resource-fragment>.owners.anchor.<32-hex-owner-guid>
```

before publishing its owner line, then holds an exclusive open-description
`flock` until its mapped view is gone and its owner release is safely recorded.
This lock is deliberately private to the owner and distinct from the POSIX
record-lock protocol on `.lock` and `.lifecycle`; it never appears on a hot data
path. C++/Python and older managed owners remain compatible because the
three-field sidecar format is unchanged and they do not need to create anchors.
The canonical name is the exact store `.owners` path plus `.anchor.` and the
owner GUID rendered as exactly 32 lowercase hexadecimal digits; anchor cleanup
never widens that per-store name pattern.

Under `.lifecycle`, a current managed reader probes a referenced anchor through
a separately opened descriptor. Lock contention means live even if the recorded
PID is invisible in the reader's PID namespace. Successful lock acquisition
means stale. A missing anchor falls back to the v1 PID/start-token check for
older, C++, and Python owners. Access errors, symbolic links, directories, and
other ambiguous results retain the owner conservatively. A same-process anchor
registry makes local-owner classification explicit.

Close and failed-open cleanup never wait indefinitely for `.lifecycle`. After
the mapped view is released, a C# resource-protocol-2 participant waits at most
250 milliseconds to remove its exact owner line. If it cannot acquire the lock,
or if cleanup fails before the owner-sidecar replacement commits, it publishes:

```text
<resource-fragment>.owners.released.<32-hex-owner-guid>.ready
```

The file contains the exact v1-compatible owner line. It is created as a unique
`0600` temporary file beside `.owners`, flushed, and atomically renamed to its
final name. The owner GUID in the filename must equal the third field in the
content. Temporary artifacts have the same prefix but no `.ready` suffix.

While holding `.lifecycle`, a resource-protocol-2 opener or releaser reconciles
finalized markers before process-liveness filtering. It reads the raw owner
sidecar, applies the existing line-trimming rule, removes only each marker's
ordinal-exact owner record, atomically
rewrites `.owners`, and only then deletes the corresponding marker. Replaying a
marker after a crash between rewrite and deletion is therefore idempotent. A
marker that arrives after the scan is conservative: its still-present owner line
continues to protect the region until a later lifecycle operation. A malformed
finalized marker fails the cold operation closed and is retained for diagnosis.

When the committed live-owner set is empty, stale-resource deletion also removes
the exact resource's finalized and temporary marker glob. The empty owner set is
atomically committed before this deletion. Resource-protocol-1 C#, C++, and
Python participants do not interpret release markers; they continue to see the
unreconciled owner line and therefore remain conservatively fail closed until a
protocol-2 participant reconciles it or normal PID/start-token liveness proves it
stale.

The C# 2.0 package uses this bounded cleanup extension for both mapped profiles
because layout 1.2 and layout 2.0 share the same Linux ownership resources. This
does not change layout-1.2 bytes or its required per-operation `.lock` behavior;
older resource-protocol-1 implementations remain compatible and conservative as
described above.

The owner-sidecar rewrite is the commit point before deleting an unlocked stale
anchor. Orderly close unmaps first, then releases the anchor only after either
that exact owner is absent from the committed sidecar or its finalized release
marker has been atomically published. If both recording paths fail, the managed
process deliberately retains the anchor. Process termination closes the file
descriptor and releases `flock` automatically; later lifecycle cleanup removes
the now-unlocked artifact only when the conservative probe rules below prove it
safe.

Every current C# orphan-anchor sweep runs under `.lifecycle` and only after the
replacement `.owners` sidecar commits. The sweep builds its referenced-token set
from canonical three-field records in that committed sidecar, enumerates only
canonical anchor names belonging to the same store, and ignores malformed
names. It considers only unreferenced candidates. Each candidate is opened on a
separate descriptor with `O_NOFOLLOW`, verified through that descriptor to be a
regular file, and deleted only after and while a nonblocking exclusive `flock`
succeeds. Referenced or locked anchors, ambiguous probes, non-regular files,
symbolic links, directories, malformed names, and enumeration/open/stat/lock/
delete access errors are retained conservatively. No final-owner glob removes
these uncertain artifacts.

Publishing a finalized release marker permits the closing participant to
release its local anchor after unmapping, but the fallback may leave both the
compatible owner line and the anchor pathname in place. A later lifecycle
operation reconciles the exact line, commits the sidecar, and then applies the
same conservative orphan sweep. This repair remains entirely on the cold
lifecycle path.

## Hot data paths

After open, layout-v2 publish, reservation, acquire, projection, release,
remove, reclaim, recovery, and diagnostics do not enter any named semaphore,
mutex, or file lock. They use only aligned mapped atomics, immutable bytes,
bounded scans, and helpable descriptors. A retained cold synchronization
object is unreachable from these paths and exists only for compatible close or
recovery lifecycle work.

Layout-v1.2 handles continue using resource protocol 1 and acquire the ordinary
named synchronization object per data operation. Resource protocol 2 does not
change their bytes or behavior.
