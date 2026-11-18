#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
cmake -S src/hpc/StepToGlb -B build/native -G Ninja -DCMAKE_BUILD_TYPE=Release "$@"
cmake --build build/native --parallel 4
ctest --test-dir build/native --output-on-failure
