# Support

SharedMemoryStore is a prerelease package. Support is best effort and does not
include response-time service levels, production incident response, or paid
support commitments.

## Where to Ask

- General questions: open a public discussion or issue if the question can be
  shared publicly.
- Bugs: use `.github/ISSUE_TEMPLATE/bug_report.yml`.
- Documentation problems: use `.github/ISSUE_TEMPLATE/documentation.yml`.
- Feature requests: use `.github/ISSUE_TEMPLATE/feature_request.yml`.
- Security vulnerabilities: follow [SECURITY.md](SECURITY.md) and avoid public
  exploit details.

## Bug Report Evidence

Useful reports include:

- package version.
- OS and architecture.
- .NET SDK and runtime version.
- store options.
- operation being attempted.
- observed `StoreOpenStatus` or `StoreStatus`.
- expected status.
- minimal reproduction.
- diagnostic snapshot fields and logs without secrets.

## Unsupported Scenarios

The current prerelease does not claim support for:

- C++ or Python bindings.
- macOS runtime support.
- Windows containers, default-isolated Docker containers, or cross-host
  container sharing.
- network-distributed storage.
- persistence beyond memory-mapped region lifetime.
- application-specific frame parsing by the core store.
- response-time service levels.

See [Portability](docs/portability.md), [Lifecycle](docs/lifecycle.md), and
[Packaging](docs/packaging.md) for current package scope.
