# Security Policy

SharedMemoryStore `1.0.x` receives best-effort community security fixes. This
policy does not promise response or remediation service levels.

## Supported Versions

| Version | Support |
|---------|---------|
| `1.0.x` | Supported with best-effort community security fixes |
| `< 1.0` | Unsupported |

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

- affected package version.
- OS and .NET runtime.
- store options and operation sequence.
- observed status or failure mode.
- minimal reproduction that does not expose secrets.
- impact assessment and any known mitigations.

Maintainers will review, request more information if needed, coordinate a fix
when appropriate, and decide when public disclosure is safe.
