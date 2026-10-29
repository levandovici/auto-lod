// Cross-validation: run the JS port on the harness's synthetic tower
// (demo_src.obj) and compare against the C# lodharness reference output.
//   node Web/test/compare.mjs [path/to/demo_src.obj] [targets...]
import { readFileSync } from 'fs';
import { parseObj, triangleCount } from '../public/js/objio.js';
import { simplify, defaultOptions } from '../engine/simplifier.js';

const file = process.argv[2] || process.env.TEMP + '\\lodharness\\demo_src.obj';
const targets = process.argv.slice(3).map(Number);
const run = targets.length ? targets : [1250, 780, 490, 305, 190];

const mesh = parseObj(readFileSync(file, 'utf8'));
mesh.name = 'demo-tower';
console.log(`src verts=${mesh.positions.length / 3} tris=${triangleCount(mesh)} normals=${mesh.normals ? 'yes' : 'no'}`);

for (const target of run) {
    const opt = defaultOptions();
    opt.targetTriangleCount = target;
    const res = simplify(mesh, opt);
    console.log(
        `target=${target}: tris=${res.resultTriangles}/${res.sourceTriangles} ` +
        `bnd=${res.sourceBoundaryComponents}->${res.resultBoundaryComponents} ` +
        `reached=${res.reachedTarget} collapses=${res.collapses} stage=${res.releaseStage} ` +
        `maxErr=${res.maxError.toFixed(4)} rms=${res.rmsError.toFixed(4)} ` +
        `verts=${res.resultVertices} t=${res.elapsedMs.toFixed(0)}ms` +
        (res.warning ? `\n    warn: ${res.warning}` : ''));
}
