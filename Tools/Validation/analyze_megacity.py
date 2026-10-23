"""Read frozen Unity exports; deterministic geometry-only comparisons.

Four fixed orthographic views, same framing at every LOD. Opaque, double-sided
flat shading isolates geometry; this does not evaluate textures or URP shaders.
Coverage loss/gain is normalized by source covered pixels, not the whole image.
Depth is in world units and compared only where both meshes cover a pixel.
No intersection-count proxy or invented quality score is used.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components

BASE = Path(os.environ.get("COMPLEXLODS_AUDIT_DIR", str(Path("Logs/MegaCityAudit"))))
SIZE = 320
VIEWS = [(1, .32, 1), (-1, .32, -1), (0, .12, 1), (0, .12, -1)]


def vectors(values):
    return np.array([[p["x"], p["y"], p["z"]] for p in values], dtype=np.float64).reshape(-1, 3)


def source_level(data):
    level = data["levels"][0]
    if data["model"] == "Football_01" and data["phase"] == "baseline":
        # Authored prefab LODGroup (fileID 7114645320404476517) assigns the
        # 82,863-triangle renderer to LOD0 and the 20,479-triangle renderer to
        # LOD1. Baseline Generate incorrectly promoted both to LOD0. Reference
        # only the real LOD0 plus eight unassigned doors. Keep the erroneous
        # lower output levels intact to expose that generator defect.
        level = dict(level, parts=[p for p in level["parts"] if p["name"] != "SM_Buildings_Football_01_LOD1"])
        assert sum(p["triangles"] for p in level["parts"]) == 84463
    return level


def mesh(level):
    vertices, triangles, slots = [], [], []
    offset = 0
    for part in level["parts"]:
        vertices.append(vectors(part["positions"]))
        for s, sub in enumerate(part["submeshes"]):
            faces = np.array(sub["indices"], dtype=np.int64).reshape(-1, 3)
            triangles.append(faces + offset)
            slots.extend([s] * len(faces))
        offset += len(vertices[-1])
    return np.concatenate(vertices), np.concatenate(triangles), np.array(slots)


def structure(part):
    v = vectors(part["positions"])
    f = np.concatenate([np.array(s["indices"], dtype=np.int64).reshape(-1, 3) for s in part["submeshes"]])
    if not len(f):
        return dict(components=0, boundaries=0, nonmanifold=0, degenerate=0, area=0, unique=0)
    cross = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])
    area2 = np.linalg.norm(cross, axis=1)
    good = area2 >= 1e-6  # Product's zero-area threshold in world space for these unit-scale prefabs.
    _, weld = np.unique(v, axis=0, return_inverse=True)
    f = weld[f[good]]
    used, f = np.unique(f, return_inverse=True)
    f = f.reshape(-1, 3)
    edges = np.sort(np.concatenate((f[:, [0, 1]], f[:, [1, 2]], f[:, [2, 0]])), axis=1)
    edges, count = np.unique(edges, axis=0, return_counts=True)
    adjacency = coo_matrix((np.ones(len(edges)), (edges[:, 0], edges[:, 1])), shape=(len(used), len(used)))
    components = connected_components(adjacency, directed=False, return_labels=False) if len(used) else 0
    return dict(components=int(components), boundaries=int(np.sum(count == 1)),
                nonmanifold=int(np.sum(count > 2)), degenerate=int(np.sum(~good)),
                area=float(area2[good].sum() / 2), unique=len(used))


def frame(source, direction):
    forward = np.array(direction, dtype=float)
    forward /= np.linalg.norm(forward)
    right = np.cross([0., 1., 0.], forward)
    right /= np.linalg.norm(right)
    up = np.cross(forward, right)
    basis = np.stack((right, up, forward), axis=1)
    proj = source @ basis
    center = (proj.min(axis=0) + proj.max(axis=0)) / 2
    scale = (SIZE - 24) / max(np.ptp(proj[:, :2], axis=0))
    return basis, center, scale


def render(v, faces, slots, camera):
    # Reuse identical geometry/camera captures across presets and plateaued
    # levels. The cache changes execution cost, not the measurement definition.
    fingerprint = hashlib.sha256()
    for array in (v, faces, slots, camera[0], camera[1], np.array([camera[2], SIZE])):
        fingerprint.update(str((array.shape, array.dtype.str)).encode())
        fingerprint.update(array.tobytes())
    directory = BASE / "render-cache"
    directory.mkdir(exist_ok=True)
    path = directory / (fingerprint.hexdigest() + ".npz")
    if path.exists():
        with np.load(path) as frozen:
            return frozen["image"], frozen["depth"]
    image, depth = raster(v, faces, slots, camera)
    np.savez_compressed(path, image=image, depth=depth)
    return image, depth


def raster(v, faces, slots, camera):
    basis, center, scale = camera
    p = (v @ basis - center)
    p[:, :2] *= scale
    p[:, 0] += SIZE / 2
    p[:, 1] = SIZE / 2 - p[:, 1]
    depth = np.full((SIZE, SIZE), -np.inf)
    image = np.full((SIZE, SIZE, 3), 242, dtype=np.uint8)
    colors = np.array([[123, 163, 186], [200, 159, 99], [143, 182, 126], [173, 143, 187]])
    normals = np.cross(v[faces[:, 1]] - v[faces[:, 0]], v[faces[:, 2]] - v[faces[:, 0]])
    normals /= np.maximum(np.linalg.norm(normals, axis=1)[:, None], 1e-30)
    light = np.array([.3, .8, .5]); light /= np.linalg.norm(light)
    shades = .45 + .55 * np.abs(normals @ light)
    for ti, face in enumerate(faces):
        a, b, c = p[face]
        x0 = max(0, int(np.floor(min(a[0], b[0], c[0]))))
        x1 = min(SIZE - 1, int(np.ceil(max(a[0], b[0], c[0]))))
        y0 = max(0, int(np.floor(min(a[1], b[1], c[1]))))
        y1 = min(SIZE - 1, int(np.ceil(max(a[1], b[1], c[1]))))
        den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
        if abs(den) < 1e-10 or x1 < x0 or y1 < y0:
            continue
        yy, xx = np.mgrid[y0:y1+1, x0:x1+1]
        xx = xx + .5; yy = yy + .5
        u = ((b[1] - c[1]) * (xx - c[0]) + (c[0] - b[0]) * (yy - c[1])) / den
        w = ((c[1] - a[1]) * (xx - c[0]) + (a[0] - c[0]) * (yy - c[1])) / den
        z = 1 - u - w
        d = u * a[2] + w * b[2] + z * c[2]
        tile = depth[y0:y1+1, x0:x1+1]
        mask = (u >= -1e-9) & (w >= -1e-9) & (z >= -1e-9) & (d > tile)
        tile[mask] = d[mask]
        image[y0:y1+1, x0:x1+1][mask] = np.uint8(colors[slots[ti] % len(colors)] * shades[ti])
    return image, depth


def compare(source, result, diagonal):
    s, r = np.isfinite(source), np.isfinite(result)
    common = s & r
    delta = np.abs(source[common] - result[common])
    return {
        "coverageLostPct": float(100 * np.sum(s & ~r) / max(1, s.sum())),
        "coverageAddedPct": float(100 * np.sum(~s & r) / max(1, s.sum())),
        "depthChangedPct": float(100 * np.sum(delta > diagonal * .001) / max(1, s.sum())),
        "depthP95World": float(np.percentile(delta, 95)) if len(delta) else None,
    }


def analyze(path):
    data = json.loads(path.read_text())
    out = path.parent / "analysis"; out.mkdir(exist_ok=True)
    levels = [source_level(data)] + data["levels"][1:]
    src_v, src_f, src_s = mesh(levels[0])
    diagonal = float(np.linalg.norm(np.ptp(src_v, axis=0)))
    cameras = [frame(src_v, view) for view in VIEWS]
    src_renders = [render(src_v, src_f, src_s, camera) for camera in cameras]
    rows = []
    sheet = Image.new("RGB", (SIZE * len(levels), (SIZE + 46) * len(VIEWS)), "#ffffff")
    draw = ImageDraw.Draw(sheet)
    src_struct = [structure(p) for p in levels[0]["parts"]]
    for l, level in enumerate(levels):
        v, f, slots = mesh(level)
        if not np.isfinite(v).all():
            raise ValueError(f"Non-finite positions: {path}, LOD{l}")
        if len(f) and (f.min() < 0 or f.max() >= len(v)):
            raise ValueError(f"Invalid indices: {path}, LOD{l}")
        structs = [structure(p) for p in level["parts"]]
        views = []
        for vi, camera in enumerate(cameras):
            image, depth = src_renders[vi] if l == 0 else render(v, f, slots, camera)
            comp = compare(src_renders[vi][1], depth, diagonal)
            views.append(comp)
            y = vi * (SIZE + 46)
            sheet.paste(Image.fromarray(image), (l * SIZE, y + 46))
            draw.text((l * SIZE + 8, y + 5), f"{data['preset']} LOD{l}: {len(f):,} tris", fill="black")
            draw.text((l * SIZE + 8, y + 22),
                      f"lost {comp['coverageLostPct']:.2f}%  added {comp['coverageAddedPct']:.2f}%", fill="black")
        rows.append({
            "level": l, "triangles": len(f), "ratio": len(f) / len(src_f),
            "requested": sum(p["requestedTriangles"] for p in level["parts"]),
            "actualTargetsMet": [p["triangles"] <= p["requestedTriangles"] for p in level["parts"]],
            "reportedTargetsMet": [p["reportedReached"] for p in level["parts"]],
            "sourceReused": [p["sourceReused"] for p in level["parts"]],
            "milliseconds": sum(p["milliseconds"] for p in level["parts"]),
            "structure": structs, "views": views,
            "maxCoverageLostPct": max(m["coverageLostPct"] for m in views),
            "maxCoverageAddedPct": max(m["coverageAddedPct"] for m in views),
            "maxDepthChangedPct": max(m["depthChangedPct"] for m in views),
            "componentDelta": sum(s["components"] for s in structs) - sum(s["components"] for s in src_struct),
        })
    sheet.save(out / (path.stem + ".png"))
    result = {k: data[k] for k in ("model", "preset", "phase", "generationMs", "unityVersion")}
    result.update(sourceTriangles=len(src_f), sourceStructures=src_struct, levels=rows)
    (out / (path.stem + ".json")).write_text(json.dumps(result, indent=2))
    print(f"{path.stem}: source={len(src_f)} lowest={rows[-1]['triangles']} "
          f"loss={rows[-1]['maxCoverageLostPct']:.2f}% comps={rows[-1]['componentDelta']:+d}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=["baseline", "fixed"])
    parser.add_argument("--model", default="*")
    args = parser.parse_args()
    for path in sorted((BASE / args.phase).glob(args.model + "_*.json")):
        if not (path.parent / "analysis" / path.name).exists():
            analyze(path)
