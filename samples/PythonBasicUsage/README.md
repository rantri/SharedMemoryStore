# Python Basic Usage Sample

## Purpose and Audience

This sample is the smallest Python consumer of the installed
`shared-memory-store` wheel. It demonstrates Python context managers and
borrowed `memoryview` lifetimes over the wheel's adjacent C ABI 2 native
library.

## Concepts Demonstrated

- participant-aware `StoreOptions.create`;
- immutable SMS2 layout, resource protocol, feature, and participant identity;
- `MemoryStore.open` and context-managed close;
- opaque payload and descriptor bytes;
- a context-managed lease with borrowed read-only views;
- remove and diagnostics; and
- loading the packaged native library rather than repository sources.

## Prerequisites

- Python 3.10 or newer on a qualified x86-64 Windows or Linux host.
- An installed `shared-memory-store` `1.0.0` platform wheel.
- The wheel's adjacent native library with C ABI `2.0`.

## Run

From the repository root, using the clean wheel environment:

```powershell
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
```

Use `artifacts/python-consumer/bin/python` on Linux.

## Expected Output

```text
protocol=SMS2 layout=2.0 resource=2 required=0x7 optional=0x0 participants=4
value=b'hello from Python\x00' descriptor=b'sample'
free slots: 2/2
```

## Expected Non-Success Statuses

- `UNSUPPORTED_PLATFORM`: mapped atomics or required platform facilities are
  unavailable.
- `INCOMPATIBLE_LAYOUT`: an existing mapping is not canonical SMS2.
- `PARTICIPANT_TABLE_FULL`: every participant record is occupied.
- `STORE_BUSY`: a bounded cold-open or local progress budget expired.

Unexpected results raise an exception and produce a nonzero process exit.

## Cleanup

The sample uses a unique name, closes its lease, removes the value, and closes
the store through context managers.

## Related Documentation

- [Samples](../../docs/samples.md)
- [Getting started](../../docs/getting-started.md)
- [Usage](../../docs/usage.md)
- [Packaging](../../docs/packaging.md)
- [Portability](../../docs/portability.md)

## Scope Boundaries and Non-Goals

Run this against an installed wheel. Adding `src/python` to `PYTHONPATH` does
not supply the adjacent packaged native library and is not a package-consumer
test. This sample does not implement the mapped protocol in Python.
