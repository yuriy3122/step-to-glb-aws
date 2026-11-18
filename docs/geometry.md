# Geometry engine

OCCT is pinned to **7.9.3**, upstream commit `a016080bf6738d6aeae020badee4e888ad1540a5`. CMake requires the exact installed version. The worker image verifies the source archive SHA-256 `c533f2667b59921bd6bd40ce82e7b9900b0289ccc731af5fdeeba097de80ef0f` before compiling it. APIs were checked against installed 7.9.3 headers and [upstream sources](https://github.com/Open-Cascade-SAS/OCCT/tree/V7_9_3).

`STEPCAFControl_Reader` imports into an XCAF document with name, colour and layer modes enabled. The document uses millimetres, with STEP unit conversion handled during transfer. `BRepCheck_Analyzer` rejects invalid imported topology. Each free shape is tessellated with `BRepMesh_IncrementalMesh`; every surface face must have a nonempty triangulation. Edge-only and empty models are rejected rather than exporting an empty GLB.

`RWGltf_CafWriter` writes binary glTF with the XCAF hierarchy and locations intact. Its coordinate-system converter maps Z-up millimetres to Y-up metres. The file is written to a temporary path, checked for correct GLB magic/version/length and JSON/BIN chunks, then renamed to the final path. Tests additionally use the Khronos validator and Babylon.js to check mesh presence, materials, names, hierarchy, translated component positions and dimensions.

```sh
build/native/step-to-glb --input part.step --output model.glb \
  --linear-deflection 0.1 --angular-deflection 0.5
```

Linear deflection is in imported millimetres; angular deflection is in radians (0 < angle ≤ π). Smaller values usually improve curved-surface fidelity at a cost in triangles, memory, time and GLB size. Both must be finite and positive. These options are currently CLI-only; the API uses defaults.

Stdout contains newline-delimited JSON stage events: reading, importing, meshing, exporting, validating and converted. Percent is `null` for these kernel operations because no trustworthy measurable subprogress is connected. Conversion completion remains indeterminate until storage and provider confirmation. This intentionally avoids artificial stage-weight percentages. Stdout diagnostics from OCCT's messenger are disabled; errors use stderr and exit codes.

Exit codes: 0 success; 2 invalid arguments; 3 missing/unreadable/invalid STEP; 4 import/geometry failure; 5 meshing failure; 6 output/filesystem/export failure; 7 allocation failure. The worker additionally rejects stale/duplicate executions with exit 9. OS memory termination may occur before a C++ allocation exception; Batch/recovery handles the provider failure.

Preservation applies where OCCT's STEP/glTF mappings support it. PMI, exact B-rep, CAD constraints and all source metadata are not representable in this GLB pipeline. Tests cover generated millimetre and inch-unit solids and transformed assemblies, not every third-party CAD exporter or unit combination.
