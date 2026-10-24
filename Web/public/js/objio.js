// Wavefront OBJ <-> ComplexLODs flat mesh format (see simplifier.js).
// Supports v/vt/vn, f with "v", "v/vt", "v//vn", "v/vt/vn" (incl. negative
// indices), polygon fan triangulation, and usemtl/g/o submesh grouping.
// Each unique (v, vt, vn) corner combination becomes one mesh vertex —
// the same expansion Unity's importer performs, which preserves UV/normal
// seams for the simplifier's wedge system.

export function parseObj(text) {
    const pos = [];   // raw v
    const uv = [];    // raw vt
    const nrm = [];   // raw vn

    const verts = [];         // flattened [x,y,z]
    const vertUv = [];        // flattened [u,v]
    const vertNrm = [];       // flattened [x,y,z]
    let anyUv = false, anyNrm = false;
    const combo = new Map();  // "vi/ti/ni" -> unified vertex index

    const submeshes = [];     // index arrays
    const matNames = [];      // usemtl name per submesh (null if none)
    let current = null, curMat = null;
    const ensureSub = () => {
        if (!current) { current = []; submeshes.push(current); matNames.push(curMat); }
        return current;
    };

    const resolveIndex = (s, count) => {
        const i = parseInt(s, 10);
        return i > 0 ? i - 1 : count + i; // OBJ negative = relative to end
    };

    const cornerVertex = (tok) => {
        const parts = tok.split('/');
        const vi = resolveIndex(parts[0], pos.length / 3);
        const ti = parts.length > 1 && parts[1] !== '' ? resolveIndex(parts[1], uv.length / 2) : -1;
        const ni = parts.length > 2 && parts[2] !== '' ? resolveIndex(parts[2], nrm.length / 3) : -1;
        const key = vi + '/' + ti + '/' + ni;
        let idx = combo.get(key);
        if (idx !== undefined) return idx;
        idx = verts.length / 3;
        combo.set(key, idx);
        verts.push(pos[vi * 3], pos[vi * 3 + 1], pos[vi * 3 + 2]);
        if (ti >= 0) { vertUv.push(uv[ti * 2], uv[ti * 2 + 1]); anyUv = true; }
        else vertUv.push(0, 0);
        if (ni >= 0) { vertNrm.push(nrm[ni * 3], nrm[ni * 3 + 1], nrm[ni * 3 + 2]); anyNrm = true; }
        else vertNrm.push(0, 0, 0);
        return idx;
    };

    for (const rawLine of text.split('\n')) {
        const line = rawLine.trim();
        if (line === '' || line[0] === '#') continue;
        const c0 = line.charCodeAt(0);
        if (c0 === 118) {                              // 'v'
            if (line[1] === ' ') {
                const p = line.split(/\s+/);
                pos.push(parseFloat(p[1]), parseFloat(p[2]), parseFloat(p[3]));
            } else if (line[1] === 't') {
                const p = line.split(/\s+/);
                uv.push(parseFloat(p[1]), parseFloat(p[2]));
            } else if (line[1] === 'n') {
                const p = line.split(/\s+/);
                nrm.push(parseFloat(p[1]), parseFloat(p[2]), parseFloat(p[3]));
            }
        } else if (c0 === 102 && line[1] === ' ') {    // 'f '
            const toks = line.split(/\s+/).slice(1);
            if (toks.length < 3) continue;
            const out = ensureSub();
            const first = cornerVertex(toks[0]);
            for (let i = 1; i + 1 < toks.length; i++) {
                out.push(first, cornerVertex(toks[i]), cornerVertex(toks[i + 1]));
            }
        } else if (line.startsWith('usemtl')) {
            curMat = line.slice(6).trim() || null;
            if (current && current.length > 0) current = null; // start a fresh group
        } else if (line.startsWith('g ') || line.startsWith('o ')) {
            if (current && current.length > 0) current = null;
        }
    }

    if (submeshes.length && submeshes[submeshes.length - 1].length === 0) { submeshes.pop(); matNames.pop(); }
    if (submeshes.length === 0) { submeshes.push(new Uint32Array(0)); matNames.push(null); }

    return {
        name: 'mesh',
        positions: new Float32Array(verts),
        normals: anyNrm ? new Float32Array(vertNrm) : null,
        uvs: anyUv ? new Float32Array(vertUv) : null,
        colors: null,
        tangents: null,
        boneWeights: null,
        submeshes: submeshes.map(a => a instanceof Uint32Array ? a : new Uint32Array(a)),
        materials: matNames.some(n => n !== null) ? matNames : null
    };
}

export function triangleCount(mesh) {
    let n = 0;
    for (const s of mesh.submeshes) n += s.length / 3;
    return n;
}

export function meshToObj(mesh, name) {
    const out = [];
    out.push('o ' + (name || mesh.name || 'lod'));
    const p = mesh.positions;
    for (let i = 0; i < p.length; i += 3)
        out.push(`v ${p[i]} ${p[i + 1]} ${p[i + 2]}`);
    const hasUv = !!mesh.uvs;
    if (hasUv)
        for (let i = 0; i < mesh.uvs.length; i += 2)
            out.push(`vt ${mesh.uvs[i]} ${mesh.uvs[i + 1]}`);
    const hasN = !!mesh.normals;
    if (hasN)
        for (let i = 0; i < mesh.normals.length; i += 3)
            out.push(`vn ${mesh.normals[i]} ${mesh.normals[i + 1]} ${mesh.normals[i + 2]}`);
    for (let s = 0; s < mesh.submeshes.length; s++) {
        if (mesh.submeshes.length > 1) out.push(`usemtl ${(mesh.materials && mesh.materials[s]) || 'sub' + s}`);
        const idx = mesh.submeshes[s];
        for (let i = 0; i + 2 < idx.length; i += 3) {
            const f = (v) => {
                const k = v + 1;
                if (hasUv && hasN) return `${k}/${k}/${k}`;
                if (hasN) return `${k}//${k}`;
                if (hasUv) return `${k}/${k}`;
                return `${k}`;
            };
            out.push(`f ${f(idx[i])} ${f(idx[i + 1])} ${f(idx[i + 2])}`);
        }
    }
    return out.join('\n') + '\n';
}
