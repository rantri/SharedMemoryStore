# Quickstart: Validate Native and Python Implementations

## Prerequisites

- .NET SDK 10.
- Python 3.10 or newer.
- CMake 3.20 or newer and a C++20 compiler for the host platform.
- Docker Desktop or Docker Engine for the Linux container validation path.

## Native Build and Tests

```powershell
cmake -S . -B artifacts/native-build -DSMS_BUILD_TESTS=ON -DSMS_BUILD_SAMPLES=ON
cmake --build artifacts/native-build --config Release
ctest --test-dir artifacts/native-build -C Release --output-on-failure
```

Expected: ABI/layout, hashing, store lifecycle, lease, reservation, recovery,
platform-resource, and C++ RAII tests pass.

## Python Source and Wheel Tests

```powershell
python -m pip install --upgrade build
python -m build --wheel
python -m venv artifacts/python-consumer
artifacts/python-consumer/Scripts/python -m pip install (Get-ChildItem dist/*.whl | Select-Object -First 1)
artifacts/python-consumer/Scripts/python -m unittest discover -s tests/python -v
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
```

On Linux, use `bin/python` rather than `Scripts/python`.

Expected: the installed package locates its bundled native library, passes its
public API/lifetime tests, and runs the basic sample without importing repository
sources accidentally.

## Cross-Runtime Validation

```powershell
dotnet build SharedMemoryStore.slnx -c Release
pwsh ./scripts/validate-interoperability.ps1 -Configuration Release
```

Expected: the ordered C#/C++/Python producer-consumer matrix passes on the host.
Use the script's Docker option to run the equivalent Linux matrix from Windows.

## Existing Regression Gates

```powershell
pwsh ./scripts/validate-docs.ps1
dotnet test SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
pwsh ./scripts/validate-package-consumption.ps1
```

Expected: all existing managed behavior and package checks remain passing.
