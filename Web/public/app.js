import * as THREE from 'three';
import { parseGlb } from './js/glbmini.js?v=7';

// Real ComplexLODs output for the Poly Haven "Ship Pinnace" (CC0), Quality
// preset. LOD5 deduplicates onto LOD4's mesh, exactly like the Unity plugin's
// identical-geometry sharing.
const DEMO_LODS = [
    { file: 'lods/ship_lod0.glb', label: 'LOD0 (source)', stage: '' },
    { file: 'lods/ship_lod1.glb', label: 'LOD1', stage: 'interior release ×1' },
    { file: 'lods/ship_lod2.glb', label: 'LOD2', stage: 'interior release ×1' },
    { file: 'lods/ship_lod3.glb', label: 'LOD3', stage: 'interior release ×2' },
    { file: 'lods/ship_lod4.glb', label: 'LOD4', stage: 'interior release ×3' },
    { file: 'lods/ship_lod4.glb', label: 'LOD5', stage: 'shared with LOD4' },
].map(e => ({ ...e, demo: true }));
let lods = DEMO_LODS;

const viewport = document.getElementById('viewport');
const slider = document.getElementById('lodSlider');
const lodVal = document.getElementById('lodVal');
const lodLabel = document.getElementById('lodLabel');
const triLabel = document.getElementById('triLabel');
const stageLabel = document.getElementById('stageLabel');
const wire = document.getElementById('wire');
const tex = document.getElementById('tex');
const spin = document.getElementById('spin');
const viewerTitle = document.getElementById('viewerTitle');

// --- flat mesh -> three.js geometry -----------------------------------------
function meshToGeometry(mesh) {
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(mesh.positions, 3));
    let total = 0;
    for (const s of mesh.submeshes) total += s.length;
    const index = new Uint32Array(total);
    let o = 0, start = 0;
    mesh.submeshes.forEach((s, i) => {
        index.set(s, o); o += s.length;
        geo.addGroup(start, s.length, i); start += s.length;
    });
    geo.setIndex(new THREE.BufferAttribute(index, 1));
    if (mesh.normals) geo.setAttribute('normal', new THREE.BufferAttribute(mesh.normals, 3));
    else geo.computeVertexNormals();
    if (mesh.uvs) geo.setAttribute('uv', new THREE.BufferAttribute(mesh.uvs, 2));
    geo.userData.matNames = mesh.materials || null;
    return geo;
}

// --- scene ------------------------------------------------------------------
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
viewport.appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0a0e14);
scene.fog = new THREE.Fog(0x0a0e14, 120, 320);

const camera = new THREE.PerspectiveCamera(42, 1, 0.1, 1000);

scene.add(new THREE.HemisphereLight(0xbcd3ff, 0x1a1f2a, 1.15));
const sun = new THREE.DirectionalLight(0xffffff, 2.2);
sun.position.set(60, 80, 40);
scene.add(sun);

const material = new THREE.MeshStandardMaterial({
    color: 0x8fa7d6, roughness: 0.85, metalness: 0.05, side: THREE.DoubleSide
});
const model = new THREE.Mesh(new THREE.BufferGeometry(), material);
scene.add(model);

// Per-submesh diffuse maps for the built-in demo (material name -> jpg).
const texLoader = new THREE.TextureLoader();
const texMats = new Map();
function demoMaterials(geo) {
    const names = geo.userData.matNames;
    if (!names || !names.some(Boolean)) return null;
    return names.map(n => {
        if (!n) return material;
        if (!texMats.has(n)) {
            const t = texLoader.load(`lods/${n}_diff.jpg`);
            if ('colorSpace' in t) t.colorSpace = THREE.SRGBColorSpace;
            else t.encoding = THREE.sRGBEncoding;
            texMats.set(n, new THREE.MeshStandardMaterial({
                map: t, roughness: 0.9, metalness: 0.05, side: THREE.DoubleSide
            }));
        }
        return texMats.get(n);
    });
}

const edges = new THREE.LineSegments(
    new THREE.BufferGeometry(),
    new THREE.LineBasicMaterial({ color: 0x2c3a52, transparent: true, opacity: 0.35 }));
edges.visible = false;
scene.add(edges);

// --- minimal orbit controls -------------------------------------------------
let yaw = -0.6, pitch = 0.35, dist = 90, radius = 45, target = new THREE.Vector3();
let dragging = false, px = 0, py = 0;

viewport.addEventListener('pointerdown', e => {
    dragging = true; px = e.clientX; py = e.clientY;
    viewport.setPointerCapture(e.pointerId);
});
viewport.addEventListener('pointerup', () => dragging = false);
viewport.addEventListener('pointermove', e => {
    if (!dragging) return;
    yaw -= (e.clientX - px) * 0.006;
    pitch = Math.min(1.5, Math.max(-1.5, pitch + (e.clientY - py) * 0.006));
    px = e.clientX; py = e.clientY;
});
viewport.addEventListener('wheel', e => {
    e.preventDefault();
    dist = Math.min(radius * 8, Math.max(radius * 0.35, dist * (e.deltaY > 0 ? 1.1 : 0.9)));
}, { passive: false });

function resize() {
    const w = viewport.clientWidth, h = viewport.clientHeight;
    renderer.setSize(w, h);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
}
window.addEventListener('resize', resize);
resize();

// --- LOD switching -----------------------------------------------------------
let geos = new Array(lods.length).fill(null);
let edgeGeos = new Array(lods.length).fill(null);
let current = -1;

// loading bar
const loadBar = document.getElementById('loadBar');
const loadFill = loadBar.querySelector('i');
const loadTxt = document.getElementById('loadTxt');
let inflight = 0;
function setLoad(pct, txt) {
    loadBar.hidden = false;
    loadFill.style.width = Math.round(Math.min(1, pct) * 100) + '%';
    if (txt) loadTxt.textContent = txt;
}
const hideLoad = () => { if (--inflight <= 0) { inflight = 0; loadBar.hidden = true; } };

function fetchProgress(url, onPct) {
    inflight++;
    return fetch(url).then(async r => {
        if (!r.ok) throw new Error('HTTP ' + r.status + ' ' + url);
        const total = +r.headers.get('content-length') || 0;
        if (!r.body) { onPct(1, 'loading…'); return r.arrayBuffer(); }
        const reader = r.body.getReader();
        const parts = [];
        let got = 0;
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            parts.push(value);
            got += value.length;
            onPct(total ? got / total : 0.5,
                `loading… ${(got / 1e6).toFixed(1)}` + (total ? ` / ${(total / 1e6).toFixed(1)} MB` : ' MB'));
        }
        const all = new Uint8Array(got);
        let o = 0;
        for (const p of parts) { all.set(p, o); o += p.length; }
        return all.buffer;
    });
}

const urlGeo = new Map(); // url -> Promise<BufferGeometry> (dedupes shared files)
function fetchGeo(url) {
    if (!urlGeo.has(url)) {
        urlGeo.set(url, fetchProgress(url, setLoad)
            .then(b => { setLoad(1, 'building geometry…'); return meshToGeometry(parseGlb(b)); })
            .finally(hideLoad));
    }
    return urlGeo.get(url);
}

function geometryAt(i) {
    if (!geos[i]) {
        const entry = lods[i];
        if (entry.share !== undefined) return geometryAt(entry.share);
        if (entry.mesh) geos[i] = meshToGeometry(entry.mesh);
        else return fetchGeo(entry.preview || entry.file).then(g => geos[i] = g);
    }
    return Promise.resolve(geos[i]);
}

async function showLOD(i) {
    if (i === current) return;
    current = i;
    lodVal.textContent = i;
    const geo = await geometryAt(i);
    if (current !== i) return;                    // stale fetch, slider moved on
    model.geometry = geo;
    model.material = (lods[i].demo && tex.checked && demoMaterials(geo)) || material;
    updateEdges(i);

    const tris = geo.index.count / 3;
    const g0 = await geometryAt(0);
    const srcTris = g0.index.count / 3;
    lodLabel.textContent = lods[i].label;
    triLabel.textContent = `${tris.toLocaleString()} tris · ${(100 * tris / srcTris).toFixed(1)}% of source`;
    stageLabel.textContent = lods[i].stage || '';
}

// Wireframe edges are expensive to build on dense meshes — lazily, only
// when the checkbox is on, and cached per level.
async function updateEdges(i) {
    edges.visible = wire.checked;
    if (!wire.checked) return;
    if (!edgeGeos[i]) {
        inflight++;
        setLoad(1, 'building wireframe…');
        await new Promise(r => setTimeout(r, 10)); // let the label paint
        edgeGeos[i] = new THREE.EdgesGeometry(model.geometry, 30);
        hideLoad();
    }
    edges.geometry = edgeGeos[i];
}

slider.addEventListener('input', () => showLOD(+slider.value));
wire.addEventListener('change', () => { if (current >= 0) updateEdges(current); });
tex.addEventListener('change', () => {
    const on = lods[current] && lods[current].demo && tex.checked;
    model.material = on ? (demoMaterials(model.geometry) || material) : material;
});

function frameOn(geo) {
    geo.computeBoundingSphere();
    const c = geo.boundingSphere.center, r = geo.boundingSphere.radius;
    target.copy(c);
    radius = r;
    dist = r * 2.4;
    // Model units vary (FBX exports in cm -> huge radii): scale near/far/fog
    camera.near = Math.max(0.01, r / 500);
    camera.far = r * 10;
    camera.updateProjectionMatrix();
    scene.fog.near = r * 2.2;
    scene.fog.far = r * 6;
}

// --- loop --------------------------------------------------------------------
const clock = new THREE.Clock();
renderer.setAnimationLoop(() => {
    if (spin.checked && !dragging) yaw += clock.getDelta() * 0.25;
    else clock.getDelta();
    const cp = Math.cos(pitch), sp = Math.sin(pitch);
    camera.position.set(
        target.x + dist * cp * Math.sin(yaw),
        target.y + dist * sp,
        target.z + dist * cp * Math.cos(yaw));
    camera.lookAt(target);
    renderer.render(scene, camera);
});

// --- upload + generate ---------------------------------------------------------
const meshFile = document.getElementById('meshFile');
const fileName = document.getElementById('fileName');
const presetSel = document.getElementById('preset');
const genBtn = document.getElementById('genBtn');
const genStatus = document.getElementById('genStatus');
const genReportWrap = document.getElementById('genReportWrap');
const genReport = document.getElementById('genReport');

let fileBuf = null, fileFullName = '', fileExt = 'obj', jobId = null;

meshFile.addEventListener('change', () => {
    const f = meshFile.files[0];
    if (!f) { fileBuf = null; genBtn.disabled = true; return; }
    fileFullName = f.name;
    fileExt = (f.name.match(/\.([a-z0-9]+)$/i) || [, 'obj'])[1].toLowerCase();
    fileName.textContent = f.name;
    f.arrayBuffer().then(b => {
        fileBuf = b;
        genBtn.disabled = false;
        genStatus.textContent = `${(b.byteLength / 1024).toFixed(0)} KB loaded — ready to generate`;
    });
});

function reportRow(i, stats) {
    const tr = document.createElement('tr');
    const stageTxt = stats.shared ? 'shared — identical'
        : stats.stage > 0 ? `interior release ×${stats.stage}` : 'full preservation';
    const status = stats.shared ? 'shared' :
        stats.reached ? 'target reached' : 'preservation limited';
    tr.innerHTML =
        `<td>LOD${i}</td><td>${i === 0 ? '—' : stats.target.toLocaleString()}</td>` +
        `<td>${stats.tris.toLocaleString()}</td>` +
        `<td>${i === 0 ? '—' : stats.boundaryIn + ' → ' + stats.boundaryOut}</td>` +
        `<td>${status}${stats.shared ? '' : ' · ' + stageTxt}</td>` +
        `<td><a class="btn small" href="/api/file?id=${jobId}&i=${i}" download>.${fileExt}</a></td>`;
    return tr;
}

const dlAllBtn = document.getElementById('dlAllBtn');

// --- server-side generation (algorithm never leaves the server) --------------
genBtn.addEventListener('click', async () => {
    if (!fileBuf) return;
    lods = [];
    geos = [];
    edgeGeos = [];
    urlGeo.clear();
    current = -1;
    genReport.textContent = '';
    genReportWrap.hidden = false;
    dlAllBtn.hidden = true;
    genBtn.disabled = true;
    slider.disabled = true;
    genStatus.textContent = 'uploading…';

    try {
        const res = await fetch('/api/generate?preset=' + encodeURIComponent(presetSel.value), {
            method: 'POST',
            headers: { 'Content-Type': 'application/octet-stream', 'X-File-Name': fileFullName },
            body: fileBuf
        });
        if (!res.ok || !res.body) throw new Error('HTTP ' + res.status);

        const reader = res.body.getReader();
        const dec = new TextDecoder();
        let buf = '';
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            buf += dec.decode(value, { stream: true });
            let nl;
            while ((nl = buf.indexOf('\n')) >= 0) {
                const line = buf.slice(0, nl).trim();
                buf = buf.slice(nl + 1);
                if (!line) continue;
                const m = JSON.parse(line);
                if (m.type === 'parsed') {
                    genStatus.textContent = `${m.vertices.toLocaleString()} verts · ${m.triangles.toLocaleString()} tris — generating on server…`;
                    viewerTitle.textContent = 'Live demo — ' + fileFullName + ' (uploaded)';
                } else if (m.type === 'progress') {
                    genStatus.textContent =
                        `LOD${m.level}: ${m.collapses.toLocaleString()} collapses · ${m.alive.toLocaleString()} tris → target ${m.target.toLocaleString()}`;
                } else if (m.type === 'done') {
                    jobId = m.id;
                    lods = m.levels.map(lv => ({
                        preview: `/api/preview?id=${jobId}&i=${lv.index}`,
                        label: lv.index === 0 ? 'LOD0 (source)' : 'LOD' + lv.index,
                        stage: lv.shared ? 'shared with LOD' + lv.sharedWith
                            : lv.stage > 0 ? 'interior release ×' + lv.stage : 'full preservation'
                    }));
                    geos = new Array(lods.length).fill(null);
                    edgeGeos = new Array(lods.length).fill(null);
                    slider.max = lods.length - 1;
                    for (const lv of m.levels) genReport.appendChild(reportRow(lv.index, lv));
                    const ttlMin = Math.max(1, Math.round((m.expiresAt - Date.now()) / 60000));
                    genStatus.textContent =
                        `${lods.length} levels generated — auto-deleted in ~${ttlMin} min · downloads are .${fileExt}`;
                    genBtn.disabled = false;
                    slider.disabled = false;
                    dlAllBtn.hidden = false;
                    dlAllBtn.onclick = () => { location.href = `/api/zip?id=${jobId}`; };
                    geometryAt(0).then(frameOn).then(() => showLOD(0));
                } else if (m.type === 'error') {
                    throw new Error(m.message);
                }
            }
        }
        if (!jobId) throw new Error('no result returned');
    } catch (err) {
        genStatus.textContent = 'Error: ' + (err.message || 'generation failed');
        genBtn.disabled = false;
    }
});

// --- mobile nav ---------------------------------------------------------------
const navToggle = document.getElementById('navToggle');
const siteNav = document.getElementById('siteNav');
navToggle.addEventListener('click', () => {
    const open = siteNav.classList.toggle('open');
    navToggle.classList.toggle('open', open);
    navToggle.setAttribute('aria-expanded', String(open));
});
siteNav.addEventListener('click', e => {
    if (e.target.tagName === 'A') {
        siteNav.classList.remove('open');
        navToggle.classList.remove('open');
        navToggle.setAttribute('aria-expanded', 'false');
    }
});

// --- boot --------------------------------------------------------------------
geometryAt(0).then(geo => {
    frameOn(geo);
    return showLOD(0);
}).then(() => {
    slider.disabled = false;
    // Preload the remaining demo levels in the background.
    for (let i = 1; i < lods.length; i++) geometryAt(i);
}).catch(err => {
    triLabel.textContent = 'failed to load demo meshes';
    console.error(err);
});
