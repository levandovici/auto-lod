"""Check source boundary corners against frozen Unity output vertex positions.

A corner has boundary degree other than two, or a turn > 0.1 degrees.
This tests geometric anchors of exterior rims and interior openings even when
the opaque overview views cannot see them. It is not a semantic window count.
"""
import json
import sys
from pathlib import Path
import numpy as np
from scipy.spatial import cKDTree
from analyze_megacity import BASE, vectors, source_level


def corners(part):
    v = vectors(part["positions"])
    faces = np.concatenate([np.array(s["indices"], dtype=np.int64).reshape(-1, 3) for s in part["submeshes"]])
    good = np.linalg.norm(np.cross(v[faces[:, 1]] - v[faces[:, 0]], v[faces[:, 2]] - v[faces[:, 0]]), axis=1) >= 1e-6
    unique, weld = np.unique(v, axis=0, return_inverse=True)
    f = weld[faces[good]]
    edges = np.sort(np.concatenate([f[:, [0, 1]], f[:, [1, 2]], f[:, [2, 0]]]), axis=1)
    edges, counts = np.unique(edges, axis=0, return_counts=True)
    neighbors = {}
    for a, b in edges[counts == 1]:
        neighbors.setdefault(a, []).append(b)
        neighbors.setdefault(b, []).append(a)
    selected = []
    for a, ns in neighbors.items():
        if len(ns) != 2:
            selected.append(a)
            continue
        u, w = unique[ns] - unique[a]
        cosine = np.dot(u, w) / max(np.linalg.norm(u) * np.linalg.norm(w), 1e-30)
        if cosine > -np.cos(np.deg2rad(.1)):
            selected.append(a)
    return unique[selected]


def analyze(path):
    out = path.parent / "analysis" / (path.stem + "_boundaries.json")
    if out.exists():
        return
    data = json.loads(path.read_text())
    sources = {p["name"]: p for p in source_level(data)["parts"]}
    source_corners = {name: corners(p) for name, p in sources.items()}
    result = []
    for level in data["levels"]:
        missing, total, worst = 0, 0, 0
        for part in level["parts"]:
            source_name = part["name"]
            if level["index"]:
                suffix = "_LOD" + str(level["index"])
                assert source_name.endswith(suffix)
                source_name = source_name[:-len(suffix)]
            if source_name not in sources:
                continue  # Erroneously promoted authored stadium LOD1 is not part of reference LOD0.
            reference = source_corners[source_name]
            total += len(reference)
            if not len(reference):
                continue
            v = vectors(part["positions"])
            if not len(v):
                missing += len(reference)
                worst = None
                continue
            diagonal = np.linalg.norm(np.ptp(vectors(sources[source_name]["positions"]), axis=0))
            distance = cKDTree(v).query(reference, k=1)[0]
            missing += int(np.sum(distance > max(1e-6, diagonal * 1e-6)))
            if worst is not None:
                worst = max(worst, float(distance.max()))
        result.append(dict(level=level["index"], sourceCorners=total,
                           missingCorners=missing, maxCornerDistanceWorld=worst))
    out.parent.mkdir(exist_ok=True)
    out.write_text(json.dumps(result, indent=2))
    print(f"{path.stem}: missing boundary corners {max(r['missingCorners'] for r in result)}/{result[0]['sourceCorners']}", flush=True)


if __name__ == "__main__":
    for path in sorted((BASE / sys.argv[1]).glob("*.json")):
        analyze(path)
