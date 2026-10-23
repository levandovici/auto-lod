# auto-lod

Unity 6000.6 project (URP). Contains `Assets/ComplexLODs` — a feature-preserving
LOD generator with its own simplification stack (no third-party code).

## Layout

- `Assets/ComplexLODs/Editor/Core` — own simplification stack:
  - `Quadric.cs` — Garland-Heckbert symmetric quadric.
  - `MeshGraph.cs` — position-welded vertex graph: corners, wedges (per-corner attribute sets), edge map, boundary detection.
  - `QEMSimplifier.cs` — edge-collapse simplifier driven by **probabilistic quadrics** (Trettner-Kobbelt 2020, expected QEM under isotropic Gaussian vertex noise — well-posed 3x3 minimum everywhere, no singular-direction spikes). **Subset placement**: collapse targets are restricted to edge endpoints + midpoint, and midpoints are only taken on edges that still coincide with a real source edge (`Edge.phantom` tracks this — inherited/moved edges are phantom). This bounds vertex drift to zero: every output vertex lies on the source surface. Geometric integrity = **SurfaceGuard** (static grid of source verts; a new tri may not cover off-plane recessed detail — windows/door insets/frames — else reject) + **hole-chord guard** (verts of the same hole loop may only collapse along the loop — interior chords would fill openings) + **atomic sheets** (connected components <= 24 tris — glass panes, frames, decals in modular kits — may only die whole: partial kills rejected, whole-component removals queued in the heap by cost; component-size vertex weighting delays them until their surroundings simplify). When every legal collapse is exhausted above target, a **relaxation ladder** drops the geometric guards in order (surface -> hole-chord -> boundary-loops -> link-condition) so deep LODs fuse sheets/fill openings into a solid shell instead of stalling. Preservation = constraint planes on boundary AND non-manifold edges (openings/T-junctions), crease edges, UV/normal seams + vertex locks (boundaries, top-N, bbox extremes) + importance weights (radial extremity, vertex-color mask) + flip/sliver(shape-factor)/growth rejection + link condition + boundary-loop topology guard.
  - Meshes with <= 256 triangles skip decimation entirely (`MinSimplifyTris` in `ComplexLODGenerator`) — tiny children like door leaves only lose rim coverage; every LOD reuses the source mesh.
- `Assets/ComplexLODs/Editor` — `ComplexLODProfile` (settings asset), `ComplexLODGenerator` (multi-LOD pipeline + LODGroup fill + reports), `TextureLODUtility` (texture downscale + material variants), `ComplexLODWindow` (UI: `Window/ComplexLODs`, `CONTEXT/MeshRenderer/ComplexLODs...`, `CONTEXT/LODGroup/Generate Missing LODs/ComplexLODs`).

Every LOD is decimated **from the LOD0 source mesh** — never chained — so errors don't accumulate.

`MeshGraph.Build` strips zero-area source triangles at preflight (kit meshes
often contain degenerate faces that would otherwise propagate into every LOD).

## Algorithm regression testing

A standalone harness (UnityEngine stubs + the real `Core/*.cs` compiled into a
console app + minimal FBX binary reader) lives at
`%TEMP%\lodharness` — rebuild it the same way if deleted. It runs the actual
simplifier on synthetic and real FBX meshes and reports per-level
`maxDrift` (must stay ~0), `worstShape`, `maxEdge` and boundary-loop counts —
much faster than eyeballing regenerated meshes in the editor.

## Compile verification

The generated csproj references an uninstalled Unity version. To compile-check
ComplexLODs sources against an installed editor:

```
dotnet build %TEMP%\complexlods-check\compile-check.csproj
```

(project file at `C:\Users\nichi\AppData\Local\Temp\complexlods-check\compile-check.csproj`
globs `Assets/ComplexLODs/Editor/**/*.cs` and references
`C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Data\Managed\**\*.dll`).
