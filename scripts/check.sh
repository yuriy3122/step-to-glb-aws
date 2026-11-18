#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -x "$PWD/.local/tools/node_modules/.bin/node" ]]; then export PATH="$PWD/.local/tools/node_modules/.bin:$PATH"; fi
bash scripts/build-native.sh "$@"
dotnet restore StepGlb.sln --locked-mode --disable-parallel
dotnet build StepGlb.sln --no-restore -m:1
dotnet test tests/Backend.Tests/Backend.Tests.csproj --no-build -m:1
npm ci --prefix src/frontend
npm run --prefix src/frontend test
npm run --prefix src/frontend test:glb
npm run --prefix src/frontend build
if command -v cfn-lint >/dev/null 2>&1; then cfn-lint infrastructure/cloudformation/*.yaml; elif [[ -x .local/python/bin/cfn-lint ]]; then .local/python/bin/cfn-lint infrastructure/cloudformation/*.yaml; else echo 'cfn-lint is unavailable; infrastructure lint NOT run' >&2; exit 1; fi
