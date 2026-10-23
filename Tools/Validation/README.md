# MegaCity geometry validation

This audit uses Unity's imported meshes and `ComplexLODGenerator.Generate` on
preview-scene copies of five complete JC_LP_MegaCity prefabs: Airport 01,
Skyscraper 02, Church 01, City Hall 01, and Football 01. Performance, Balanced,
and Quality generate 2, 3, and 5 lower levels respectively: 15 chains and
50 generated levels.

The third-party assets and exported geometry are local prerequisites/artifacts,
not part of the repository. Original prefabs and the user's open scene are not
saved by the audit.

## Saved results

- `Artifacts/MegaCityAudit/index.html`: before/after gallery and measurements.
- `Artifacts/MegaCityAudit/summary.md`: all 15 model/preset rows.
- `Artifacts/MegaCityAudit/{baseline,fixed}/*.json`: frozen Unity exports.
- `Artifacts/MegaCityAudit/{baseline,fixed}/analysis`: diagnostic renders,
  per-level measurements and boundary-corner checks.
- `Artifacts/MegaCityAudit/regressions{,-baseline}.txt`: focused checks.
- `Assets/ComplexLODs/Generated/Audit/fixed/<Model>/<Preset>`: saved Unity
  review prefabs and generated meshes. Open a prefab and scrub its LODGroup.

The baseline was captured from revision `f39efa3`. It must not be regenerated
using corrected code and then described as the old algorithm.

## Repeat the corrected audit in Unity

Use a working Unity editor with this project open and JC_LP_MegaCity installed.
The C# drivers are intentionally outside Assets, so ordinary project imports do
not run validation. Copy them temporarily into the editor assembly:

```powershell
Copy-Item Tools/Validation/MegaCityAudit.cs Assets/ComplexLODs/Editor/MegaCityAudit.cs
Copy-Item Tools/Validation/MegaCityRegressions.cs Assets/ComplexLODs/Editor/MegaCityRegressions.cs
```

The model audit polls an explicit request file:

```powershell
Set-Content Logs/MegaCityAudit.request fixed
```

The regression driver checks its request after an assembly reload. Create
`Logs/MegaCityRegressions.request` before importing its driver, then focus Unity.
Use `New-Item -ItemType File -Force Logs/MegaCityRegressions.request` to create it.
Read `Logs/MegaCityAudit/status.txt`, `regressions.txt`, and any `error.txt`.
Request files live under `Logs/` because Unity wipes `Temp/` at startup.

`RegenRepro.cs` replays repeated generation across preset switches and counts
new children, mesh assets and LOD slots (writes `Logs/RegenRepro.txt`). All
drivers also work headless, which avoids the focus/copy dance entirely:

```powershell
Copy-Item Tools/Validation/*.cs Assets/ComplexLODs/Editor/
New-Item -ItemType File -Force Logs/MegaCityRegressions.request
& "C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Unity.exe" -batchmode -nographics `
    -projectPath . -executeMethod RegenRepro.RunBatch -quit `
    -logFile Temp/unity.log
# MegaCityAudit.RunBatch / MegaCityRegressions.RunBatch run individually the same way.
```

Completed JSON jobs are skipped to avoid regenerating frozen evidence. To make
a fresh run, first move the existing `Logs/MegaCityAudit` directory to a separate
archive. A user cancellation is recorded as an error; it is not a passing run.

After the run, remove the two temporary C# files and their `.meta` files from
`Assets/ComplexLODs/Editor`. Keep the tools in `Tools/Validation`.

## Measure and render frozen exports

The Python tools require NumPy, SciPy and Pillow. Point them at an existing
export directory; they never invoke generation:

```powershell
$env:COMPLEXLODS_AUDIT_DIR = "$PWD/Artifacts/MegaCityAudit"
python Tools/Validation/analyze_megacity.py fixed
python Tools/Validation/boundary_megacity.py fixed
python Tools/Validation/report_megacity.py
```

`report_megacity.py` requires complete baseline and fixed measurements. It
formats those measurements without rerunning the analysis. Existing analysis
JSON files are skipped, and identical geometry/camera renders are cached.

Geometry renders use four fixed orthographic views, 320 × 320 pixels per tile,
opaque double-sided surfaces, flat shading, and material-slot colors. They are
not Unity screenshots or a test of textures, transparency, runtime transitions,
or every possible self-intersection.

- **Coverage loss:** source-covered pixels missing from output, divided by
  source-covered pixels.
- **Added coverage:** output pixels outside the source coverage, with the same
  denominator. Zero coverage loss alone does not establish unchanged geometry.
- **Depth change:** common pixels whose nearest visible surface differs by more
  than 0.1% of the source bounding-box diagonal, relative to source coverage.
- **Boundary corners:** boundary junctions and turns greater than 0.1 degrees.
  A source corner is missing if no output vertex lies within one millionth of
  the renderer's diagonal, with a 0.000001 world-unit minimum. This also checks
  internal openings hidden in the overview renders, but does not count semantic
  windows or doors.
- **Connected components:** measured after exact position welding and removing
  triangles below the product's degenerate-area threshold. Attribute splits do
  not count as disconnected geometry.

The stadium's baseline had a separate generator defect: it included an authored
LOD1 as additional LOD0 geometry. The reference is the real authored LOD0 plus
eight unassigned doors, totaling 84,463 triangles. Erroneous lower-level output
is retained in the baseline comparisons to expose that defect.

## Algorithm references

The implementation remains original. The relevant design distinction is
between choosing a cheap collapse and constraining a valid placement:

- [CGAL constrained placement](https://doc.cgal.org/latest/Surface_mesh_simplification/classCGAL_1_1Surface__mesh__simplification_1_1Constrained__placement.html)
- [CGAL simplification overview](https://doc.cgal.org/latest/Surface_mesh_simplification/index.html)

Preserving source curves and topology can leave a requested triangle target
unmet. The current implementation's stopping point is conservative; it is not
a proof of the minimum possible triangle count for the model.
