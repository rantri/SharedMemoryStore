FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        cmake \
        g++ \
        ninja-build \
        python3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace
COPY . .

RUN cmake -S . -B artifacts/docker-interop-native -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DSMS_BUILD_TESTS=ON \
        -DSMS_BUILD_SAMPLES=OFF \
        -DSMS_INSTALL=ON \
        -DSMS_PYTHON_INSTALL_DIR=shared_memory_store \
    && cmake --build artifacts/docker-interop-native --parallel \
    && ctest --test-dir artifacts/docker-interop-native --output-on-failure \
    && cmake --install artifacts/docker-interop-native \
        --component Python \
        --prefix /workspace/src/python \
    && dotnet build tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj \
        --configuration Release

ENV SMS_CPP_AGENT=/workspace/artifacts/docker-interop-native/tests/cpp/sms_cpp_interop_agent \
    SMS_PYTHON_EXECUTABLE=python3 \
    SMS_RUN_INTEROP_STRESS=0 \
    SMS_INTEROP_STRESS_VALUES=1000 \
    SMS_INTEROP_STRESS_LIFECYCLE_CYCLES=10000

CMD ["bash", "-lc", "dotnet test tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj --configuration Release --no-build --filter 'FullyQualifiedName!~StressInteropTests' && if [ \"$SMS_RUN_INTEROP_STRESS\" = 1 ]; then dotnet test tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~StressInteropTests'; fi"]
