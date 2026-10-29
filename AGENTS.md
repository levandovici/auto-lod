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
  - `QEMSimplifier.cs` — edge-collapse simplifier driven by **probabilistic quadrics** (Trettner-Kobbelt 2020, expected QEM under isotropic Gaussian vertex noise). **Constrained placement**: collapse targets are the edge endpoints, plus the midpoint only on edges still coinciding with a real source edge (`Edge.phantom` tracks this — inherited/moved edges are phantom). **Feature chains**: boundary, sharp-crease, UV/normal-seam, submesh-change and non-manifold edges are `Edge.feature`; a vertex on such a curve may only collapse *along* the chain — straight-run vertices can be consumed, corners and junctions stay put, and non-manifold endpoints are locked outright. Moved vertices interpolate normals/tangents/UVs/color along the source edge's wedge chart; a move without an unambiguous attribute partner (or with differing skin weights) is rejected. Integrity = link condition + duplicate-face rejection + boundary-loop topology guard + flip/sliver(shape-factor)/growth rejection + bounded collapse distance. **Exterior shell**: vertices on the 2D convex hull of any of 14 viewing projections are silhouette verts — locked for the whole run so the outer bounds can never move. **Visibility-based interior removal** (`opt.interiorRemoval`, default on): `Visibility.ExteriorVertices` classifies every welded vertex as exterior (visible from outside) or interior; `opt.stripInterior` then deletes fully-interior triangles up front — the cut rims become ordinary boundary edges, so the feature/boundary machinery protects them like any other opening. Interior verts collapse under fully relaxed rules at every stage and interior-interior edges cost `interiorCostBias`×, so hidden detail is consumed first; **exterior verts keep strict stage-0 feature rules for the whole run** — the release ladder does not apply to them, and interior-only boundary loops may dissolve at any stage while loops touching an exterior vertex never dissolve. Unreachable targets are still reported honestly (`reachedTarget`, `releaseStage`, `interiorTriangles`, `strippedInteriorTriangles`). With `interiorRemoval` off, the legacy **interior-release ladder** applies instead: non-silhouette detail is released in stages gated by target depth (≥75% preserved, ≥50% chain-past-corners, ≥25% free, <25% interior loops dissolve). Silhouette locks, the link condition, flips/slivers/growth, attribute charts and the collapse-distance bound are never relaxed. Boundary-component counts are topological loops, not semantic doors/windows. Preservation = constraint planes on boundary AND non-manifold edges (openings/T-junctions), crease edges, UV/normal seams + vertex locks (boundaries, top-N, bbox extremes) + importance weights (radial extremity, vertex-color mask).
  - `Visibility.cs` — exterior/interior classification. A source triangle is **exterior** if it wins ≥1 pixel in an unculled conservative (~1.25px-dilated) z-buffer render of the welded graph from any of 48 directions (6 axes + 42 Fibonacci-sphere); a vertex is exterior if any incident triangle is. A per-vertex **ray-escape rescue pass** on a uniform triangle grid (96³ max) closes the sampling gap: any vertex a ray can reach infinity from without hitting non-incident geometry is exterior. One-sided by design: hiding interior detail is harmless, so only the exterior→interior direction needs the rescue.
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
much faster than eyeballing regenerated meshes in the editor. Set `NOINT=1`
to disable interior removal for A/B comparison.

**Visual audit**: `lodharness audit <outdir> <files...>` renders every model's
LOD0 + 3 levels (ratios .5/.25/.125) to PNG: 6 orthographic **exterior** views
(camera outside along ±X/±Y/±Z) + 6 perspective **interior** views (camera at
bbox centre along each axis), via `Render.cs` (unculled rasterizer + own PNG
encoder). `audit.csv` diffs each LOD against LOD0: `extLostMax` (coverage lost
per exterior view — holes/removed items), `extPushed` (surfaces pushed back),
`intUncovMax` (see-through holes visible from inside), `intPushed` (interior
emptied = good). Exterior loss ≈0% is the safety bar; interior pushed >0 is
the removal win.

## Prefab-level validation

`Tools/Validation` holds the whole-prefab audit drivers
(`MegaCityAudit.cs`, `MegaCityRegressions.cs`, `RegenRepro.cs` — copied
temporarily into `Assets/ComplexLODs/Editor` to run inside Unity) plus the
offline coverage/boundary measurement and report scripts. See
`Tools/Validation/README.md` before rerunning the MegaCity matrix.

## Web demo site

`Web/` — zero-dependency Node site (`node Web/server.js`). `public/` = served
statics (viewer, `js/objio.js` only). `Web/engine/` = the JS port of
`Core/*.cs` + format loaders (`loaders.js`, `fbx.js`), writers
(`exporters.js`, `zip.js`) and `jobworker.mjs` — **server-side only, never
served**. Upload → `POST /api/generate` (NDJSON stream) → results via
`GET /api/preview|file|zip`; jobs land in `Web/store/<uuid>` and auto-delete
after `LODS_TTL_MIN` (default 30 min) or when the 1 GB store cap evicts
oldest. Presets mirror `ComplexLODProfile`. Bump the `?v=` asset query in
`index.html`/`app.js` together on every deploy. Cross-validate the port with
`node Web/test/compare.mjs` (diffs against `lodharness synth` output).

## Compile verification

The generated csproj references an uninstalled Unity version. To compile-check
ComplexLODs sources against an installed editor:

```
dotnet build %TEMP%\complexlods-check\compile-check.csproj
```

(project file at `C:\Users\nichi\AppData\Local\Temp\complexlods-check\compile-check.csproj`
globs `Assets/ComplexLODs/Editor/**/*.cs` and references
`C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Data\Managed\**\*.dll`).
