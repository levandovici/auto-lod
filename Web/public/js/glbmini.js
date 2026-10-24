// Minimal GLB reader for previews — positions, normals, uv, one submesh per
// primitive, material names. ~glTF 2.0 triangle meshes only.
const CT = { 5120: Int8Array, 5121: Uint8Array, 5122: Int16Array, 5123: Uint16Array, 5125: Uint32Array, 5126: Float32Array };
const TN = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4 };

export function parseGlb(buf) {
    const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (dv.getUint32(0, true) !== 0x46546c67) throw new Error('not GLB');
    let off = 12, json = null, bin = null;
    while (off + 8 <= bytes.length) {
        const clen = dv.getUint32(off, true), ctype = dv.getUint32(off + 4, true);
        const chunk = bytes.subarray(off + 8, off + 8 + clen);
        if (ctype === 0x4e4f534a) json = JSON.parse(new TextDecoder().decode(chunk));
        else if (ctype === 0x004e4942) bin = chunk;
        off += 8 + clen;
    }
    if (!json) throw new Error('GLB: no JSON chunk');

    const read = (ai) => {
        const a = json.accessors[ai];
        const bv = json.bufferViews[a.bufferView];
        const n = TN[a.type];
        const base = (bv.byteOffset || 0) + (a.byteOffset || 0);
        const T = CT[a.componentType];
        const stride = bv.byteStride || 0;
        if (!stride || stride === n * T.BYTES_PER_ELEMENT) {
            // fast path: contiguous slice
            const out = new T(a.count * n);
            const src = new T(bin.buffer, bin.byteOffset + base, a.count * n);
            out.set(src);
            return out;
        }
        const out = new T(a.count * n);
        const dvv = new DataView(bin.buffer, bin.byteOffset + base);
        const get = T === Float32Array ? (o) => dvv.getFloat32(o, true)
            : T === Uint32Array ? (o) => dvv.getUint32(o, true)
            : T === Uint16Array ? (o) => dvv.getUint16(o, true)
            : (o) => dvv.getUint8(o);
        for (let i = 0; i < a.count; i++)
            for (let k = 0; k < n; k++) out[i * n + k] = get(i * stride + k * T.BYTES_PER_ELEMENT);
        return out;
    };

    const cat = (arrays) => {
        let n = 0;
        for (const a of arrays) n += a.length;
        const out = new Float32Array(n);
        let o = 0;
        for (const a of arrays) { out.set(a, o); o += a.length; }
        return out;
    };

    const posParts = [], nrmParts = [], uvParts = [], submeshes = [], matNames = [];
    let anyN = false, anyU = false, vBase = 0;
    for (const mesh of json.meshes || []) {
        for (const pr of mesh.primitives) {
            if ((pr.mode ?? 4) !== 4) continue;
            const pa = read(pr.attributes.POSITION);
            const nVerts = pa.length / 3;
            posParts.push(pa);
            if (pr.attributes.NORMAL !== undefined) { nrmParts.push(read(pr.attributes.NORMAL)); anyN = true; }
            else nrmParts.push(new Float32Array(pa.length));
            if (pr.attributes.TEXCOORD_0 !== undefined) { uvParts.push(read(pr.attributes.TEXCOORD_0)); anyU = true; }
            else uvParts.push(new Float32Array(nVerts * 2));
            const idx = pr.indices !== undefined ? read(pr.indices) : Uint32Array.from({ length: nVerts }, (_, i) => i);
            const out = new Uint32Array(idx.length);
            for (let i = 0; i < idx.length; i++) out[i] = idx[i] + vBase;
            submeshes.push(out);
            vBase += nVerts;
            matNames.push(pr.material !== undefined && json.materials ? json.materials[pr.material].name : null);
        }
    }
    return {
        positions: cat(posParts),
        normals: anyN ? cat(nrmParts) : null,
        uvs: anyU ? cat(uvParts) : null,
        submeshes,
        materials: matNames.some(n => n) ? matNames : null
    };
}
