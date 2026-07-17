FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        cmake \
        g++ \
        ninja-build \
        python3 \
        python3-venv \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace
COPY . .

RUN cmake -S . -B artifacts/docker-interop-native -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DSMS_BUILD_TESTS=ON \
        -DSMS_BUILD_SAMPLES=OFF \
        -DSMS_INSTALL=ON \
    && cmake --build artifacts/docker-interop-native --parallel \
    && ctest --test-dir artifacts/docker-interop-native --output-on-failure \
    && python3 -m venv /opt/sms-python-build \
    && /opt/sms-python-build/bin/python -m pip install --upgrade pip build \
    && /opt/sms-python-build/bin/python -m build --wheel --outdir /tmp/sms-dist . \
    && python3 -m venv /opt/sms-python-runtime \
    && /opt/sms-python-runtime/bin/python -m pip install --no-deps /tmp/sms-dist/*.whl \
    && /opt/sms-python-runtime/bin/python -c \
        "import shared_memory_store; assert shared_memory_store.LAYOUT_MAJOR_VERSION == 2; assert shared_memory_store.REQUIRED_FEATURES == 7" \
    && dotnet build tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj \
        --configuration Release

ENV SMS_CPP_AGENT=/workspace/artifacts/docker-interop-native/tests/cpp/sms_cpp_interop_agent \
    SMS_PYTHON_EXECUTABLE=/opt/sms-python-runtime/bin/python \
    SMS_PYTHONPATH=/opt/sms-python-runtime/lib/python3.12/site-packages \
    SMS_PYTHON_CHECKPOINT_LIBRARY=/workspace/artifacts/docker-interop-native/src/cpp/libshared_memory_store_python_checkpoint.so \
    SMS_RUN_INTEROP_STRESS=0 \
    SMS_INTEROP_STRESS_VALUES=1000 \
    SMS_INTEROP_STRESS_LIFECYCLE_CYCLES=10000

CMD ["bash", "-lc", "dotnet test tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName!~StressInteropTests' && if [ \"$SMS_RUN_INTEROP_STRESS\" = 1 ]; then dotnet test tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~StressInteropTests'; fi"]
