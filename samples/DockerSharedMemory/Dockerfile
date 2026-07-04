FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src
COPY . .
RUN dotnet build samples/DockerSharedMemory/DockerSharedMemory.csproj -c Release

ENTRYPOINT ["dotnet", "run", "--project", "samples/DockerSharedMemory/DockerSharedMemory.csproj", "-c", "Release", "--no-build", "--"]
