// Regenerate the site's demo LODs from a source model through the real
// engine: node Web/tools/make-demo.mjs <model.fbx> <basename>
// Writes public/lods/<basename>_lod<i>.glb (positions+normals+uvs+material
// names) and prints the stats row values for index.html.
import { readFileSync, writeFileSync } from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { loadMesh } from '../engine/loaders.js';
import { simplify, defaultOptions } from '../engine/simplifier.js';
import { exportMesh } from '../engine/exporters.js';
import { triangleCount } from '../public/js/objio.js';

const [file, base = 'demo'] = process.argv.slice(2);
const buf = readFileSync(file);
const mesh = await loadMesh(path.basename(file), buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength));
const srcTris = triangleCount(mesh);
console.log(`src: ${mesh.positions.length / 3} verts, ${srcTris} tris, ${mesh.submeshes.length} submeshes`);
console.log('materials:', JSON.stringify(mesh.materials));

const outDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'public', 'lods');
const RATE = 1.6, LEVELS = 6; // Quality preset

const identical = (a, b) =>
    a.positions.length === b.positions.length && a.positions.every((v, i) => v === b.positions[i]);

writeFileSync(path.join(outDir, `${base}_lod0.glb`), Buffer.from(exportMesh(mesh, 'glb', `${base}_lod0`)));
console.log(`LOD0 ${srcTris} tris (source)`);
let prev = mesh;
for (let l = 1; l < LEVELS; l++) {
    const target = Math.max(1, Math.round(srcTris * Math.pow(1 / RATE, l)));
    const opt = defaultOptions();
    opt.targetTriangleCount = target;
    const r = simplify(mesh, opt);
    const shared = identical(prev, r.mesh);
    writeFileSync(path.join(outDir, `${base}_lod${l}.glb`), Buffer.from(exportMesh(shared ? prev : r.mesh, 'glb', `${base}_lod${l}`)));
    console.log(`LOD${l} target=${target} tris=${r.resultTriangles} bnd=${r.sourceBoundaryComponents}->${r.resultBoundaryComponents} stage=${r.releaseStage} reached=${r.reachedTarget} shared=${shared} t=${r.elapsedMs.toFixed(0)}ms`);
    if (!shared) prev = r.mesh;
}
