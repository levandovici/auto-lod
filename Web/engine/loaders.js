// Multi-format mesh loader -> ComplexLODs flat mesh format.
// Supported: .obj, .fbx (binary + ascii), .stl (binary + ascii),
// .glb / .gltf (embedded/data-URI buffers), .ply (ascii + binary LE).
import { parseObj } from '../public/js/objio.js';
import { parseFbx } from './fbx.js';

export const ACCEPT = '.obj,.fbx,.stl,.gltf,.glb,.ply';
export const FORMAT_LABEL = '.obj, .fbx, .stl, .glb / .gltf, .ply';

export async function loadMesh(name, buffer) {
    const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    const ext = (name.match(/\.([a-z0-9]+)$/i) || [, ''])[1].toLowerCase();
    switch (ext) {
        case 'obj': return parseObj(new TextDecoder().decode(bytes));
        case 'fbx': return parseFbx(bytes, name.replace(/\.[^.]+$/, ''));
        case 'stl': return parseStl(bytes, name);
        case 'glb': return parseGlb(bytes, name);
        case 'gltf': return parseGltfJson(JSON.parse(new TextDecoder().decode(bytes)), null, name);
        case 'ply': return parsePly(bytes, name);
        default: {
            // Sniff by magic: GLB, binary FBX, OBJ/PLY/STL text.
            if (bytes.length > 4 && bytes[0] === 0x67 && bytes[1] === 0x6C && bytes[2] === 0x54 && bytes[3] === 0x46)
                return parseGlb(bytes, name);
            if (new TextDecoder('ascii').decode(bytes.subarray(0, 18)) === 'Kaydara FBX Binary')
                return parseFbx(bytes, name);
            const head = new TextDecoder().decode(bytes.subarray(0, 256));
            if (head.startsWith('ply')) return parsePly(bytes, name);
            if (/^\s*(v|f|o|g|#)/m.test(head)) return parseObj(new TextDecoder().decode(bytes));
            if (head.trimStart().startsWith('solid')) return parseStl(bytes, name);
            throw new Error('Unrecognized format — try .obj, .fbx, .stl, .glb or .ply');
        }
    }
}

// ---------------- STL ----------------

function parseStl(bytes, name) {
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const isBinary = (() => {
        if (bytes.length < 84) return false;
        const n = dv.getUint32(80, true);
        return bytes.length === 84 + n * 50;
    })();

    const pos = [], nrm = [], idx = [];
    if (isBinary) {
        const n = dv.getUint32(80, true);
        for (let t = 0; t < n; t++) {
            const o = 84 + t * 50;
            const nx = dv.getFloat32(o, true), ny = dv.getFloat32(o + 4, true), nz = dv.getFloat32(o + 8, true);
            for (let k = 0; k < 3; k++) {
                const b = o + 12 + k * 12;
                pos.push(dv.getFloat32(b, true), dv.getFloat32(b + 4, true), dv.getFloat32(b + 8, true));
                nrm.push(nx, ny, nz);
                idx.push(idx.length);
            }
        }
    } else {
        const text = new TextDecoder().decode(bytes);
        let curN = [0, 0, 0];
        for (const line of text.split('\n')) {
            const p = line.trim().split(/\s+/);
            if (p[0] === 'facet') curN = [+p[2], +p[3], +p[4]];
            else if (p[0] === 'vertex' && p.length >= 4) {
                pos.push(+p[1], +p[2], +p[3]);
                nrm.push(curN[0], curN[1], curN[2]);
                idx.push(idx.length);
            }
        }
        if (!idx.length) throw new Error('STL: no vertices parsed');
    }
    return {
        name: name.replace(/\.[^.]+$/, ''),
        positions: new Float32Array(pos),
        normals: new Float32Array(nrm),
        uvs: null, colors: null, tangents: null, boneWeights: null,
        submeshes: [new Uint32Array(idx)]
    };
}

// ---------------- glTF / GLB ----------------

const CT_SIZE = { 5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4 };
const TYPE_N = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT4: 16 };

function b64ToBytes(b64) {
    if (typeof Buffer !== 'undefined') return new Uint8Array(Buffer.from(b64, 'base64'));
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

// mat4 (column-major, like glTF)
const m4Identity = () => [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
function m4Mul(a, b) {
    const o = new Array(16);
    for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) {
        let s = 0;
        for (let k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k];
        o[c * 4 + r] = s;
    }
    return o;
}
function m4FromTRS(t, q, s) {
    const [x, y, z, w] = q || [0, 0, 0, 1];
    const [sx, sy, sz] = s || [1, 1, 1];
    const x2 = x + x, y2 = y + y, z2 = z + z;
    const xx = x * x2, xy = x * y2, xz = x * z2;
    const yy = y * y2, yz = y * z2, zz = z * z2;
    const wx = w * x2, wy = w * y2, wz = w * z2;
    return [
        (1 - (yy + zz)) * sx, (xy + wz) * sx, (xz - wy) * sx, 0,
        (xy - wz) * sy, (1 - (xx + zz)) * sy, (yz + wx) * sy, 0,
        (xz + wy) * sz, (yz - wx) * sz, (1 - (xx + yy)) * sz, 0,
        t ? t[0] : 0, t ? t[1] : 0, t ? t[2] : 0, 1
    ];
}
const xformPt = (m, x, y, z) => [
    m[0] * x + m[4] * y + m[8] * z + m[12],
    m[1] * x + m[5] * y + m[9] * z + m[13],
    m[2] * x + m[6] * y + m[10] * z + m[14]
];
// normal matrix = normalized rotation of the world matrix (uniform-scale-safe)
function xformDir(m, x, y, z) {
    let nx = m[0] * x + m[4] * y + m[8] * z;
    let ny = m[1] * x + m[5] * y + m[9] * z;
    let nz = m[2] * x + m[6] * y + m[10] * z;
    const l = Math.hypot(nx, ny, nz) || 1;
    return [nx / l, ny / l, nz / l];
}

async function bufferData(gltf, binBuf, i) {
    const b = gltf.buffers[i];
    if (b.uri === undefined) {           // GLB BIN chunk
        if (!binBuf) throw new Error('glTF buffer has no URI and no BIN chunk');
        return binBuf;
    }
    if (b.uri.startsWith('data:')) return b64ToBytes(b.uri.slice(b.uri.indexOf(',') + 1));
    throw new Error('External .bin buffers are not supported in the browser demo — export a single-file .glb');
}

function readAccessor(gltf, buffers, ai) {
    const a = gltf.accessors[ai];
    if (a.sparse) throw new Error('Sparse glTF accessors are not supported');
    const n = TYPE_N[a.type];
    const out = new Float64Array(a.count * n);
    if (a.bufferView === undefined) return out; // accessor defaults to zeros
    const bv = gltf.bufferViews[a.bufferView];
    const src = buffers[bv.buffer || 0];
    const dv = new DataView(src.buffer, src.byteOffset + (bv.byteOffset || 0) + (a.byteOffset || 0));
    const elem = CT_SIZE[a.componentType] * n;
    const stride = bv.byteStride || elem;
    const read = {
        5120: (o) => dv.getInt8(o), 5121: (o) => dv.getUint8(o),
        5122: (o) => dv.getInt16(o, true), 5123: (o) => dv.getUint16(o, true),
        5125: (o) => dv.getUint32(o, true), 5126: (o) => dv.getFloat32(o, true)
    }[a.componentType];
    if (!read) throw new Error('glTF componentType ' + a.componentType);
    for (let i = 0; i < a.count; i++) {
        const base = i * stride;
        for (let k = 0; k < n; k++) out[i * n + k] = read(base + k * CT_SIZE[a.componentType]);
    }
    return { data: out, n, count: a.count };
}

async function parseGlb(bytes, name) {
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (dv.getUint32(0, true) !== 0x46546c67) throw new Error('Bad GLB magic');
    let off = 12, json = null, bin = null;
    while (off + 8 <= bytes.length) {
        const clen = dv.getUint32(off, true), ctype = dv.getUint32(off + 4, true);
        const chunk = bytes.subarray(off + 8, off + 8 + clen);
        if (ctype === 0x4e4f534a) json = JSON.parse(new TextDecoder().decode(chunk));
        else if (ctype === 0x004e4942) bin = chunk;
        off += 8 + clen;   // chunkLength already includes 4-byte alignment padding
    }
    if (!json) throw new Error('GLB: no JSON chunk');
    return parseGltfJson(json, bin, name);
}

async function parseGltfJson(gltf, binBuf, name) {
    const buffers = [];
    for (let i = 0; i < (gltf.buffers || []).length; i++)
        buffers.push(await bufferData(gltf, binBuf, i));

    // node world transforms (scene graph)
    const nodes = gltf.nodes || [];
    const world = new Array(nodes.length).fill(null);
    const scene = gltf.scenes ? gltf.scenes[gltf.scene || 0] : null;
    const roots = scene ? scene.nodes : nodes.map((_, i) => i);
    const visit = (ni, parent) => {
        const node = nodes[ni];
        const local = node.matrix ? Array.from(node.matrix)
            : m4FromTRS(node.translation, node.rotation, node.scale);
        world[ni] = m4Mul(parent, local);
        for (const c of node.children || []) visit(c, world[ni]);
    };
    for (const r of roots || []) visit(r, m4Identity());
    const meshWorld = new Map(); // mesh index -> first world matrix seen
    for (let i = 0; i < nodes.length; i++)
        if (nodes[i].mesh !== undefined && world[i] && !meshWorld.has(nodes[i].mesh))
            meshWorld.set(nodes[i].mesh, world[i]);

    const positions = [], normals = [], uvs = [];
    let anyN = false, anyU = false;
    const submeshes = [];

    for (let mi = 0; mi < (gltf.meshes || []).length; mi++) {
        const wm = meshWorld.get(mi) || m4Identity();
        for (const prim of gltf.meshes[mi].primitives) {
            if ((prim.mode ?? 4) !== 4) continue;           // triangles only
            const pa = prim.attributes.POSITION;
            if (pa === undefined) continue;
            const pAcc = readAccessor(gltf, buffers, pa);
            const nAcc = prim.attributes.NORMAL !== undefined ? readAccessor(gltf, buffers, prim.attributes.NORMAL) : null;
            const uAcc = prim.attributes.TEXCOORD_0 !== undefined ? readAccessor(gltf, buffers, prim.attributes.TEXCOORD_0) : null;
            const base = positions.length / 3;
            for (let i = 0; i < pAcc.count; i++) {
                const [x, y, z] = xformPt(wm, pAcc.data[i * 3], pAcc.data[i * 3 + 1], pAcc.data[i * 3 + 2]);
                positions.push(x, y, z);
                if (nAcc) { const [nx, ny, nz] = xformDir(wm, nAcc.data[i * 3], nAcc.data[i * 3 + 1], nAcc.data[i * 3 + 2]); normals.push(nx, ny, nz); }
                else normals.push(0, 0, 0);
                if (uAcc) uvs.push(uAcc.data[i * 2], uAcc.data[i * 2 + 1]);
                else uvs.push(0, 0);
            }
            if (nAcc) anyN = true;
            if (uAcc) anyU = true;
            const idx = [];
            if (prim.indices !== undefined) {
                const iAcc = readAccessor(gltf, buffers, prim.indices);
                for (let i = 0; i < iAcc.count; i++) idx.push(base + iAcc.data[i]);
            } else {
                for (let i = 0; i < pAcc.count; i++) idx.push(base + i);
            }
            if (idx.length) submeshes.push(new Uint32Array(idx));
        }
    }
    if (!submeshes.length) throw new Error('glTF: no triangle primitives');
    return {
        name: name.replace(/\.[^.]+$/, ''),
        positions: new Float32Array(positions),
        normals: anyN ? new Float32Array(normals) : null,
        uvs: anyU ? new Float32Array(uvs) : null,
        colors: null, tangents: null, boneWeights: null,
        submeshes
    };
}

// ---------------- PLY ----------------

const PLY_SIZE = { char: 1, uchar: 1, int8: 1, uint8: 1, short: 2, ushort: 2, int16: 2, uint16: 2, int: 4, uint: 4, int32: 4, uint32: 4, float: 4, float32: 4, double: 8, float64: 8 };

function parsePly(bytes, name) {
    const head = new TextDecoder('ascii').decode(bytes.subarray(0, Math.min(bytes.length, 8192)));
    const he = head.indexOf('end_header');
    if (!head.startsWith('ply') || he < 0) throw new Error('Bad PLY header');
    const header = head.slice(0, he).split('\n');
    let format = 'ascii';
    const elements = [];
    let cur = null;
    for (const raw of header) {
        const p = raw.trim().split(/\s+/);
        if (p[0] === 'format') format = p[1];
        else if (p[0] === 'element') { cur = { name: p[1], count: +p[2], props: [] }; elements.push(cur); }
        else if (p[0] === 'property' && cur) {
            if (p[1] === 'list') cur.props.push({ list: true, ct: p[2], type: p[3], name: p[4] });
            else cur.props.push({ list: false, type: p[1], name: p[2] });
        }
    }
    let bodyOff = he + 'end_header'.length;
    while (bodyOff < bytes.length && (bytes[bodyOff] === 13 || bytes[bodyOff] === 10)) bodyOff++;
    const pos = [], nrm = [], uvs = [], idx = [];
    let anyN = false, anyU = false;

    if (format === 'ascii') {
        const lines = new TextDecoder().decode(bytes.subarray(bodyOff)).split('\n');
        let li = 0;
        for (const el of elements) {
            for (let r = 0; r < el.count; r++, li++) {
                while (li < lines.length && !lines[li].trim()) li++;
                const vals = (lines[li] || '').trim().split(/\s+/).map(Number);
                let vi = 0;
                const rec = {};
                for (const pr of el.props) {
                    if (pr.list) {
                        const n = vals[vi++] || 0;
                        const l = vals.slice(vi, vi + n); vi += n;
                        rec[pr.name] = l;
                    } else rec[pr.name] = vals[vi++];
                }
                if (el.name === 'vertex') {
                    pos.push(rec.x || 0, rec.y || 0, rec.z || 0);
                    if (rec.nx !== undefined) { nrm.push(rec.nx, rec.ny || 0, rec.nz || 0); anyN = true; }
                    else nrm.push(0, 0, 0);
                    const u = rec.s ?? rec.texture_u, vv = rec.t ?? rec.texture_v;
                    if (u !== undefined) { uvs.push(u, vv || 0); anyU = true; }
                    else uvs.push(0, 0);
                } else if (el.name === 'face') {
                    const l = rec.vertex_indices || rec.vertex_index || [];
                    for (let k = 1; k + 1 < l.length; k++) idx.push(l[0], l[k], l[k + 1]);
                }
            }
        }
    } else if (format === 'binary_little_endian') {
        const dv = new DataView(bytes.buffer, bytes.byteOffset + bodyOff, bytes.byteLength - bodyOff);
        let off = 0;
        const rd = (t) => {
            const v = {
                char: dv.getInt8, uchar: dv.getUint8, int8: dv.getInt8, uint8: dv.getUint8,
                short: dv.getInt16, ushort: dv.getUint16, int16: dv.getInt16, uint16: dv.getUint16,
                int: dv.getInt32, uint: dv.getUint32, int32: dv.getInt32, uint32: dv.getUint32,
                float: dv.getFloat32, float32: dv.getFloat32, double: dv.getFloat64, float64: dv.getFloat64
            }[t].bind(dv);
            const sz = PLY_SIZE[t];
            const val = sz === 1 ? v(off) : v(off, true);
            off += sz;
            return val;
        };
        for (const el of elements) {
            for (let r = 0; r < el.count; r++) {
                const rec = {};
                for (const pr of el.props) {
                    if (pr.list) {
                        const n = rd(pr.ct);
                        const l = [];
                        for (let k = 0; k < n; k++) l.push(rd(pr.type));
                        rec[pr.name] = l;
                    } else rec[pr.name] = rd(pr.type);
                }
                if (el.name === 'vertex') {
                    pos.push(rec.x || 0, rec.y || 0, rec.z || 0);
                    if (rec.nx !== undefined) { nrm.push(rec.nx, rec.ny || 0, rec.nz || 0); anyN = true; }
                    else nrm.push(0, 0, 0);
                    const u = rec.s ?? rec.texture_u, vv = rec.t ?? rec.texture_v;
                    if (u !== undefined) { uvs.push(u, vv || 0); anyU = true; }
                    else uvs.push(0, 0);
                } else if (el.name === 'face') {
                    const l = rec.vertex_indices || rec.vertex_index || [];
                    for (let k = 1; k + 1 < l.length; k++) idx.push(l[0], l[k], l[k + 1]);
                }
            }
        }
    } else throw new Error('PLY format "' + format + '" not supported (use ascii or binary_little_endian)');

    if (!idx.length) throw new Error('PLY: no faces');
    return {
        name: name.replace(/\.[^.]+$/, ''),
        positions: new Float32Array(pos),
        normals: anyN ? new Float32Array(nrm) : null,
        uvs: anyU ? new Float32Array(uvs) : null,
        colors: null, tangents: null, boneWeights: null,
        submeshes: [new Uint32Array(idx)]
    };
}
