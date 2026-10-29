// Software mesh renderer for marketing cards: perspective z-buffered
// rasterizer with Lambert + rim lighting and optional per-submesh textures.
// Input is the flat mesh format shared by the engine and glbmini.js:
//   { positions, normals, uvs, submeshes[], materials[] }
const PALETTE = {
    ship_pinnace_aft: [122, 84, 52],
    ship_pinnace_rigging: [74, 58, 46],
    ship_pinnace_details: [150, 112, 68],
    ship_pinnace_hull: [96, 62, 40],
    ship_pinnace_deck: [168, 128, 84],
    ship_pinnace_interior: [70, 48, 34],
    ship_pinnace_sails: [214, 200, 176],
};
const FALLBACK = [150, 140, 128];

function norm(v) { const l = Math.hypot(v[0], v[1], v[2]) || 1; return [v[0] / l, v[1] / l, v[2] / l]; }
function cross(a, b) { return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]; }
function dot(a, b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
function sub(a, b) { return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]; }

export function meshBounds(mesh) {
    let mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
    const p = mesh.positions;
    for (let i = 0; i < p.length; i += 3)
        for (let k = 0; k < 3; k++) {
            if (p[i + k] < mn[k]) mn[k] = p[i + k];
            if (p[i + k] > mx[k]) mx[k] = p[i + k];
        }
    const c = [(mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2];
    let r = 0;
    for (let i = 0; i < p.length; i += 3) {
        const d = Math.hypot(p[i] - c[0], p[i + 1] - c[1], p[i + 2] - c[2]);
        if (d > r) r = d;
    }
    return { mn, mx, c, r };
}

// Render a mesh into an image { w, h, data: RGBA Uint8Array } with alpha.
// opts: yaw, pitch, fov (deg), fit (0..1 fraction of frame), cy (vertical
// center shift), textures: { matName -> {w,h,data} }, palette override,
// zoom (extra scale).
export function renderMesh(img, mesh, opts = {}) {
    const { w, h, data } = img;
    const yaw = opts.yaw ?? -0.7, pitch = opts.pitch ?? 0.15;
    const fov = (opts.fov ?? 26) * Math.PI / 180;
    const zoom = opts.zoom ?? 1;
    const { c, r } = meshBounds(mesh);
    const dist = (r / Math.tan(fov / 2)) / Math.max(0.05, (opts.fit ?? 0.8)) / zoom;
    const cp = Math.cos(pitch), sp = Math.sin(pitch);
    const eye = [c[0] + dist * cp * Math.sin(yaw), c[1] + dist * sp, c[2] + dist * cp * Math.cos(yaw)];
    const fwd = norm(sub(c, eye));
    const right = norm(cross(fwd, [0, 1, 0]));
    const up = cross(right, fwd);
    const fpx = (img.w / 2) / Math.tan(fov / 2);
    const cx = img.w / 2, cy = img.h / 2 + (opts.cy ?? 0) * img.h;

    const zbuf = new Float32Array(w * h).fill(Infinity);
    const P = mesh.positions, N = mesh.normals, UV = mesh.uvs;
    const light = norm(opts.light || [-0.4, 0.75, 0.5]);
    const amb = opts.amb ?? 0.34, diff = opts.diff ?? 0.78, rimAmt = opts.rim ?? 0.22;
    const rimCol = opts.rimColor || [120, 160, 255];

    // Transform a world vertex to screen {x, y, invz, nx, ny, nz}
    const sv = new Float32Array(6);
    const project = (i, out) => {
        const px = P[i] - eye[0], py = P[i + 1] - eye[1], pz = P[i + 2] - eye[2];
        const z = dot([px, py, pz], fwd);
        if (z <= 0.01) return false;
        const iz = 1 / z;
        out[0] = cx + dot([px, py, pz], right) * fpx * iz;
        out[1] = cy - dot([px, py, pz], up) * fpx * iz;
        out[2] = iz;
        if (N) {
            const n = norm([N[i], N[i + 1], N[i + 2]]);
            out[3] = n[0]; out[4] = n[1]; out[5] = n[2];
        } else { out[3] = 0; out[4] = 1; out[5] = 0; }
        return true;
    };

    const va = new Float32Array(6), vb = new Float32Array(6), vc = new Float32Array(6);
    const tex = opts.textures || {};
    const tris = (si) => mesh.submeshes[si];

    for (let si = 0; si < mesh.submeshes.length; si++) {
        const idx = tris(si);
        const name = mesh.materials ? mesh.materials[si] : null;
        const t = name ? tex[name] : null;
        const base = (name && PALETTE[name]) || FALLBACK;
        for (let f = 0; f + 2 < idx.length; f += 3) {
            const i0 = idx[f] * 3, i1 = idx[f + 1] * 3, i2 = idx[f + 2] * 3;
            if (!project(i0, va) || !project(i1, vb) || !project(i2, vc)) continue;

            // Face normal for backface culling of degenerate views (keep
            // double-sided rendering - the kit relies on it).
            let minX = Math.min(va[0], vb[0], vc[0]), maxX = Math.max(va[0], vb[0], vc[0]);
            let minY = Math.min(va[1], vb[1], vc[1]), maxY = Math.max(va[1], vb[1], vc[1]);
            if (maxX < 0 || minX >= w || maxY < 0 || minY >= h) continue;
            let x0 = Math.max(0, Math.floor(minX)), x1 = Math.min(w - 1, Math.ceil(maxX));
            let y0 = Math.max(0, Math.floor(minY)), y1 = Math.min(h - 1, Math.ceil(maxY));

            const d = (va[0] - vc[0]) * (vb[1] - vc[1]) - (vb[0] - vc[0]) * (va[1] - vc[1]);
            if (Math.abs(d) < 1e-9) continue;
            const inv = 1 / d;

            // Vertex uv (divide by z for perspective-correct interpolation)
            const u0 = UV ? UV[idx[f] * 2] * va[2] : 0, u1 = UV ? UV[idx[f + 1] * 2] * vb[2] : 0, u2 = UV ? UV[idx[f + 2] * 2] * vc[2] : 0;
            const v0 = UV ? UV[idx[f] * 2 + 1] * va[2] : 0, v1 = UV ? UV[idx[f + 1] * 2 + 1] * vb[2] : 0, v2 = UV ? UV[idx[f + 2] * 2 + 1] * vc[2] : 0;
            const nA = [va[3] * va[2], va[4] * va[2], va[5] * va[2]];
            const nB = [vb[3] * vb[2], vb[4] * vb[2], vb[5] * vb[2]];
            const nC = [vc[3] * vc[2], vc[4] * vc[2], vc[5] * vc[2]];

            for (let y = y0; y <= y1; y++) {
                for (let x = x0; x <= x1; x++) {
                    const px = x + 0.5, py = y + 0.5;
                    const w0 = ((px - vc[0]) * (vb[1] - vc[1]) - (vb[0] - vc[0]) * (py - vc[1])) * inv;
                    const w1 = ((va[0] - vc[0]) * (py - vc[1]) - (px - vc[0]) * (va[1] - vc[1])) * inv;
                    const w2 = 1 - w0 - w1;
                    if (w0 < -0.001 || w1 < -0.001 || w2 < -0.001) continue;
                    const iz = w0 * va[2] + w1 * vb[2] + w2 * vc[2];
                    const z = 1 / iz;
                    const bi = y * w + x;
                    if (zbuf[bi] <= z) continue;
                    zbuf[bi] = z;

                    let rr, gg, bb;
                    if (t) {
                        const u = (w0 * u0 + w1 * u1 + w2 * u2) * z;
                        const v = (w0 * v0 + w1 * v1 + w2 * v2) * z;
                        let tu = u - Math.floor(u), tv = v - Math.floor(v);
                        const tx = Math.min(t.w - 1, Math.max(0, Math.floor(tu * t.w)));
                        const ty = Math.min(t.h - 1, Math.max(0, Math.floor((1 - tv) * t.h)));
                        const ti = (ty * t.w + tx) * 3;
                        rr = t.data[ti]; gg = t.data[ti + 1]; bb = t.data[ti + 2];
                    } else { rr = base[0]; gg = base[1]; bb = base[2]; }

                    const nx = (w0 * nA[0] + w1 * nB[0] + w2 * nC[0]) * z;
                    const ny = (w0 * nA[1] + w1 * nB[1] + w2 * nC[1]) * z;
                    const nz = (w0 * nA[2] + w1 * nB[2] + w2 * nC[2]) * z;
                    const nn = norm([nx, ny, nz]);
                    const lam = Math.max(0, dot(nn, light));
                    const viewDot = Math.abs(dot(nn, fwd));
                    const rim = Math.pow(1 - viewDot, 2.2) * rimAmt;
                    const s = amb + diff * lam;
                    const o = bi * 4;
                    data[o] = Math.min(255, rr * s + rimCol[0] * rim);
                    data[o + 1] = Math.min(255, gg * s + rimCol[1] * rim);
                    data[o + 2] = Math.min(255, bb * s + rimCol[2] * rim);
                    data[o + 3] = 255;
                }
            }
        }
    }
    return img;
}
