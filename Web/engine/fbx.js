// Minimal FBX reader -> ComplexLODs flat mesh format.
// Binary FBX 7.x (port of the lodharness FbxParse.cs, extended with normal/UV/
// material layers) plus a best-effort ASCII FBX path for simple exports.
// Node transforms are not applied (same as the harness) — kit meshes bake them.

async function inflateZlib(data) {
    const ds = new DecompressionStream('deflate');
    const stream = new Blob([data]).stream().pipeThrough(ds);
    return new Uint8Array(await new Response(stream).arrayBuffer());
}

// ---------------- binary ----------------

class Reader {
    constructor(buf) {
        this.buf = buf;                     // Uint8Array
        this.dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
        this.pos = 27;
        this.version = this.dv.getUint32(23, true);
        this.wide = this.version >= 7500;
    }
    async readProp() {
        const dv = this.dv, pos = this.pos;
        const t = String.fromCharCode(this.buf[pos]);
        this.pos++;
        switch (t) {
            case 'Y': this.pos += 2; return dv.getInt16(pos + 1, true);
            case 'C': this.pos += 1; return this.buf[pos + 1] !== 0;
            case 'I': this.pos += 4; return dv.getInt32(pos + 1, true);
            case 'F': this.pos += 4; return dv.getFloat32(pos + 1, true);
            case 'D': this.pos += 8; return dv.getFloat64(pos + 1, true);
            case 'L': this.pos += 8; return Number(dv.getBigInt64(pos + 1, true));
            case 'S': case 'R': {
                const sl = dv.getInt32(pos + 1, true);
                this.pos += 4 + sl;
                return new TextDecoder().decode(this.buf.subarray(pos + 5, pos + 5 + sl));
            }
            case 'f': case 'd': case 'l': case 'i': case 'b': {
                const len = dv.getInt32(pos + 1, true);
                const enc = dv.getInt32(pos + 5, true);
                const clen = dv.getInt32(pos + 9, true);
                let data = this.buf.subarray(pos + 13, pos + 13 + clen);
                if (enc === 1) data = await inflateZlib(data);
                this.pos = pos + 13 + clen;
                // DataView reads (byteOffset alignment is not guaranteed)
                const dv2 = new DataView(data.buffer, data.byteOffset, data.byteLength);
                switch (t) {
                    case 'd': { const a = new Float64Array(len); for (let i = 0; i < len; i++) a[i] = dv2.getFloat64(i * 8, true); return a; }
                    case 'i': { const a = new Int32Array(len); for (let i = 0; i < len; i++) a[i] = dv2.getInt32(i * 4, true); return a; }
                    case 'l': { const a = new BigInt64Array(len); for (let i = 0; i < len; i++) a[i] = dv2.getBigInt64(i * 8, true); return a; }
                    case 'f': { const a = new Float32Array(len); for (let i = 0; i < len; i++) a[i] = dv2.getFloat32(i * 4, true); return a; }
                    default: return data.slice(0, len); // 'b'
                }
            }
            default: throw new Error(`FBX prop type '${t}' at ${pos}`);
        }
    }
}

export async function parseNodeTree(bytes) {
    const r = new Reader(bytes);
    const root = { name: '__root', props: [], children: [] };

    async function readInto(parent, end) {
        while (r.pos < end) {
            const sz = r.wide ? 8 : 4;
            if (r.pos + sz * 3 + 1 > r.buf.length) return;
            const dv = r.dv;
            const nend = r.wide ? Number(dv.getBigInt64(r.pos, true)) : dv.getUint32(r.pos, true);
            const numProps = r.wide ? Number(dv.getBigInt64(r.pos + sz, true)) : dv.getUint32(r.pos + sz, true);
            r.pos += sz * 3;
            const nameLen = r.buf[r.pos++];
            if (nend === 0) { r.pos += nameLen; return; }  // NULL-record sentinel
            const node = {
                name: nameLen > 0 ? new TextDecoder('ascii').decode(r.buf.subarray(r.pos, r.pos + nameLen)) : '',
                props: [], children: []
            };
            r.pos += nameLen;
            for (let i = 0; i < numProps; i++) node.props.push(await r.readProp());
            await readInto(node, nend);
            r.pos = nend;
            parent.children.push(node);
        }
    }
    await readInto(root, r.buf.length);
    return root;
}

export function find(node, name) {
    if (node.name === name) return node;
    for (const c of node.children) { const r = find(c, name); if (r) return r; }
    return null;
}
export function findAll(node, name, out = []) {
    if (node.name === name) out.push(node);
    for (const c of node.children) findAll(c, name, out);
    return out;
}
function propNode(node, name) {
    for (const c of node.children) if (c.name === name) return c;
    return null;
}
const toNumArr = (a) => a instanceof BigInt64Array ? Array.from(a, Number) : a;

// Reads LayerElement{Normal,UV}-style layer into a per-corner array.
// Returns flat numeric array or null.
function layerPerCorner(geom, layerName, dataName, indexName, pvi, nComps) {
    const layer = propNode(geom, layerName);
    if (!layer) return null;
    let map = null, reftype = null, data = null, idx = null;
    for (const c of layer.children) {
        if (c.name === 'MappingInformationType') map = String(c.props[0]);
        else if (c.name === 'ReferenceInformationType') reftype = String(c.props[0]);
        else if (c.name === dataName) data = c.props[0];
        else if (c.name === indexName) idx = c.props[0];
    }
    if (!data) return null;
    data = toNumArr(data);
    idx = idx && toNumArr(idx);
    const nCorners = pvi.length;
    const out = new Float64Array(nCorners * nComps);
    const refIdx = (key) => reftype === 'IndexToDirect' && idx ? idx[key] : key;
    for (let c = 0; c < nCorners; c++) {
        let key;
        if (map === 'ByPolygonVertex') key = c;
        else if (map === 'ByVertice' || map === 'ByControlPoint') key = Math.abs(pvi[c] < 0 ? -pvi[c] - 1 : pvi[c]);
        else return null; // ByPolygon / ByEdge / AllSame -> not per-corner
        const k = refIdx(key) * nComps;
        if (k + nComps > data.length) return null;
        for (let j = 0; j < nComps; j++) out[c * nComps + j] = data[k + j];
    }
    return out;
}

// Per-polygon material index (submesh grouping), or null.
function layerMaterialPerPoly(geom, nPolys) {
    const layer = propNode(geom, 'LayerElementMaterial');
    if (!layer) return null;
    let map = null, mats = null;
    for (const c of layer.children) {
        if (c.name === 'MappingInformationType') map = String(c.props[0]);
        else if (c.name === 'Materials') mats = toNumArr(c.props[0]);
    }
    if (!mats) return null;
    if (map === 'AllSame') return new Int32Array(nPolys);
    if (map === 'ByPolygon' && mats.length >= nPolys) {
        const out = new Int32Array(nPolys);
        for (let i = 0; i < nPolys; i++) out[i] = mats[i];
        return out;
    }
    return null;
}

// One Geometry node -> { verts, polys, nrm, uv, matPerPoly, matNames }
function geometryToPolygons(geom, matNames) {
    let verts = null, pvi = null;
    for (const c of geom.children) {
        if (c.name === 'Vertices' && c.props.length) verts = toNumArr(c.props[0]);
        if (c.name === 'PolygonVertexIndex' && c.props.length) pvi = toNumArr(c.props[0]);
    }
    if (!verts || !pvi) return null;
    const nrm = layerPerCorner(geom, 'LayerElementNormal', 'Normals', 'NormalsIndex', pvi, 3);
    const uv = layerPerCorner(geom, 'LayerElementUV', 'UV', 'UVIndex', pvi, 2);

    // Polygon split: negative index ends a polygon.
    const polys = [];
    let cur = [];
    for (let c = 0; c < pvi.length; c++) {
        const raw = pvi[c];
        const vi = raw < 0 ? -raw - 1 : raw;
        cur.push({ corner: c, vi, ni: nrm ? c : -1, ui: uv ? c : -1 });
        if (raw < 0) { polys.push(cur); cur = []; }
    }
    if (cur.length) polys.push(cur);

    const matPerPoly = layerMaterialPerPoly(geom, polys.length);
    return { verts, polys, nrm, uv, matPerPoly, matNames: matNames || [] };
}

// ---------------- ASCII ----------------

function asciiBlocks(text, headRe) {
    // yield text of each `{...}` block whose header matches headRe
    const blocks = [];
    let m;
    while ((m = headRe.exec(text)) !== null) {
        const open = text.indexOf('{', m.index);
        if (open < 0) break;
        let depth = 0, i = open;
        for (; i < text.length; i++) {
            if (text[i] === '{') depth++;
            else if (text[i] === '}') { if (--depth === 0) break; }
        }
        blocks.push(text.slice(open + 1, i));
        headRe.lastIndex = i + 1;
    }
    return blocks;
}

function asciiProp(block, name) {
    const m = block.match(new RegExp(name + '\\s*:[^\\n]*', 'm'));
    return m ? m[0] : null;
}

function asciiArray(block, name) {
    const m = block.match(new RegExp(name + '\\s*:\\s*\\*?\\d*\\s*\\{[^}]*?a\\s*:\\s*([^}]*)\\}', 's'))
        || block.match(new RegExp(name + '\\s*:\\s*([\\d.,+\\-eE\\s]+)', 'm'));
    if (!m) return null;
    const vals = m[1].split(',').map(s => parseFloat(s)).filter(v => !Number.isNaN(v));
    return vals;
}

function asciiGeomToPolygons(block) {
    const verts = asciiArray(block, 'Vertices');
    const pvi = asciiArray(block, 'PolygonVertexIndex');
    if (!verts || !pvi) return null;
    const normals = asciiArray(block, 'Normals');
    const uvs = asciiArray(block, 'UV');
    const mats = asciiArray(block, 'Materials');

    const polys = [];
    let cur = [];
    const hasN = normals !== null, hasU = uvs !== null;
    for (let c = 0; c < pvi.length; c++) {
        const raw = pvi[c] | 0;
        const vi = raw < 0 ? -raw - 1 : raw;
        cur.push({ corner: c, vi, ni: hasN ? c : -1, ui: hasU ? c : -1 });
        if (raw < 0) { polys.push(cur); cur = []; }
    }
    if (cur.length) polys.push(cur);
    const nCorners = pvi.length;
    return {
        verts, polys,
        nrm: normals && normals.length >= nCorners * 3 ? normals : null,
        uv: uvs && uvs.length >= nCorners * 2 ? uvs : null,
        matPerPoly: mats && mats.length >= polys.length ? Int32Array.from(mats.map(v => v | 0)) : null
    };
}

// ---------------- mesh assembly ----------------

function assembleMesh(geomResults, name) {
    const positions = [], normals = [], uvs = [];
    let anyN = false, anyU = false;
    const submeshMap = new Map(); // global material index -> index array
    const matList = [];           // global material index -> name
    const matGlobal = new Map();  // name -> global index

    for (const g of geomResults) {
        if (!g) continue;
        const local = new Map(); // combo -> vert index (per geometry)
        const g2g = (g.matNames || []).map(nm => {
            if (!matGlobal.has(nm)) { matGlobal.set(nm, matList.length); matList.push(nm); }
            return matGlobal.get(nm);
        });
        for (let p = 0; p < g.polys.length; p++) {
            const poly = g.polys[p];
            const localMat = g.matPerPoly ? g.matPerPoly[p] : 0;
            const mat = g2g.length ? (g2g[localMat] ?? 0) : (g.matPerPoly ? localMat : 0);
            let arr = submeshMap.get(mat);
            if (!arr) { arr = []; submeshMap.set(mat, arr); }
            const cornerIdx = (cc) => {
                const key = cc.vi + '/' + cc.ni + '/' + cc.ui;
                let idx = local.get(key);
                if (idx === undefined) {
                    idx = positions.length / 3;
                    positions.push(g.verts[cc.vi * 3], g.verts[cc.vi * 3 + 1], g.verts[cc.vi * 3 + 2]);
                    if (g.nrm) { normals.push(g.nrm[cc.ni * 3], g.nrm[cc.ni * 3 + 1], g.nrm[cc.ni * 3 + 2]); anyN = true; }
                    else normals.push(0, 0, 0);
                    if (g.uv) { uvs.push(g.uv[cc.ui * 2], g.uv[cc.ui * 2 + 1]); anyU = true; }
                    else uvs.push(0, 0);
                    local.set(key, idx);
                }
                return idx;
            };
            if (poly.length >= 3) {
                const first = cornerIdx(poly[0]);
                for (let k = 1; k + 1 < poly.length; k++)
                    arr.push(first, cornerIdx(poly[k]), cornerIdx(poly[k + 1]));
            }
        }
    }
    const keys = [...submeshMap.keys()].sort((a, b) => a - b);
    return {
        name,
        positions: new Float32Array(positions),
        normals: anyN ? new Float32Array(normals) : null,
        uvs: anyU ? new Float32Array(uvs) : null,
        colors: null, tangents: null, boneWeights: null,
        submeshes: keys.map(k => new Uint32Array(submeshMap.get(k))),
        materials: keys.map(k => matList[k] || null)
    };
}

// Ordered material names for each Geometry, resolved via FBX Connections:
// materials connect to the Model the geometry hangs off (C "OO" mat->model).
function binaryMaterialMap(root) {
    const matNameById = new Map();
    const modelById = new Map();
    for (const m of findAll(root, 'Material'))
        matNameById.set(Number(m.props[0]), String(m.props[1]).replace(/^Material::/, '').split('\x00')[0]);
    for (const m of findAll(root, 'Model'))
        modelById.set(Number(m.props[0]), true);
    const geomById = new Map();
    for (const g of findAll(root, 'Geometry'))
        geomById.set(Number(g.props[0]), true);
    const matsOfModel = new Map();   // modelId -> [matId...] in connection order
    const geomToModel = new Map();   // geomId -> modelId
    const conns = find(root, 'Connections');
    if (!conns) return new Map();
    for (const c of conns.children) {
        if (c.name !== 'C' || String(c.props[0]) !== 'OO') continue;
        const child = Number(c.props[1]), parent = Number(c.props[2]);
        if (geomById.has(child)) geomToModel.set(child, parent);
        else if (matNameById.has(child)) {
            let arr = matsOfModel.get(parent);
            if (!arr) { arr = []; matsOfModel.set(parent, arr); }
            arr.push(child);
        }
    }
    const out = new Map(); // geomId -> [names]
    for (const [geomId, modelId] of geomToModel) {
        const names = (matsOfModel.get(modelId) || []).map(id => matNameById.get(id));
        out.set(geomId, names);
    }
    return out;
}

// --- node transforms -----------------------------------------------------
// Euler XYZ degrees -> 3x3 (column-vector convention: X applied first).
function eulerToMat3([x, y, z]) {
    const r = (d) => d * Math.PI / 180;
    const cx = Math.cos(r(x)), sx = Math.sin(r(x));
    const cy = Math.cos(r(y)), sy = Math.sin(r(y));
    const cz = Math.cos(r(z)), sz = Math.sin(r(z));
    // M = Rz * Ry * Rx (fixed-axis XYZ)
    return [
        cy * cz, cz * sx * sy - cx * sz, cx * cz * sy + sx * sz,
        cy * sz, cx * cz + sx * sy * sz, cx * sy * sz - cz * sx,
        -sy, cy * sx, cx * cy
    ];
}
const mat3mul = (a, b) => { // a*b, row-major 3x3
    const o = new Array(9);
    for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++)
        o[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
    return o;
};
const applyM3 = (m, [x, y, z]) => [
    m[0] * x + m[1] * y + m[2] * z,
    m[3] * x + m[4] * y + m[5] * z,
    m[6] * x + m[7] * y + m[8] * z
];

// geomId -> {r: 3x3, t:[x,y,z]} world transform of its Model (composed
// through the parent chain). FBX props are read as column vectors:
// v' = T + Rlcl * Rpre * (S * v)
function binaryTransforms(root) {
    const modelLocal = new Map();
    const geomById = new Map();
    for (const g of findAll(root, 'Geometry')) geomById.set(Number(g.props[0]), true);
    const propOf = (m, key) => {
        const p70 = find(m, 'Properties70');
        if (!p70) return null;
        for (const p of p70.children)
            if (p.name === 'P' && String(p.props[0]).split('\x00')[0] === key)
                return [Number(p.props[4] || 0), Number(p.props[5] || 0), Number(p.props[6] || 0)];
        return null;
    };
    for (const m of findAll(root, 'Model')) {
        modelLocal.set(Number(m.props[0]), {
            t: propOf(m, 'Lcl Translation') || [0, 0, 0],
            r: propOf(m, 'Lcl Rotation') || [0, 0, 0],
            pre: propOf(m, 'PreRotation') || [0, 0, 0],
            s: propOf(m, 'Lcl Scaling') || [1, 1, 1]
        });
    }
    const parentOf = new Map(), geomToModel = new Map();
    const conns = find(root, 'Connections');
    if (conns) for (const c of conns.children) {
        if (c.name !== 'C' || String(c.props[0]) !== 'OO') continue;
        const child = Number(c.props[1]), parent = Number(c.props[2]);
        if (geomById.has(child)) geomToModel.set(child, parent);
        else if (modelLocal.has(child)) parentOf.set(child, parent);
    }
    const world = new Map();
    const worldOf = (id, guard) => {
        if (guard.has(id)) return { m: [1, 0, 0, 0, 1, 0, 0, 0, 1], t: [0, 0, 0] };
        guard.add(id);
        if (world.has(id)) return world.get(id);
        const L = modelLocal.get(id);
        const w = !L ? { m: [1, 0, 0, 0, 1, 0, 0, 0, 1], t: [0, 0, 0] } : (() => {
            const rm = mat3mul(eulerToMat3(L.r), eulerToMat3(L.pre));
            // scale applied first (rightmost) -> scale columns: m[r][c] *= s[c]
            const pm = rm.slice();
            for (let c = 0; c < 3; c++) { pm[c] = rm[c] * L.s[0]; pm[3 + c] = rm[3 + c] * L.s[1]; pm[6 + c] = rm[6 + c] * L.s[2]; }
            const par = parentOf.has(id) ? worldOf(parentOf.get(id), guard) : { m: [1, 0, 0, 0, 1, 0, 0, 0, 1], t: [0, 0, 0] };
            const wm = mat3mul(par.m, pm);
            const rt = applyM3(par.m, L.t);
            return { m: wm, t: [rt[0] + par.t[0], rt[1] + par.t[1], rt[2] + par.t[2]] };
        })();
        world.set(id, w);
        return w;
    };
    const out = new Map();
    for (const [geomId, modelId] of geomToModel) out.set(geomId, worldOf(modelId, new Set()));
    return out;
}

function transformVerts(verts, xf) {
    if (!xf) return;
    const isId = xf.m.every((v, i) => v === [1, 0, 0, 0, 1, 0, 0, 0, 1][i]) && xf.t.every(v => v === 0);
    if (isId) return;
    for (let i = 0; i < verts.length; i += 3) {
        const p = applyM3(xf.m, [verts[i], verts[i + 1], verts[i + 2]]);
        verts[i] = p[0]; verts[i + 1] = p[1]; verts[i + 2] = p[2];
    }
}
function rotateNormals(nrm, xf) {
    if (!nrm || !xf) return;
    for (let i = 0; i < nrm.length; i += 3) {
        const n = applyM3(xf.m, [nrm[i], nrm[i + 1], nrm[i + 2]]);
        const l = Math.hypot(n[0], n[1], n[2]) || 1;
        nrm[i] = n[0] / l; nrm[i + 1] = n[1] / l; nrm[i + 2] = n[2] / l;
    }
}

export async function parseFbx(bytes, name) {
    const head = new TextDecoder('ascii').decode(bytes.subarray(0, 20));
    if (head.startsWith('Kaydara FBX Binary')) {
        const root = await parseNodeTree(bytes);
        const matMap = binaryMaterialMap(root);
        const xfMap = binaryTransforms(root);
        const geoms = findAll(root, 'Geometry');
        const results = geoms.map(g => {
            const out = geometryToPolygons(g, matMap.get(Number(g.props[0])) || []);
            const xf = xfMap.get(Number(g.props[0]));
            if (out && xf) { transformVerts(out.verts, xf); rotateNormals(out.nrm, xf); }
            return out;
        }).filter(Boolean);
        if (!results.length) throw new Error('FBX contains no mesh geometry');
        return assembleMesh(results, name);
    }
    // ASCII FBX: material names gathered file-wide (multi-geometry FBX rare in ASCII)
    const text = new TextDecoder().decode(bytes);
    const allMatNames = [...text.matchAll(/Material:\s*\d+,\s*"Material::([^"]+)"/g)].map(m => m[1].split('\x00')[0]);
    const blocks = asciiBlocks(text, /Geometry\s*:[^\n{]*"Mesh"[^\n{]*\{/g);
    const results = blocks.map(b => {
        const g = asciiGeomToPolygons(b);
        if (g) g.matNames = allMatNames;
        return g;
    }).filter(Boolean);
    if (!results.length) throw new Error('Unsupported FBX (ASCII without mesh geometry)');
    return assembleMesh(results, name);
}
