# Support

Managed SharedMemoryStore `1.0.x` is a stable community package. The native
CMake and Python `0.1.x` distributions are alpha. Support is best effort and
does not include response-time service levels, production incident response, or
paid support commitments.

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

- distribution and package version.
- OS and architecture.
- .NET SDK/runtime, CMake and C++ compiler/runtime, or Python version.
- C ABI, layout, and resource-naming versions when applicable.
- store options.
- operation being attempted.
- observed `StoreOpenStatus` or `StoreStatus`.
- expected status.
- minimal reproduction.
- diagnostic snapshot fields and logs without secrets.

## Unsupported Scenarios

The current distributions do not claim support for:

- macOS runtime support.
- 32-bit processes, big-endian hosts, or architectures without recorded
  conformance evidence.
- Windows containers, default-isolated Docker containers, or cross-host
  container sharing.
- network-distributed storage.
- persistence beyond memory-mapped region lifetime.
- application-specific frame parsing by the core store.
- response-time service levels.
- protection from a malicious same-host writer that already has legitimate
  access to the shared resources.
- availability from PyPI or a native package registry until those publication
  channels are explicitly announced.

See [Portability](docs/portability.md), [Lifecycle](docs/lifecycle.md), and
[Packaging](docs/packaging.md) for current package scope.

For cross-runtime reports, include both participant distributions, which
process created the store, the ordered producer/consumer direction, exact
binary inputs, and whether every process used the same public name, capacities,
OS identity, IPC namespace, and native library. A skipped interoperability test
or absent agent artifact is not evidence of a supported runtime pair.
