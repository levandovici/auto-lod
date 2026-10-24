// ComplexLODs simplification stack — JS port of Assets/ComplexLODs/Editor/Core.
// Probabilistic-quadric edge-collapse simplifier with feature preservation:
// boundary/crease/seam constraint planes, feature-chain collapse rules,
// silhouette locking and the staged interior-release ladder. Runs offline
// (Node) and inside a Web Worker — no DOM, no dependencies.
//
// Mesh exchange format (flat, structured-clone friendly):
//   {
//     positions: Float32Array|number[]  (3 per vertex),
//     normals:   Float32Array|null      (3 per vertex, or null),
//     uvs:       Float32Array|null      (2 per vertex, or null),
//     colors:    Uint8Array|null        (4 per vertex, or null),
//     submeshes: [Uint32Array|number[]] (triangle indices, one array per submesh)
//   }

const ATTR_EPS = 1e-6;
const MAX_COLLAPSE_DISTANCE = 4; // in units of endpoints' local feature scale

// --- vector helpers (plain {x,y,z} objects) ----------------------------------
const v3 = (x, y, z) => ({ x, y, z });
const V3_ZERO = { x: 0, y: 0, z: 0 };
const V3_UP = { x: 0, y: 1, z: 0 };
const V3_RIGHT = { x: 1, y: 0, z: 0 };
const sub3 = (a, b) => v3(a.x - b.x, a.y - b.y, a.z - b.z);
const add3 = (a, b) => v3(a.x + b.x, a.y + b.y, a.z + b.z);
const scale3 = (a, s) => v3(a.x * s, a.y * s, a.z * s);
const mid3 = (a, b) => v3((a.x + b.x) * 0.5, (a.y + b.y) * 0.5, (a.z + b.z) * 0.5);
const dot3 = (a, b) => a.x * b.x + a.y * b.y + a.z * b.z;
const cross3 = (a, b) => v3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
const sqrLen3 = (a) => dot3(a, a);
const len3 = (a) => Math.sqrt(sqrLen3(a));
const norm3 = (a) => { const m = len3(a); return m > 1e-20 ? scale3(a, 1 / m) : v3(0, 0, 0); };
const eq3 = (a, b) => a.x === b.x && a.y === b.y && a.z === b.z;
const angle3 = (a, b) => { // Unity Vector3.Angle: degrees, normalized inputs
    const na = norm3(a), nb = norm3(b);
    return Math.acos(Math.min(1, Math.max(-1, dot3(na, nb)))) * 180 / Math.PI;
};
const lerp3 = (a, b, t) => v3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
const lerp2 = (a, b, t) => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
const lerp4 = (a, b, t) => ({
    x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t,
    z: a.z + (b.z - a.z) * t, w: a.w + (b.w - a.w) * t
});
const lerpColor = (a, b, t) => ({
    r: Math.round(a.r + (b.r - a.r) * t), g: Math.round(a.g + (b.g - a.g) * t),
    b: Math.round(a.b + (b.b - a.b) * t), a: Math.round(a.a + (b.a - a.a) * t)
});
const clamp01 = (v) => v < 0 ? 0 : v > 1 ? 1 : v;
const lerp = (a, b, t) => a + (b - a) * t;

const ZERO2 = { x: 0, y: 0 };
const ZERO4 = { x: 0, y: 0, z: 0, w: 0 };
const WHITE = { r: 255, g: 255, b: 255, a: 255 };
const NO_BONES = { w0: 0, w1: 0, w2: 0, w3: 0, i0: 0, i1: 0, i2: 0, i3: 0 };

const approx2 = (a, b) => (a.x - b.x) ** 2 + (a.y - b.y) ** 2 < ATTR_EPS * ATTR_EPS;
const approx3 = (a, b) => sqrLen3(sub3(a, b)) < ATTR_EPS * ATTR_EPS;
const approx4 = (a, b) =>
    (a.x - b.x) ** 2 + (a.y - b.y) ** 2 + (a.z - b.z) ** 2 + (a.w - b.w) ** 2 < ATTR_EPS * ATTR_EPS;
const approxBW = (a, b) =>
    Math.abs(a.w0 - b.w0) < ATTR_EPS && Math.abs(a.w1 - b.w1) < ATTR_EPS &&
    Math.abs(a.w2 - b.w2) < ATTR_EPS && Math.abs(a.w3 - b.w3) < ATTR_EPS &&
    a.i0 === b.i0 && a.i1 === b.i1 && a.i2 === b.i2 && a.i3 === b.i3;
const eqBW = (a, b) =>
    a.w0 === b.w0 && a.w1 === b.w1 && a.w2 === b.w2 && a.w3 === b.w3 &&
    a.i0 === b.i0 && a.i1 === b.i1 && a.i2 === b.i2 && a.i3 === b.i3;

// --- quadric ------------------------------------------------------------------
class Quadric {
    constructor() {
        this.m00 = 0; this.m01 = 0; this.m02 = 0; this.m03 = 0;
        this.m11 = 0; this.m12 = 0; this.m13 = 0;
        this.m22 = 0; this.m23 = 0;
        this.m33 = 0;
    }

    static fromPlane(a, b, c, d, weight) {
        const q = new Quadric();
        q.m00 = weight * a * a;
        q.m01 = weight * a * b;
        q.m02 = weight * a * c;
        q.m03 = weight * a * d;
        q.m11 = weight * b * b;
        q.m12 = weight * b * c;
        q.m13 = weight * b * d;
        q.m22 = weight * c * c;
        q.m23 = weight * c * d;
        q.m33 = weight * d * d;
        return q;
    }

    add(o) {
        this.m00 += o.m00; this.m01 += o.m01; this.m02 += o.m02; this.m03 += o.m03;
        this.m11 += o.m11; this.m12 += o.m12; this.m13 += o.m13;
        this.m22 += o.m22; this.m23 += o.m23;
        this.m33 += o.m33;
    }

    scale(s) {
        this.m00 *= s; this.m01 *= s; this.m02 *= s; this.m03 *= s;
        this.m11 *= s; this.m12 *= s; this.m13 *= s;
        this.m22 *= s; this.m23 *= s;
        this.m33 *= s;
    }

    // Probabilistic triangle quadric (Trettner & Kobbelt 2020), area-normalised.
    static fromProbabilisticTriangle(p, q, r, relSigma) {
        const px = p.x, py = p.y, pz = p.z;
        const qx = q.x, qy = q.y, qz = q.z;
        const rx = r.x, ry = r.y, rz = r.z;

        const ux = qx - px, uy = qy - py, uz = qz - pz;
        const vx = rx - px, vy = ry - py, vz = rz - pz;
        const nx = uy * vz - uz * vy;
        const ny = uz * vx - ux * vz;
        const nz = ux * vy - uy * vx;
        const nLen = Math.sqrt(nx * nx + ny * ny + nz * nz);

        const d = px * (qy * rz - qz * ry) - py * (qx * rz - qz * rx) + pz * (qx * ry - qy * rx);

        const ex = px - qx, ey = py - qy, ez = pz - qz;
        const fx = qx - rx, fy = qy - ry, fz = qz - rz;
        const gx = rx - px, gy = ry - py, gz = rz - pz;

        const S = ex * ex + ey * ey + ez * ez
                + fx * fx + fy * fy + fz * fz
                + gx * gx + gy * gy + gz * gz;

        const meanEdge = (Math.sqrt(ex * ex + ey * ey + ez * ez)
                        + Math.sqrt(fx * fx + fy * fy + fz * fz)
                        + Math.sqrt(gx * gx + gy * gy + gz * gz)) / 3;
        const sigma2 = relSigma * relSigma * meanEdge * meanEdge;
        const s4 = sigma2 * sigma2;
        const reg = sigma2 * S + 6 * s4;

        const o = new Quadric();
        o.m00 = nx * nx + reg - sigma2 * (ex * ex + fx * fx + gx * gx);
        o.m11 = ny * ny + reg - sigma2 * (ey * ey + fy * fy + gy * gy);
        o.m22 = nz * nz + reg - sigma2 * (ez * ez + fz * fz + gz * gz);
        o.m01 = nx * ny - sigma2 * (ex * ey + fx * fy + gx * gy);
        o.m02 = nx * nz - sigma2 * (ex * ez + fx * fz + gx * gz);
        o.m12 = ny * nz - sigma2 * (ey * ez + fy * fz + gy * gz);

        const pqx = py * qz - pz * qy, pqy = pz * qx - px * qz, pqz = px * qy - py * qx;
        const qrx = qy * rz - qz * ry, qry = qz * rx - qx * rz, qrz = qx * ry - qy * rx;
        const rpx = ry * pz - rz * py, rpy = rz * px - rx * pz, rpz = rx * py - ry * px;

        const t1x = (qy - py) * pqz - (qz - pz) * pqy;
        const t1y = (qz - pz) * pqx - (qx - px) * pqz;
        const t1z = (qx - px) * pqy - (qy - py) * pqx;
        const t2x = (ry - qy) * qrz - (rz - qz) * qry;
        const t2y = (rz - qz) * qrx - (rx - qx) * qrz;
        const t2z = (rx - qx) * qry - (ry - qy) * qrx;
        const t3x = (py - ry) * rpz - (pz - rz) * rpy;
        const t3y = (pz - rz) * rpx - (px - rx) * rpz;
        const t3z = (px - rx) * rpy - (py - ry) * rpx;

        const bx = nx * d + sigma2 * (t1x + t2x + t3x) + 2 * s4 * (px + qx + rx);
        const by = ny * d + sigma2 * (t1y + t2y + t3y) + 2 * s4 * (py + qy + ry);
        const bz = nz * d + sigma2 * (t1z + t2z + t3z) + 2 * s4 * (pz + qz + rz);

        o.m03 = -bx;
        o.m13 = -by;
        o.m23 = -bz;
        o.m33 = d * d;

        const scale = nLen > 1e-12 ? 1 / (2 * nLen) : 0;
        o.scale(scale);
        return o;
    }

    eval(v) {
        const x = v.x, y = v.y, z = v.z;
        return this.m00 * x * x + 2 * this.m01 * x * y + 2 * this.m02 * x * z + 2 * this.m03 * x +
               this.m11 * y * y + 2 * this.m12 * y * z + 2 * this.m13 * y +
               this.m22 * z * z + 2 * this.m23 * z +
               this.m33;
    }
}

// --- mesh graph ---------------------------------------------------------------
function edgeKey(a, b) {
    return a < b ? a * 4294967296 + b : b * 4294967296 + a;
}

class MeshGraph {
    triCross(tri) {
        const c0 = this.triCorners[tri * 3], c1 = this.triCorners[tri * 3 + 1], c2 = this.triCorners[tri * 3 + 2];
        const p0 = this.pos[this.cornerVert[c0]], p1 = this.pos[this.cornerVert[c1]], p2 = this.pos[this.cornerVert[c2]];
        return cross3(sub3(p1, p0), sub3(p2, p0));
    }

    triNormal(tri) {
        const n = this.triCross(tri);
        const m = len3(n);
        return m > 1e-20 ? scale3(n, 1 / m) : v3(0, 0, 0);
    }

    getEdge(a, b) {
        return this.edges.get(edgeKey(a, b));
    }

    removeTri(tri) {
        if (!this.triAlive[tri]) return;
        this.triAlive[tri] = false;
        this.aliveTris--;
    }

    resolveWedge(wedge) {
        let guard = 0;
        while (this.wedges[wedge].alias >= 0 && guard++ < 1024)
            wedge = this.wedges[wedge].alias;
        return wedge;
    }

    // Number of connected components formed by boundary edges.
    countBoundaryComponents() {
        const parent = new Map();
        const find = (x) => {
            let r = x;
            while (parent.has(r) && parent.get(r) !== r) r = parent.get(r);
            while (parent.has(x) && parent.get(x) !== r) { const p2 = parent.get(x); parent.set(x, r); x = p2; }
            return r;
        };
        const roots = new Set();
        for (const e of this.edges.values()) {
            if (e.removed || !e.boundary) continue;
            if (!parent.has(e.a)) parent.set(e.a, e.a);
            if (!parent.has(e.b)) parent.set(e.b, e.b);
            const ra = find(e.a), rb = find(e.b);
            if (ra !== rb) parent.set(rb, ra);
        }
        for (const e of this.edges.values()) {
            if (e.removed || !e.boundary) continue;
            roots.add(find(e.a));
        }
        return roots.size;
    }

    static attributesEqual(a, b) {
        return approx3(a.normal, b.normal) && approx4(a.tangent, b.tangent) &&
            approx2(a.uv0, b.uv0) && approx2(a.uv1, b.uv1) &&
            approx2(a.uv2, b.uv2) && approx2(a.uv3, b.uv3) &&
            a.color.r === b.color.r && a.color.g === b.color.g &&
            a.color.b === b.color.b && a.color.a === b.color.a &&
            approxBW(a.boneWeight, b.boneWeight);
    }

    static build(mesh, weldTolerance) {
        const srcCount = mesh.positions.length / 3;
        const srcPos = new Array(srcCount);
        for (let i = 0; i < srcCount; i++)
            srcPos[i] = v3(mesh.positions[i * 3], mesh.positions[i * 3 + 1], mesh.positions[i * 3 + 2]);

        const weldLookup = weldPositions(srcPos, weldTolerance);
        let vCount = 0;
        for (let i = 0; i < weldLookup.length; i++) if (weldLookup[i] + 1 > vCount) vCount = weldLookup[i] + 1;

        const g = new MeshGraph();
        g.pos = new Array(vCount);
        const seen = new Uint8Array(vCount);
        for (let i = 0; i < srcCount; i++) {
            const w = weldLookup[i];
            if (!seen[w]) { g.pos[w] = srcPos[i]; seen[w] = 1; }
        }

        const srcNormals = mesh.normals;
        const srcTangents = mesh.tangents;
        const srcColors = mesh.colors;
        const srcUv = mesh.uvs;
        const srcBone = mesh.boneWeights;

        const hasN = srcNormals != null && srcNormals.length === mesh.positions.length;
        const hasT = srcTangents != null && srcTangents.length === srcCount * 4;
        const hasC = srcColors != null && srcColors.length === srcCount * 4;
        const hasU0 = srcUv != null && srcUv.length === srcCount * 2;
        const hasB = srcBone != null && srcBone.length === srcCount * 8;

        const readV3 = (arr, i) => arr ? v3(arr[i * 3], arr[i * 3 + 1], arr[i * 3 + 2]) : null;
        const submeshes = mesh.submeshes;
        let triTotal = 0;
        for (const idx of submeshes) triTotal += (idx.length / 3) | 0;

        const cornerVert = new Int32Array(triTotal * 3);
        const cornerWedge = new Int32Array(triTotal * 3);
        const triCorners = new Int32Array(triTotal * 3);
        const triSub = new Int32Array(triTotal);

        const wedgeList = [];
        g.vertWedges = new Array(vCount);
        g.vertTris = new Array(vCount);
        for (let i = 0; i < vCount; i++) { g.vertWedges[i] = []; g.vertTris[i] = []; }

        let triIndex = 0, cornerIndex = 0;
        for (let s = 0; s < submeshes.length; s++) {
            const idx = submeshes[s];
            for (let i = 0; i + 2 < idx.length; i += 3) {
                for (let k = 0; k < 3; k++) {
                    const ov = idx[i + k];
                    const v = weldLookup[ov];
                    cornerVert[cornerIndex] = v;
                    cornerWedge[cornerIndex] = findOrAddWedge(g, wedgeList, v, ov,
                        hasN ? readV3(srcNormals, ov) : V3_UP,
                        hasT ? { x: srcTangents[ov * 4], y: srcTangents[ov * 4 + 1], z: srcTangents[ov * 4 + 2], w: srcTangents[ov * 4 + 3] } : ZERO4,
                        hasC ? { r: srcColors[ov * 4], g: srcColors[ov * 4 + 1], b: srcColors[ov * 4 + 2], a: srcColors[ov * 4 + 3] } : WHITE,
                        hasU0 ? { x: srcUv[ov * 2], y: srcUv[ov * 2 + 1] } : ZERO2,
                        ZERO2, ZERO2, ZERO2,
                        hasB ? {
                            w0: srcBone[ov * 8], w1: srcBone[ov * 8 + 1], w2: srcBone[ov * 8 + 2], w3: srcBone[ov * 8 + 3],
                            i0: srcBone[ov * 8 + 4] | 0, i1: srcBone[ov * 8 + 5] | 0, i2: srcBone[ov * 8 + 6] | 0, i3: srcBone[ov * 8 + 7] | 0
                        } : NO_BONES);
                    cornerIndex++;
                }
                triCorners[triIndex * 3] = cornerIndex - 3;
                triCorners[triIndex * 3 + 1] = cornerIndex - 2;
                triCorners[triIndex * 3 + 2] = cornerIndex - 1;
                triSub[triIndex] = s;
                triIndex++;
            }
        }

        g.wedges = wedgeList;
        g.cornerVert = cornerVert;
        g.cornerWedge = cornerWedge;
        g.triCorners = triCorners.subarray(0, triIndex * 3);
        g.triSub = triSub.subarray(0, triIndex);
        g.triAlive = new Uint8Array(triIndex).fill(1);
        g.aliveTris = triIndex;
        g.locked = new Uint8Array(vCount);
        g.weight = new Float64Array(vCount).fill(1);
        g.edges = new Map();

        // Edges + vertex adjacency
        for (let t = 0; t < triIndex; t++) {
            for (let k = 0; k < 3; k++) {
                const c = triCorners[t * 3 + k];
                g.vertTris[cornerVert[c]].push(t);
            }
            for (let k = 0; k < 3; k++) {
                const va = cornerVert[triCorners[t * 3 + k]];
                const vb = cornerVert[triCorners[t * 3 + (k + 1) % 3]];
                if (va === vb) continue;
                const key = edgeKey(va, vb);
                let e = g.edges.get(key);
                if (!e) {
                    e = {
                        a: Math.min(va, vb), b: Math.max(va, vb), tris: [],
                        removed: false, version: 0, boundary: false, seam: false,
                        phantom: false, feature: false, creaseAngle: 0
                    };
                    g.edges.set(key, e);
                }
                if (!e.tris.includes(t)) e.tris.push(t);
            }
        }

        // Preflight: drop zero-area source triangles.
        for (let t = 0; t < g.triAlive.length; t++)
            if (sqrLen3(g.triCross(t)) < 1e-12) g.removeTri(t);

        // Classification
        for (const e of g.edges.values()) {
            for (let i = e.tris.length - 1; i >= 0; i--)
                if (!g.triAlive[e.tris[i]]) e.tris.splice(i, 1);
            if (e.tris.length === 0) { e.removed = true; continue; }
            e.boundary = e.tris.length === 1;
            if (e.tris.length === 2) {
                e.creaseAngle = angle3(g.triNormal(e.tris[0]), g.triNormal(e.tris[1]));
                e.seam = isSeamEdge(g, e);
            }
        }

        // Per-vertex local feature scale: mean incident edge length.
        const eSum = new Float64Array(vCount);
        const eCnt = new Int32Array(vCount);
        let global = 0, gn = 0;
        for (const e of g.edges.values()) {
            if (e.removed) continue;
            const l = len3(sub3(g.pos[e.a], g.pos[e.b]));
            eSum[e.a] += l; eCnt[e.a]++;
            eSum[e.b] += l; eCnt[e.b]++;
            global += l; gn++;
        }
        const gMean = gn > 0 ? global / gn : 1;
        g.localScale = new Float64Array(vCount);
        for (let i = 0; i < vCount; i++)
            g.localScale[i] = eCnt[i] > 0 ? eSum[i] / eCnt[i] : gMean;

        return g;
    }
}

function isSeamEdge(g, e) {
    for (let k = 0; k < 2; k++) {
        const v = k === 0 ? e.a : e.b;
        let w0 = -1, w1 = -1;
        for (let c = 0; c < 3; c++) {
            let corner = g.triCorners[e.tris[0] * 3 + c];
            if (g.cornerVert[corner] === v) w0 = g.cornerWedge[corner];
            corner = g.triCorners[e.tris[1] * 3 + c];
            if (g.cornerVert[corner] === v) w1 = g.cornerWedge[corner];
        }
        if (w0 >= 0 && w1 >= 0 && w0 !== w1) return true;
    }
    return false;
}

function findOrAddWedge(g, wedgeList, v, ov, n, t, c, u0, u1, u2, u3, bw) {
    const owned = g.vertWedges[v];
    for (let i = 0; i < owned.length; i++) {
        const w = wedgeList[owned[i]];
        if (approx3(w.normal, n) && approx4(w.tangent, t) &&
            approx2(w.uv0, u0) && approx2(w.uv1, u1) && approx2(w.uv2, u2) && approx2(w.uv3, u3) &&
            w.color.r === c.r && w.color.g === c.g && w.color.b === c.b && w.color.a === c.a &&
            approxBW(w.boneWeight, bw)) {
            return owned[i];
        }
    }
    wedgeList.push({
        vertex: v, alias: -1, removed: false, srcVertex: ov,
        normal: n, tangent: t, color: c,
        uv0: u0, uv1: u1, uv2: u2, uv3: u3, boneWeight: bw
    });
    owned.push(wedgeList.length - 1);
    return wedgeList.length - 1;
}

function weldPositions(src, tolerance) {
    const map = new Int32Array(src.length);
    if (tolerance <= 0) {
        const exact = new Map();
        for (let i = 0; i < src.length; i++) {
            const p = src[i];
            const key = p.x + '|' + p.y + '|' + p.z;
            let id = exact.get(key);
            if (id === undefined) { id = exact.size; exact.set(key, id); }
            map[i] = id;
        }
    } else {
        const grid = new Map();
        const welded = [];
        for (let i = 0; i < src.length; i++) {
            const p = src[i];
            const cx = Math.floor(p.x / tolerance);
            const cy = Math.floor(p.y / tolerance);
            const cz = Math.floor(p.z / tolerance);
            let found = -1;
            let best = tolerance * tolerance;
            for (let dx = -1; dx <= 1 && found < 0; dx++)
            for (let dy = -1; dy <= 1 && found < 0; dy++)
            for (let dz = -1; dz <= 1 && found < 0; dz++) {
                const bucket = grid.get((cx + dx) + '|' + (cy + dy) + '|' + (cz + dz));
                if (!bucket) continue;
                for (let k = 0; k < bucket.length; k++) {
                    const d = sqrLen3(sub3(welded[bucket[k]], p));
                    if (d <= best) { best = d; found = bucket[k]; }
                }
            }
            if (found < 0) {
                found = welded.length;
                welded.push(p);
                const cellKey = cx + '|' + cy + '|' + cz;
                let bucket = grid.get(cellKey);
                if (!bucket) { bucket = []; grid.set(cellKey, bucket); }
                bucket.push(found);
            }
            map[i] = found;
        }
    }
    return map;
}

// --- min heap ------------------------------------------------------------------
class MinHeap {
    constructor() { this.data = []; }
    get count() { return this.data.length; }
    push(cost, edge, version) {
        const data = this.data;
        data.push({ cost, edge, version });
        let i = data.length - 1;
        while (i > 0) {
            const p = (i - 1) >> 1;
            if (data[p].cost <= data[i].cost) break;
            const tmp = data[p]; data[p] = data[i]; data[i] = tmp;
            i = p;
        }
    }
    pop() {
        const data = this.data;
        const top = data[0];
        const last = data.pop();
        if (data.length > 0) {
            data[0] = last;
            let i = 0;
            for (;;) {
                const l = i * 2 + 1, r = l + 1;
                let m = i;
                if (l < data.length && data[l].cost < data[m].cost) m = l;
                if (r < data.length && data[r].cost < data[m].cost) m = r;
                if (m === i) break;
                const tmp = data[m]; data[m] = data[i]; data[i] = tmp;
                i = m;
            }
        }
        return top;
    }
}

// --- options / result ------------------------------------------------------------
export function defaultOptions() {
    return {
        targetTriangleCount: 1000,
        weldTolerance: 0,
        boundaryWeight: 500,
        lockBoundaries: false,
        creaseAngle: 35,
        creaseWeight: 25,
        seamWeight: 40,
        extremesWeight: 6,
        extremesPower: 3,
        lockTopVertices: 0,
        lockBoundsExtremes: false,
        useVertexColorImportance: false,
        vertexColorChannel: 3,
        importanceMax: 10,
        preventFlips: true,
        neverCollapseLocked: true,
        preserveTopology: true,
        preserveBoundaryLoops: true,
        recalculateNormals: false,
        progress: null,          // (collapses, sourceTris, aliveTris, target) => void
        cancelRequested: null    // () => bool
    };
}

// --- simplifier ------------------------------------------------------------------
const SILHOUETTE_DIRS = [
    v3(1, 0, 0), v3(-1, 0, 0), v3(0, 1, 0), v3(0, -1, 0), v3(0, 0, 1), v3(0, 0, -1),
    v3(1, 1, 1), v3(1, 1, -1), v3(1, -1, 1), v3(1, -1, -1),
    v3(-1, 1, 1), v3(-1, 1, -1), v3(-1, -1, 1), v3(-1, -1, -1)
];

export function simplify(source, opt) {
    const t0 = performance.now();
    const res = {
        mesh: null, sourceTriangles: 0, resultTriangles: 0,
        sourceVertices: 0, resultVertices: 0, collapses: 0,
        maxError: 0, rmsError: 0,
        sourceBoundaryComponents: 0, resultBoundaryComponents: 0,
        reachedTarget: false, cancelled: false, releaseStage: 0,
        elapsedMs: 0, warning: null
    };
    if (!source) { res.warning = 'Null mesh'; return res; }

    const g = MeshGraph.build(source, opt.weldTolerance || 0);
    res.sourceTriangles = g.aliveTris;
    res.sourceVertices = g.pos.length;
    res.sourceBoundaryComponents = g.countBoundaryComponents();

    const vertEdges = buildVertexEdges(g);
    applyVertexWeightsAndLocks(g, source, opt);

    for (const edge of g.edges.values()) {
        if (edge.removed) continue;
        edge.feature = edge.boundary || edge.seam || edge.tris.length > 2 ||
            edge.creaseAngle >= opt.creaseAngle ||
            (edge.tris.length === 2 && g.triSub[edge.tris[0]] !== g.triSub[edge.tris[1]]);
        if (edge.tris.length > 2)
            g.locked[edge.a] = g.locked[edge.b] = 1;
    }

    const silhouette = markSilhouette(g);
    for (let i = 0; i < silhouette.length; i++)
        if (silhouette[i]) g.locked[i] = 1;

    const quadrics = buildQuadrics(g, opt);
    const heap = new MinHeap();
    for (const e of g.edges.values()) {
        if (e.removed) continue;
        const r = edgeCost(g, quadrics, vertEdges, e, opt, silhouette, 0);
        if (r.cost !== Infinity) heap.push(r.cost, e, e.version);
    }
    const target = Math.max(0, opt.targetTriangleCount | 0);

    // Interior-release ladder (see QEMSimplifier.cs for the full contract):
    //   0 - full preservation, 1 - interior chain corners, 2 - free interior,
    //   3 - interior boundary loops may dissolve. Silhouette locks and all
    //   integrity checks are never relaxed.
    const depth = target / Math.max(1, g.aliveTris);
    const maxStage = depth >= 0.75 ? 0 : depth >= 0.5 ? 1 : depth >= 0.25 ? 2 : 3;
    let stage = 0;
    let collapses = 0, iteration = 0;
    let errSum = 0, errMax = 0;
    for (;;) {
        while (heap.count > 0 && g.aliveTris > target) {
            const h = heap.pop();
            const e = h.edge;
            if (e.removed || h.version !== e.version) continue;
            if ((iteration++ & 511) === 0) {
                if (opt.progress) opt.progress(collapses, res.sourceTriangles, g.aliveTris, target);
                if (opt.cancelRequested && opt.cancelRequested()) {
                    res.cancelled = true;
                    break;
                }
            }
            const r = edgeCost(g, quadrics, vertEdges, e, opt, silhouette, stage);
            if (r.cost === Infinity) continue;
            const p = r.p;
            const limit = MAX_COLLAPSE_DISTANCE * Math.max(g.localScale[e.a], g.localScale[e.b]);
            if (len3(sub3(g.pos[e.a], g.pos[e.b])) > limit) continue;
            if (opt.preventFlips && wouldFlip(g, e, p)) continue;
            if (opt.preserveTopology && !passesLinkCondition(g, vertEdges, e)) continue;
            if (opt.preserveBoundaryLoops && violatesBoundaryTopology(g, vertEdges, e, silhouette, stage)) continue;
            commitCollapse(g, quadrics, vertEdges, e, p);
            collapses++;
            const errorSquared = Math.max(0, r.cost);
            errSum += errorSquared;
            errMax = Math.max(errMax, Math.sqrt(errorSquared));
            const incident = vertEdges[e.a];
            for (let i = incident.length - 1; i >= 0; i--) {
                const next = incident[i];
                if (next.removed) { incident.splice(i, 1); continue; }
                next.version++;
                const nc = edgeCost(g, quadrics, vertEdges, next, opt, silhouette, stage);
                if (nc.cost !== Infinity) heap.push(nc.cost, next, next.version);
            }
        }

        if (res.cancelled || g.aliveTris <= target || stage >= maxStage) break;
        stage++;
        for (const e of g.edges.values()) {
            if (e.removed) continue;
            e.version++;
            const r = edgeCost(g, quadrics, vertEdges, e, opt, silhouette, stage);
            if (r.cost !== Infinity) heap.push(r.cost, e, e.version);
        }
    }
    res.releaseStage = stage;

    if (!res.cancelled && g.aliveTris > target)
        res.warning = (res.warning ? res.warning + ' ' : '') +
            'Stopped above the triangle target to preserve the exterior bounds and feature topology.';

    res.collapses = collapses;
    res.maxError = errMax;
    res.rmsError = collapses > 0 ? Math.sqrt(errSum / collapses) : 0;
    res.resultTriangles = g.aliveTris;
    res.reachedTarget = g.aliveTris <= target;
    res.resultBoundaryComponents = g.countBoundaryComponents();

    const rebuilt = rebuildMesh(g, source, opt);
    res.mesh = rebuilt.mesh;
    res.resultVertices = rebuilt.vertCount;
    res.elapsedMs = performance.now() - t0;
    return res;
}

function buildVertexEdges(g) {
    const ve = new Array(g.pos.length);
    for (let i = 0; i < ve.length; i++) ve[i] = [];
    for (const e of g.edges.values()) {
        if (e.removed) continue;
        ve[e.a].push(e);
        ve[e.b].push(e);
    }
    return ve;
}

function applyVertexWeightsAndLocks(g, source, opt) {
    const n = g.pos.length;

    if (opt.extremesWeight > 0) {
        const min = v3(Infinity, Infinity, Infinity), max = v3(-Infinity, -Infinity, -Infinity);
        const srcCount = source.positions.length / 3;
        for (let i = 0; i < srcCount; i++) {
            const x = source.positions[i * 3], y = source.positions[i * 3 + 1], z = source.positions[i * 3 + 2];
            if (x < min.x) min.x = x; if (x > max.x) max.x = x;
            if (y < min.y) min.y = y; if (y > max.y) max.y = y;
            if (z < min.z) min.z = z; if (z > max.z) max.z = z;
        }
        const c = mid3(min, max);
        let maxD = 0.0001;
        const dist = new Float64Array(n);
        for (let i = 0; i < n; i++) {
            dist[i] = len3(sub3(g.pos[i], c));
            if (dist[i] > maxD) maxD = dist[i];
        }
        for (let i = 0; i < n; i++) {
            const t = Math.pow(dist[i] / maxD, opt.extremesPower);
            g.weight[i] *= lerp(1, opt.extremesWeight, t);
        }
    }

    // Vertex color importance mask (only when the input carries colors).
    if (opt.useVertexColorImportance && source.colors &&
        source.colors.length === source.positions.length / 3 * 4) {
        const cols = source.colors;
        const val = new Float64Array(n);
        for (const w of g.wedges) {
            if (w.srcVertex < 0 || w.srcVertex * 4 + 3 >= cols.length) continue;
            const o = w.srcVertex * 4;
            const f = opt.vertexColorChannel === 0 ? cols[o] :
                      opt.vertexColorChannel === 1 ? cols[o + 1] :
                      opt.vertexColorChannel === 2 ? cols[o + 2] : cols[o + 3];
            const fn = f / 255;
            if (fn > val[w.vertex]) val[w.vertex] = fn;
        }
        for (let i = 0; i < n; i++)
            g.weight[i] *= lerp(1, opt.importanceMax, val[i]);
    }

    if (opt.lockBoundaries) {
        for (const e of g.edges.values())
            if (e.boundary) { g.locked[e.a] = 1; g.locked[e.b] = 1; }
    }

    if (opt.lockTopVertices > 0) {
        const order = new Int32Array(n);
        for (let i = 0; i < n; i++) order[i] = i;
        const sorted = Array.from(order).sort((x, y) => g.pos[y].y - g.pos[x].y);
        const k = Math.min(opt.lockTopVertices, n);
        for (let i = 0; i < k; i++) g.locked[sorted[i]] = 1;
    }

    if (opt.lockBoundsExtremes) {
        let minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
        for (let i = 1; i < n; i++) {
            const p = g.pos[i];
            if (p.x < g.pos[minX].x) minX = i; if (p.x > g.pos[maxX].x) maxX = i;
            if (p.y < g.pos[minY].y) minY = i; if (p.y > g.pos[maxY].y) maxY = i;
            if (p.z < g.pos[minZ].z) minZ = i; if (p.z > g.pos[maxZ].z) maxZ = i;
        }
        g.locked[minX] = g.locked[maxX] = g.locked[minY] =
        g.locked[maxY] = g.locked[minZ] = g.locked[maxZ] = 1;
    }
}

function cross2D(o, a, b) {
    return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
}

// A vertex on the 2D convex hull of the mesh projected along any viewing
// direction contributes to the visible outline. Hull corners get locked;
// collinear hull-edge points are redundant and stay free to collapse.
function markSilhouette(g) {
    const n = g.pos.length;
    const silhouette = new Uint8Array(n);
    const pts = new Array(n);
    for (let i = 0; i < n; i++) pts[i] = { x: 0, y: 0, index: i };
    for (const raw of SILHOUETTE_DIRS) {
        const d = norm3(raw);
        const u = norm3(cross3(d, Math.abs(d.y) < 0.9 ? V3_UP : V3_RIGHT));
        const v = cross3(d, u);
        for (let i = 0; i < n; i++) {
            const p = g.pos[i];
            pts[i].x = dot3(p, u);
            pts[i].y = dot3(p, v);
            pts[i].index = i;
        }
        pts.sort((a, b) => a.x !== b.x ? a.x - b.x : a.y - b.y);

        // Andrew monotone chain - drops collinear hull-edge points.
        const hull = [];
        for (let i = 0; i < n; i++) {
            while (hull.length >= 2 &&
                cross2D(pts[hull[hull.length - 2]], pts[hull[hull.length - 1]], pts[i]) <= 0)
                hull.pop();
            hull.push(i);
        }
        const lower = hull.length;
        for (let i = n - 2; i >= 0; i--) {
            while (hull.length > lower &&
                cross2D(pts[hull[hull.length - 2]], pts[hull[hull.length - 1]], pts[i]) <= 0)
                hull.pop();
            hull.push(i);
        }
        for (let i = 0; i < hull.length; i++) silhouette[pts[hull[i]].index] = 1;
    }
    return silhouette;
}

function buildQuadrics(g, opt) {
    const n = g.pos.length;
    const quadrics = new Array(n);
    for (let i = 0; i < n; i++) quadrics[i] = new Quadric();

    const relSigma = 0.05;
    for (let t = 0; t < g.triSub.length; t++) {
        if (!g.triAlive[t]) continue;
        const v0 = g.cornerVert[g.triCorners[t * 3]];
        const v1 = g.cornerVert[g.triCorners[t * 3 + 1]];
        const v2 = g.cornerVert[g.triCorners[t * 3 + 2]];
        const q = Quadric.fromProbabilisticTriangle(g.pos[v0], g.pos[v1], g.pos[v2], relSigma);
        quadrics[v0].add(q);
        quadrics[v1].add(q);
        quadrics[v2].add(q);
    }

    const creaseDeg = opt.creaseAngle;
    for (const e of g.edges.values()) {
        if (e.removed || e.tris.length === 0) continue;

        if (e.tris.length !== 2 && opt.boundaryWeight > 0) {
            // Boundary edges (openings) and non-manifold edges both need
            // hard protection.
            addEdgeConstraintPlane(g, quadrics, e, e.tris[0], opt.boundaryWeight);
        } else if (e.tris.length === 2) {
            if (e.creaseAngle > creaseDeg && opt.creaseWeight > 0) {
                const w = opt.creaseWeight * (e.creaseAngle / 180);
                addEdgeConstraintPlane(g, quadrics, e, e.tris[0], w);
                addEdgeConstraintPlane(g, quadrics, e, e.tris[1], w);
            }
            if (e.seam && opt.seamWeight > 0) {
                addEdgeConstraintPlane(g, quadrics, e, e.tris[0], opt.seamWeight);
                addEdgeConstraintPlane(g, quadrics, e, e.tris[1], opt.seamWeight);
            }
        }
    }

    for (let i = 0; i < n; i++)
        if (g.weight[i] !== 1) quadrics[i].scale(g.weight[i]);

    return quadrics;
}

function addEdgeConstraintPlane(g, quadrics, e, tri, weight) {
    const pa = g.pos[e.a], pb = g.pos[e.b];
    let edgeDir = sub3(pb, pa);
    const len = len3(edgeDir);
    if (len < 1e-12) return;
    edgeDir = scale3(edgeDir, 1 / len);

    const c0 = g.triCorners[tri * 3], c1 = g.triCorners[tri * 3 + 1], c2 = g.triCorners[tri * 3 + 2];
    const p0 = g.pos[g.cornerVert[c0]], p1 = g.pos[g.cornerVert[c1]], p2 = g.pos[g.cornerVert[c2]];
    let tn = cross3(sub3(p1, p0), sub3(p2, p0));
    const m = len3(tn);
    if (m < 1e-12) return;
    tn = scale3(tn, 1 / m);
    const area = m * 0.5;

    let cn = cross3(edgeDir, tn);
    if (sqrLen3(cn) < 1e-12) return;
    cn = norm3(cn);
    const d = -dot3(cn, pa);
    const q = Quadric.fromPlane(cn.x, cn.y, cn.z, d, weight * area);
    quadrics[e.a].add(q);
    quadrics[e.b].add(q);
}

// --- collapse --------------------------------------------------------------------
function edgeCost(g, quadrics, vertEdges, e, opt, silhouette, stage) {
    const la = g.locked[e.a], lb = g.locked[e.b];
    if (la && lb) return { cost: Infinity, p: null };
    const q = new Quadric();
    q.add(quadrics[e.a]);
    q.add(quadrics[e.b]);
    const pa = g.pos[e.a], pb = g.pos[e.b];
    let chosen = null;
    let cost = Infinity;
    const consider = (p) => {
        if (!preservesFeatureVertex(g, vertEdges, e.a, e.b, p, silhouette, stage) ||
            !preservesFeatureVertex(g, vertEdges, e.b, e.a, p, silhouette, stage) ||
            !canMoveAttributes(g, e, p)) return;
        const c = q.eval(p);
        if (Number.isNaN(c) || c === Infinity || c === -Infinity) return;
        if (c < cost) { cost = c; chosen = p; }
    };
    if (!lb) consider(pa);
    if (!la) consider(pb);
    if (!la && !lb && !e.phantom) consider(mid3(pa, pb));
    return { cost, p: chosen };
}

// A vertex on a feature curve may only move along the chain itself.
// Interior release relaxes this for non-silhouette verts (see C# comments).
function preservesFeatureVertex(g, vertEdges, vertex, other, p, silhouette, stage) {
    if (eq3(p, g.pos[vertex])) return true;
    const shell = !!silhouette[vertex];
    if (!shell && stage >= 2) return true;
    let count = 0;
    let first = V3_ZERO, second = V3_ZERO;
    let alongFeature = false;
    for (const edge of vertEdges[vertex]) {
        if (edge.removed || !edge.feature) continue;
        const neighbor = edge.a === vertex ? edge.b : edge.a;
        if (neighbor === other) alongFeature = true;
        if (++count === 1) first = sub3(g.pos[neighbor], g.pos[vertex]);
        else if (count === 2) second = sub3(g.pos[neighbor], g.pos[vertex]);
    }
    if (count === 0) return true;
    if (!alongFeature || !eq3(p, g.pos[other])) return false;
    if (!shell && stage >= 1) return true;
    if (count !== 2) return false; // shell corner/junction stays
    const product = sqrLen3(first) * sqrLen3(second);
    return product > 0 && dot3(first, second) < 0 &&
        sqrLen3(cross3(first, second)) <= product * 1e-10;
}

// --- attribute transport ----------------------------------------------------------
function usedWedges(g, vertex) {
    const used = new Set();
    for (const t of g.vertTris[vertex]) {
        if (!g.triAlive[t]) continue;
        for (let k = 0; k < 3; k++) {
            const c = g.triCorners[t * 3 + k];
            if (g.cornerVert[c] === vertex)
                used.add(g.resolveWedge(g.cornerWedge[c]));
        }
    }
    return used;
}

function partnerWedge(g, e, vertex, wedge) {
    let partner = -1;
    const other = vertex === e.a ? e.b : e.a;
    for (const t of e.tris) {
        if (!g.triAlive[t]) continue;
        let hasWedge = false;
        let candidate = -1;
        for (let k = 0; k < 3; k++) {
            const c = g.triCorners[t * 3 + k];
            const w = g.resolveWedge(g.cornerWedge[c]);
            if (g.cornerVert[c] === vertex && w === wedge) hasWedge = true;
            if (g.cornerVert[c] === other) candidate = w;
        }
        if (!hasWedge || candidate < 0) continue;
        if (partner >= 0 && !MeshGraph.attributesEqual(g.wedges[partner], g.wedges[candidate]))
            return -1;
        partner = candidate;
    }
    return partner;
}

function canMoveAttributes(g, e, p) {
    for (let side = 0; side < 2; side++) {
        const v = side === 0 ? e.a : e.b;
        if (eq3(p, g.pos[v])) continue;
        for (const wid of usedWedges(g, v)) {
            const partner = partnerWedge(g, e, v, wid);
            if (partner < 0) return false;
            const a = g.wedges[wid], b = g.wedges[partner];
            if (!eqBW(a.boneWeight, b.boneWeight) || a.tangent.w !== b.tangent.w) return false;
        }
    }
    return true;
}

function moveAttributes(g, e, p) {
    const edge = sub3(g.pos[e.b], g.pos[e.a]);
    const e2 = sqrLen3(edge);
    const t = e2 > 0 ? clamp01(dot3(sub3(p, g.pos[e.a]), edge) / e2) : 0;
    const changes = [];
    for (let side = 0; side < 2; side++) {
        const v = side === 0 ? e.a : e.b;
        if (eq3(p, g.pos[v])) continue;
        for (const wid of usedWedges(g, v)) {
            const partner = partnerWedge(g, e, v, wid);
            // Eligibility checked before committing; a missing pair is a programming error.
            if (partner < 0) throw new Error('Missing edge attribute chart');
            const a = g.wedges[side === 0 ? wid : partner];
            const b = g.wedges[side === 0 ? partner : wid];
            const tangent = lerp4(a.tangent, b.tangent, t);
            const dir = norm3(v3(tangent.x, tangent.y, tangent.z));
            changes.push([wid, {
                normal: norm3(lerp3(a.normal, b.normal, t)),
                tangent: { x: dir.x, y: dir.y, z: dir.z, w: a.tangent.w },
                uv0: lerp2(a.uv0, b.uv0, t), uv1: lerp2(a.uv1, b.uv1, t),
                uv2: lerp2(a.uv2, b.uv2, t), uv3: lerp2(a.uv3, b.uv3, t),
                color: lerpColor(a.color, b.color, t), boneWeight: a.boneWeight
            }]);
        }
    }
    for (const [wid, s] of changes) {
        const d = g.wedges[wid];
        d.normal = s.normal; d.tangent = s.tangent; d.uv0 = s.uv0; d.uv1 = s.uv1;
        d.uv2 = s.uv2; d.uv3 = s.uv3; d.color = s.color; d.boneWeight = s.boneWeight;
    }
}

function pos(g, corner, ovA, ovB, p) {
    const v = g.cornerVert[corner];
    return (v === ovA || v === ovB) ? p : g.pos[v];
}

function crossWithOverride(g, tri, ovA, ovB, p) {
    const c0 = g.triCorners[tri * 3], c1 = g.triCorners[tri * 3 + 1], c2 = g.triCorners[tri * 3 + 2];
    const r0 = pos(g, c0, ovA, ovB, p);
    const r1 = pos(g, c1, ovA, ovB, p);
    const r2 = pos(g, c2, ovA, ovB, p);
    return cross3(sub3(r1, r0), sub3(r2, r0));
}

function triHasVert(g, tri, v) {
    for (let k = 0; k < 3; k++)
        if (g.cornerVert[g.triCorners[tri * 3 + k]] === v) return true;
    return false;
}

// Normalized triangle quality: 4*sqrt(3)*A / sum(edge^2). 1 = equilateral.
function shapeFactor(g, tri, ovA, ovB, p, useNew) {
    const c0 = g.triCorners[tri * 3], c1 = g.triCorners[tri * 3 + 1], c2 = g.triCorners[tri * 3 + 2];
    const r0 = useNew ? pos(g, c0, ovA, ovB, p) : g.pos[g.cornerVert[c0]];
    const r1 = useNew ? pos(g, c1, ovA, ovB, p) : g.pos[g.cornerVert[c1]];
    const r2 = useNew ? pos(g, c2, ovA, ovB, p) : g.pos[g.cornerVert[c2]];
    const e1 = sqrLen3(sub3(r1, r0));
    const e2v = sqrLen3(sub3(r2, r1));
    const e3 = sqrLen3(sub3(r0, r2));
    const sum = e1 + e2v + e3;
    if (sum < 1e-12) return 0;
    return 2 * 1.7320508 * len3(cross3(sub3(r1, r0), sub3(r2, r0))) / sum;
}

function wouldFlip(g, e, p) {
    for (let side = 0; side < 2; side++) {
        const list = g.vertTris[side === 0 ? e.a : e.b];
        for (let i = 0; i < list.length; i++) {
            const t = list[i];
            if (!g.triAlive[t]) continue;
            if (triHasVert(g, t, e.a) && triHasVert(g, t, e.b)) continue; // dies anyway
            const c0 = g.triCross(t);
            const c1 = crossWithOverride(g, t, e.a, e.b, p);
            const m0 = len3(c0), m1 = len3(c1);
            if (m1 < 1e-8 || m1 < m0 * 1e-3) return true;
            if (m1 > m0 * 4) return true;
            if (dot3(c0, c1) < 0) return true;
            const s0 = shapeFactor(g, t, e.a, e.b, p, false);
            const s1 = shapeFactor(g, t, e.a, e.b, p, true);
            if (s1 < 0.02 && s1 < s0 * 0.5) return true;
        }
    }
    return false;
}

// Link condition (Dey et al. / Hoppe): collapsing (a,b) keeps the mesh
// manifold iff every vertex adjacent to BOTH is an apex of a face on the edge.
function passesLinkCondition(g, vertEdges, e) {
    const a = e.a, b = e.b;

    const apex = new Set();
    const aTris = g.vertTris[a];
    for (let i = 0; i < aTris.length; i++) {
        const t = aTris[i];
        if (!g.triAlive[t] || !triHasVert(g, t, b)) continue;
        for (let k = 0; k < 3; k++) {
            const v = g.cornerVert[g.triCorners[t * 3 + k]];
            if (v !== a && v !== b) apex.add(v);
        }
    }

    const bEdges = vertEdges[b];
    for (let i = 0; i < bEdges.length; i++) {
        const be = bEdges[i];
        if (be.removed) continue;
        const x = be.a === b ? be.b : be.a;
        if (x === a || apex.has(x)) continue;
        if (isNeighbor(vertEdges[a], x)) return false;
    }

    // Reject collapses leaving two surviving faces on the same vertex pair.
    const faces = new Set();
    const visited = new Set();
    for (let side = 0; side < 2; side++) {
        for (const t of g.vertTris[side === 0 ? a : b]) {
            if (!g.triAlive[t] || visited.has(t)) continue;
            visited.add(t);
            if (triHasVert(g, t, a) && triHasVert(g, t, b)) continue;
            let x = -1, y = -1;
            for (let k = 0; k < 3; k++) {
                const v = g.cornerVert[g.triCorners[t * 3 + k]];
                if (v === a || v === b) continue;
                if (x < 0) x = v; else y = v;
            }
            if (x >= 0 && y >= 0) {
                const key = edgeKey(x, y);
                if (faces.has(key)) return false;
                faces.add(key);
            }
        }
    }
    return true;
}

function isNeighbor(edges, v) {
    for (let i = 0; i < edges.length; i++) {
        const e = edges[i];
        if (e.removed) continue;
        if (e.a === v || e.b === v) return true;
    }
    return false;
}

// Boundary-loop topology guard: no loop merges, splits or annihilation.
function violatesBoundaryTopology(g, vertEdges, e, silhouette, stage) {
    if (!touchesBoundary(vertEdges[e.a]) || !touchesBoundary(vertEdges[e.b]))
        return false;

    const seenVert = new Set();
    const seenEdge = new Set();
    const stack = [e.a];
    seenVert.add(e.a);
    let reachedB = false, onShell = !!(silhouette[e.a] || silhouette[e.b]);
    while (stack.length > 0) {
        const v = stack.pop();
        if (silhouette[v]) onShell = true;
        const list = vertEdges[v];
        for (let i = 0; i < list.length; i++) {
            const be = list[i];
            if (be.removed || !be.boundary) continue;
            seenEdge.add(be);
            const o = be.a === v ? be.b : be.a;
            if (o === e.b) reachedB = true;
            if (!seenVert.has(o)) { seenVert.add(o); stack.push(o); }
        }
    }

    // Deepest release: a fully interior boundary component may shrink away.
    if (stage >= 3 && !onShell) return false;

    if (!reachedB) return true;                    // loop merge
    if (e.boundary) return seenEdge.size <= 3;     // loop annihilation
    return true;                                   // loop split
}

function touchesBoundary(edges) {
    for (let i = 0; i < edges.length; i++)
        if (!edges[i].removed && edges[i].boundary) return true;
    return false;
}

function commitCollapse(g, quadrics, vertEdges, e, p) {
    const a = e.a, b = e.b;
    moveAttributes(g, e, p);
    const oldA = g.pos[a];
    g.pos[a] = v3(p.x, p.y, p.z);

    // A survivor that moved leaves the source edge chart: mark its incident
    // edges phantom (b's edges are handled during repointing below).
    if (sqrLen3(sub3(p, oldA)) > 1e-12) {
        const aList = vertEdges[a];
        for (let i = 0; i < aList.length; i++)
            if (aList[i] !== e) aList[i].phantom = true;
    }

    // 1. Migrate wedges of b to a (merge attribute-identical ones).
    const bWedges = g.vertWedges[b];
    for (let i = 0; i < bWedges.length; i++) {
        const wid = bWedges[i];
        const w = g.wedges[wid];
        if (w.removed) continue;
        const match = findMatchingWedge(g, a, w);
        if (match >= 0) {
            w.alias = match;
            w.removed = true;
        } else {
            w.vertex = a;
            g.vertWedges[a].push(wid);
        }
    }
    bWedges.length = 0;

    // 2. Kill triangles that contain both endpoints, repoint the rest.
    const aTris = [];
    for (const t of g.vertTris[b]) {
        if (!g.triAlive[t]) continue;
        if (triHasVert(g, t, a)) {
            g.removeTri(t);
            continue;
        }
        for (let k = 0; k < 3; k++) {
            const c = g.triCorners[t * 3 + k];
            if (g.cornerVert[c] === b) g.cornerVert[c] = a;
        }
        aTris.push(t);
    }
    for (const t of g.vertTris[a])
        if (g.triAlive[t]) aTris.push(t);
    g.vertTris[a] = aTris;
    g.vertTris[b].length = 0;

    // 3. Repoint edges of b; merge duplicates into existing (a,x) edges.
    const atB = sqrLen3(sub3(p, g.pos[b])) < 1e-12;
    const bEdges = vertEdges[b].slice();
    vertEdges[b].length = 0;
    for (const e2 of bEdges) {
        if (e2.removed) continue;
        const other = e2.a === b ? e2.b : e2.a;
        g.edges.delete(edgeKey(e2.a, e2.b));
        if (other === a) {
            e2.removed = true;
            const idx = vertEdges[a].indexOf(e2);
            if (idx >= 0) vertEdges[a].splice(idx, 1);
            continue;
        }
        const existing = g.getEdge(a, other);
        if (existing && !existing.removed) {
            for (const t of e2.tris)
                if (g.triAlive[t] && !existing.tris.includes(t)) existing.tris.push(t);
            existing.seam = existing.seam || e2.seam;
            existing.feature = existing.feature || e2.feature;
            e2.removed = true;
            const oe = vertEdges[other];
            if (!oe.includes(existing)) oe.push(existing);
        } else {
            e2.phantom = e2.phantom || !atB;
            if (a < other) { e2.a = a; e2.b = other; } else { e2.a = other; e2.b = a; }
            g.edges.set(edgeKey(e2.a, e2.b), e2);
            vertEdges[a].push(e2);
        }
    }
    for (const incident of vertEdges[a]) {
        if (incident.removed || incident === e) continue;
        refreshEdge(g, incident);
    }

    // 4. Accumulate quadric + lock state.
    quadrics[a].add(quadrics[b]);
    if (g.locked[b]) g.locked[a] = 1;

    e.removed = true;
    g.edges.delete(edgeKey(a, b));
}

function findMatchingWedge(g, v, candidate) {
    const owned = g.vertWedges[v];
    for (let i = 0; i < owned.length; i++) {
        const w = g.wedges[owned[i]];
        if (w.removed) continue;
        if (MeshGraph.attributesEqual(w, candidate))
            return owned[i];
    }
    return -1;
}

function refreshEdge(g, e) {
    for (let i = e.tris.length - 1; i >= 0; i--)
        if (!g.triAlive[e.tris[i]]) e.tris.splice(i, 1);
    if (e.tris.length === 0) { e.removed = true; return; }
    e.boundary = e.tris.length === 1;
    e.creaseAngle = 0;
    if (e.tris.length === 2)
        e.creaseAngle = angle3(g.triNormal(e.tris[0]), g.triNormal(e.tris[1]));
}

// --- output ------------------------------------------------------------------------
function rebuildMesh(g, source, opt) {
    const srcCount = source.positions.length / 3;
    const hasN = source.normals != null && source.normals.length === source.positions.length;
    const hasT = source.tangents != null && source.tangents.length === srcCount * 4;
    const hasC = source.colors != null && source.colors.length === srcCount * 4;
    const hasU0 = source.uvs != null && source.uvs.length === srcCount * 2;
    const hasB = source.boneWeights != null && source.boneWeights.length === srcCount * 8;

    const wedgeToOut = new Map();
    const outPos = [];
    const outN = [];
    const outT = [];
    const outC = [];
    const outU0 = [];
    const outB = [];

    const subTris = source.submeshes.map(() => []);

    for (let t = 0; t < g.triSub.length; t++) {
        if (!g.triAlive[t]) continue;
        const sub = g.triSub[t];
        for (let k = 0; k < 3; k++) {
            const c = g.triCorners[t * 3 + k];
            const w = g.resolveWedge(g.cornerWedge[c]);
            g.cornerWedge[c] = w;
            let idx = wedgeToOut.get(w);
            if (idx === undefined) {
                idx = outPos.length / 3;
                wedgeToOut.set(w, idx);
                const wd = g.wedges[w];
                const wp = g.pos[wd.vertex];
                outPos.push(wp.x, wp.y, wp.z);
                const nn = norm3(wd.normal);
                outN.push(nn.x, nn.y, nn.z);
                outT.push(wd.tangent.x, wd.tangent.y, wd.tangent.z, wd.tangent.w);
                outC.push(wd.color.r, wd.color.g, wd.color.b, wd.color.a);
                outU0.push(wd.uv0.x, wd.uv0.y);
                const bw = wd.boneWeight;
                outB.push(bw.w0, bw.w1, bw.w2, bw.w3, bw.i0, bw.i1, bw.i2, bw.i3);
            }
            subTris[sub].push(idx);
        }
    }

    const vertCount = outPos.length / 3;
    const mesh = {
        name: (source.name || 'mesh') + '_simplified',
        positions: new Float32Array(outPos),
        normals: null,
        uvs: hasU0 ? new Float32Array(outU0) : null,
        tangents: null,
        colors: hasC ? new Uint8Array(outC) : null,
        boneWeights: hasB ? new Float32Array(outB) : null,
        submeshes: subTris.map(a => new Uint32Array(a)),
        materials: source.materials || null
    };
    if (hasN) mesh.normals = new Float32Array(outN);
    if (hasT && !opt.recalculateNormals) mesh.tangents = new Float32Array(outT);
    if (opt.recalculateNormals) mesh.normals = recalcNormals(mesh);
    return { mesh, vertCount };
}

function recalcNormals(mesh) {
    const n = mesh.positions.length / 3;
    const acc = new Float32Array(n * 3);
    for (const idx of mesh.submeshes) {
        for (let i = 0; i + 2 < idx.length; i += 3) {
            const a = idx[i], b = idx[i + 1], c = idx[i + 2];
            const ax = mesh.positions[a * 3], ay = mesh.positions[a * 3 + 1], az = mesh.positions[a * 3 + 2];
            const ux = mesh.positions[b * 3] - ax, uy = mesh.positions[b * 3 + 1] - ay, uz = mesh.positions[b * 3 + 2] - az;
            const vx = mesh.positions[c * 3] - ax, vy = mesh.positions[c * 3 + 1] - ay, vz = mesh.positions[c * 3 + 2] - az;
            const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            for (const v of [a, b, c]) { acc[v * 3] += nx; acc[v * 3 + 1] += ny; acc[v * 3 + 2] += nz; }
        }
    }
    for (let i = 0; i < n; i++) {
        const m = Math.hypot(acc[i * 3], acc[i * 3 + 1], acc[i * 3 + 2]);
        if (m > 1e-20) { acc[i * 3] /= m; acc[i * 3 + 1] /= m; acc[i * 3 + 2] /= m; }
    }
    return acc;
}
