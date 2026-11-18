#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development Platform__Mode=Local
export ASPNETCORE_URLS=http://127.0.0.1:5080
export Platform__LocalRoot="$PWD/.local/data" Platform__ConverterPath="$PWD/build/native/step-to-glb"
export Platform__WorkerPath="$PWD/src/backend/WorkerHost/bin/Debug/net10.0/WorkerHost.dll"
if [[ -x "$PWD/.local/tools/node_modules/.bin/node" ]]; then export PATH="$PWD/.local/tools/node_modules/.bin:$PATH"; fi
if [[ ! -x build/native/step-to-glb ]]; then bash scripts/build-native.sh; fi
dotnet build StepGlb.sln -m:1
npm ci --prefix src/frontend
dotnet src/backend/JobApi/bin/Debug/net10.0/JobApi.dll &
api_pid=$!
trap 'kill "$api_pid" 2>/dev/null || true' EXIT INT TERM
npm run --prefix src/frontend dev
