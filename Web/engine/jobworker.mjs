// LOD generation job worker (worker_threads). Receives the uploaded file
// bytes, parses, simplifies per preset, writes download + preview files and
// job.json into the job dir. Progress is posted back as messages.
import { parentPort, workerData } from 'worker_threads';
import { writeFile, mkdir } from 'fs/promises';
import path from 'path';
import { loadMesh } from './loaders.js';
import { simplify, defaultOptions } from './simplifier.js';
import { exportMesh } from './exporters.js';
import { triangleCount } from '../public/js/objio.js';

const PRESETS = {
    performance: { lodCount: 3, reductionRate: 2.5 },
    balanced:    { lodCount: 4, reductionRate: 2.0 },
    quality:     { lodCount: 6, reductionRate: 1.6 }
};
const MIN_SIMPLIFY_TRIS = 256; // mirrors ComplexLODGenerator.MinSimplifyTris

function meshesIdentical(a, b) {
    if (a.positions.length !== b.positions.length) return false;
    for (let i = 0; i < a.positions.length; i++)
        if (a.positions[i] !== b.positions[i]) return false;
    if (a.submeshes.length !== b.submeshes.length) return false;
    for (let s = 0; s < a.submeshes.length; s++) {
        const x = a.submeshes[s], y = b.submeshes[s];
        if (x.length !== y.length) return false;
        for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return false;
    }
    return true;
}

const post = (m) => parentPort.postMessage(m);

try {
    const { buffer, fileName, name, preset, jobDir, ttlMs } = workerData;
    const ext = (fileName.match(/\.([a-z0-9]+)$/i) || [, 'obj'])[1].toLowerCase();

    const mesh = await loadMesh(fileName, Buffer.from(buffer));
    mesh.name = name || 'mesh';
    const srcTris = triangleCount(mesh);
    if (srcTris === 0) throw new Error('No triangles found — is this a valid model file?');
    post({ type: 'parsed', vertices: mesh.positions.length / 3, triangles: srcTris });

    await mkdir(jobDir, { recursive: true });
    const cfg = PRESETS[preset] || PRESETS.balanced;

    const levels = [];
    const writeLevel = async (i, m, stats) => {
        const safe = `${name || 'mesh'}_lod${i}`;
        await writeFile(path.join(jobDir, `${safe}.${ext}`), Buffer.from(exportMesh(m, ext, safe)));
        await writeFile(path.join(jobDir, `${safe}.preview.glb`), Buffer.from(exportMesh(m, 'glb', safe)));
        levels.push({ index: i, ext, file: `${safe}.${ext}`, preview: `${safe}.preview.glb`, ...stats });
    };

    await writeLevel(0, mesh, {
        target: srcTris, tris: srcTris, verts: mesh.positions.length / 3,
        stage: 0, reached: true, boundaryIn: 0, boundaryOut: 0,
        collapses: 0, maxError: 0, ms: 0, shared: false
    });

    let prevMesh = mesh;
    const nLevels = srcTris <= MIN_SIMPLIFY_TRIS ? 0 : cfg.lodCount - 1;
    for (let l = 1; l <= nLevels; l++) {
        const target = Math.max(1, Math.round(srcTris * Math.pow(1 / cfg.reductionRate, l)));
        const opt = defaultOptions();
        opt.targetTriangleCount = target;
        opt.progress = (collapses, src, alive, tgt) =>
            post({ type: 'progress', level: l, collapses, srcTris: src, alive, target: tgt });
        const res = simplify(mesh, opt);
        const shared = meshesIdentical(prevMesh, res.mesh);
        await writeLevel(l, shared ? prevMesh : res.mesh, {
            target, tris: res.resultTriangles, verts: res.resultVertices,
            stage: res.releaseStage, reached: res.reachedTarget,
            boundaryIn: res.sourceBoundaryComponents, boundaryOut: res.resultBoundaryComponents,
            collapses: res.collapses, maxError: res.maxError, ms: res.elapsedMs,
            shared, sharedWith: shared ? l - 1 : -1, warning: res.warning
        });
        post({ type: 'level', index: l });
        if (!shared) prevMesh = res.mesh;
    }

    const expiresAt = Date.now() + ttlMs;
    await writeFile(path.join(jobDir, 'job.json'), JSON.stringify({
        id: path.basename(jobDir), fileName, name, ext, preset,
        srcTris, srcVerts: mesh.positions.length / 3,
        created: Date.now(), expiresAt, levels
    }));
    post({ type: 'done', id: path.basename(jobDir), levels, expiresAt, srcTris, srcVerts: mesh.positions.length / 3 });
} catch (err) {
    post({ type: 'error', message: String(err && err.message || err) });
}
