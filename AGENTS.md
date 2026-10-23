# auto-lod

Unity 6000.6 project (URP). Contains `Assets/ComplexLODs` — a feature-preserving
LOD generator with its own simplification stack (no third-party code).

## Commit rules

- Never reference Devin in commits: no "Generated with Devin" trailer,
  no `Co-Authored-By: Devin`, no Devin links in messages or descriptions.
- Commit messages describe the technical change only.

## Layout

- `Assets/ComplexLODs/Editor/Core` — own simplification stack:
  - `Quadric.cs` — Garland-Heckbert symmetric quadric.
  - `MeshGraph.cs` — position-welded vertex graph: corners, wedges (per-corner attribute sets), edge map, boundary detection.
  - `QEMSimplifier.cs` — edge-collapse simplifier driven by **probabilistic quadrics** (Trettner-Kobbelt 2020, expected QEM under isotropic Gaussian vertex noise). **Constrained placement**: collapse targets are the edge endpoints, plus the midpoint only on edges still coinciding with a real source edge (`Edge.phantom` tracks this — inherited/moved edges are phantom). **Feature chains**: boundary, sharp-crease, UV/normal-seam, submesh-change and non-manifold edges are `Edge.feature`; a vertex on such a curve may only collapse *along* the chain — straight-run vertices can be consumed, corners and junctions stay put, and non-manifold endpoints are locked outright. Moved vertices interpolate normals/tangents/UVs/color along the source edge's wedge chart; a move without an unambiguous attribute partner (or with differing skin weights) is rejected. Integrity = link condition + duplicate-face rejection + boundary-loop topology guard + flip/sliver(shape-factor)/growth rejection + bounded collapse distance. **Exterior shell**: vertices on the 2D convex hull of any of 14 viewing projections are silhouette verts — locked for the whole run so the outer bounds can never move. **Interior-release ladder**: when reduction stalls above the target, interior (non-silhouette) detail is released in stages gated by target depth — ≥75% stays fully preserved, ≥50% allows interior collapse along feature chains past corners, ≥25% frees interior verts entirely, <25% additionally lets interior boundary loops dissolve. Silhouette locks, the link condition, flips/slivers/growth, attribute charts and the collapse-distance bound are never relaxed; unreachable targets are still reported honestly (`releaseStage` records how far the ladder went). Boundary-component counts are topological loops, not semantic doors/windows. Preservation = constraint planes on boundary AND non-manifold edges (openings/T-junctions), crease edges, UV/normal seams + vertex locks (boundaries, top-N, bbox extremes) + importance weights (radial extremity, vertex-color mask).
  - Meshes with <= 256 triangles skip decimation entirely (`MinSimplifyTris` in `ComplexLODGenerator`) — tiny children like door leaves only lose rim coverage; every LOD reuses the source mesh. When a level's output is byte-identical to the previous level's mesh (preservation floor hit early — common on feature-dense kits), both levels share one mesh asset and one renderer object instead of writing duplicates; a fresh clone is still made when the level needs its own material variant. Regeneration destroys prior generated clones (detected by mesh living under `outputFolder`, or `<source>_LOD<n>` name + same mesh instance) while authored LOD content is only disabled, never deleted.
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

## Prefab-level validation

`Tools/Validation` holds the whole-prefab audit drivers
(`MegaCityAudit.cs`, `MegaCityRegressions.cs`, `RegenRepro.cs` — copied
temporarily into `Assets/ComplexLODs/Editor` to run inside Unity) plus the
offline coverage/boundary measurement and report scripts. See
`Tools/Validation/README.md` before rerunning the MegaCity matrix.

## Compile verification

The generated csproj references an uninstalled Unity version. To compile-check
ComplexLODs sources against an installed editor:

```
dotnet build %TEMP%\complexlods-check\compile-check.csproj
```

(project file at `C:\Users\nichi\AppData\Local\Temp\complexlods-check\compile-check.csproj`
globs `Assets/ComplexLODs/Editor/**/*.cs` and references
`C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Data\Managed\**\*.dll`).
