# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS target-tools
RUN apt-get update && apt-get install -y --no-install-recommends build-essential cmake ninja-build curl ca-certificates rapidjson-dev libtbb-dev libfreetype6-dev libfontconfig1-dev && rm -rf /var/lib/apt/lists/*

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS native
ARG BUILDARCH
ARG TARGETARCH
RUN set -eux; \
    packages=""; \
    case "$BUILDARCH:$TARGETARCH" in \
      arm64:amd64) packages=g++-x86-64-linux-gnu ;; \
      amd64:arm64) packages=g++-aarch64-linux-gnu ;; \
    esac; \
    apt-get update && apt-get install -y --no-install-recommends build-essential cmake ninja-build curl ca-certificates rapidjson-dev libtbb-dev libfreetype6-dev libfontconfig1-dev $packages && rm -rf /var/lib/apt/lists/*
# OCCT V7_9_3 resolved to this immutable upstream commit; verify archive bytes.
RUN curl -fsSL https://codeload.github.com/Open-Cascade-SAS/OCCT/tar.gz/a016080bf6738d6aeae020badee4e888ad1540a5 -o /tmp/occt.tar.gz \
 && echo 'c533f2667b59921bd6bd40ce82e7b9900b0289ccc731af5fdeeba097de80ef0f  /tmp/occt.tar.gz' | sha256sum -c - \
 && mkdir /tmp/occt && tar -xzf /tmp/occt.tar.gz -C /tmp/occt --strip-components=1
RUN --mount=type=cache,id=hpc-occt-793-${BUILDARCH}-${TARGETARCH},target=/tmp/occt-build,sharing=locked \
    --mount=from=target-tools,source=/usr/lib,target=/target-libs,readonly \
    set -eu; \
    set --; \
    if [ "$BUILDARCH" != "$TARGETARCH" ]; then \
      case "$TARGETARCH" in amd64) cpu=x86_64 ;; arm64) cpu=aarch64 ;; *) exit 1 ;; esac; \
      triple="$cpu-linux-gnu"; \
      target_libs="/usr/lib/$triple"; \
      mkdir -p "$target_libs"; \
      cp -a "/target-libs/$triple/." "$target_libs/"; \
      test -f "$target_libs/libfreetype.so"; \
      test -f "$target_libs/cmake/TBB/TBBConfig.cmake"; \
      set -- -DCMAKE_SYSTEM_NAME=Linux "-DCMAKE_SYSTEM_PROCESSOR=$cpu" \
        "-DCMAKE_C_COMPILER=$triple-gcc" "-DCMAKE_CXX_COMPILER=$triple-g++" \
        "-DCMAKE_LIBRARY_PATH=$target_libs" "-DTBB_DIR=$target_libs/cmake/TBB" \
        "-D3RDPARTY_FREETYPE_LIBRARY=$target_libs/libfreetype.so"; \
    fi; \
    cmake -S /tmp/occt -B /tmp/occt-build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_CPP_STANDARD=C++20 "$@" \
    -DINSTALL_DIR=/opt/occt -DINSTALL_DIR_LAYOUT=Unix \
    -DBUILD_MODULE_FoundationClasses=OFF -DBUILD_MODULE_ModelingData=OFF -DBUILD_MODULE_ModelingAlgorithms=OFF \
    -DBUILD_MODULE_Visualization=OFF -DBUILD_MODULE_ApplicationFramework=OFF -DBUILD_MODULE_DataExchange=OFF \
    -DBUILD_MODULE_Draw=OFF -DBUILD_MODULE_DETools=OFF '-DBUILD_ADDITIONAL_TOOLKITS=TKDESTEP;TKDEGLTF;TKPrim;TKMesh' \
    -DUSE_RAPIDJSON=ON -DUSE_TBB=ON -DUSE_FREETYPE=ON -DUSE_OPENGL=OFF -DUSE_GLES2=OFF -DUSE_XLIB=OFF \
 && cmake --build /tmp/occt-build --parallel 4 && cmake --install /tmp/occt-build

# Compile the small adapter and run CTest in the target runtime environment.
FROM target-tools AS converter
COPY --from=native /opt/occt /opt/occt
COPY src/hpc/StepToGlb /source/native
RUN cmake -S /source/native -B /source/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH=/opt/occt \
 && cmake --build /source/build --parallel 4 \
 && LD_LIBRARY_PATH=/opt/occt/lib ctest --test-dir /source/build --output-on-failure
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS managed
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY src/backend ./src/backend
RUN dotnet restore src/backend/WorkerHost/WorkerHost.csproj --locked-mode --disable-parallel \
 && dotnet publish src/backend/WorkerHost/WorkerHost.csproj -c Release --no-restore -m:1 -o /app /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
RUN apt-get update && apt-get install -y --no-install-recommends libtbb12 libfreetype6 libfontconfig1 ca-certificates && rm -rf /var/lib/apt/lists/*
COPY --from=native /opt/occt /opt/occt
COPY --from=converter /source/build/step-to-glb /usr/local/bin/step-to-glb
COPY --from=managed /app /app
ENV LD_LIBRARY_PATH=/opt/occt/lib Platform__ConverterPath=/usr/local/bin/step-to-glb DOTNET_EnableDiagnostics=0
WORKDIR /app
USER 1654
ENTRYPOINT ["dotnet", "/app/WorkerHost.dll"]
