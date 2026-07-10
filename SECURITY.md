# Security Policy

Managed SharedMemoryStore `1.0.x` receives best-effort community security
fixes. The native CMake and Python `0.1.x` distributions are alpha and receive
best-effort prerelease security fixes. This policy does not promise response or
remediation service levels.

## Supported Versions

| Version | Support |
|---------|---------|
| Managed NuGet `1.0.x` | Supported with best-effort community security fixes |
| Native CMake `0.1.x` | Alpha; best-effort prerelease fixes |
| Python `0.1.x` | Alpha; best-effort prerelease fixes |
| Older lines | Unsupported |

Older unpublished or local builds are not independently supported. Users should
reproduce reports against the latest package version available to them.

## Reporting a Vulnerability

Use GitHub private vulnerability reporting or a repository security advisory
for this project when that channel is enabled. Do not include exploit details,
private payloads, credentials, or reproduction data in public issues or pull
requests.

Enabling private vulnerability reporting, or publishing an owner-approved
private contact path, is a mandatory release gate. If neither is available,
open only a minimal public issue requesting a secure contact channel and do not
include vulnerability details.

Include:

- affected distribution, package version, C ABI version when applicable, and
  layout/resource-naming versions.
- OS, architecture, and .NET runtime, C++ compiler/runtime, or Python version.
- store options and operation sequence.
- observed status or failure mode.
- minimal reproduction that does not expose secrets.
- impact assessment and any known mitigations.

Maintainers will review, request more information if needed, coordinate a fix
when appropriate, and decide when public disclosure is safe.

## Trusted Participant Boundary

All distributions assume cooperating same-host processes that are authorized
to access the same mapping, lock, and lifecycle resources. They validate
options, record shapes, lifecycle identity, ownership, and visibility, but do
not protect against a malicious participant that already has legitimate write
access and intentionally corrupts mapped bytes, forges metadata, or bypasses
the public API. Cross-host security and hostile multi-tenant sharing are outside
the supported model.

Reports about native ABI memory safety, unsafe handle or borrowed-view lifetime,
resource permissions, owner cleanup, library substitution, or Python wheel
contents are in scope. The Python loader intentionally accepts only its
package-adjacent native library and validates ABI and protocol metadata; a path
that loads an arbitrary current-directory, `PATH`, or system library should be
reported privately.
