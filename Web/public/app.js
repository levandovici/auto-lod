import * as THREE from 'three';

// Real ComplexLODs output for Airport_01 (Quality preset). LOD3-5 deduplicate
// onto the same mesh, exactly like the Unity plugin's identical-geometry sharing.
const LODS = [
    { file: 'lods/airport_lod0.obj', label: 'LOD0 (source)', stage: '' },
    { file: 'lods/airport_lod1.obj', label: 'LOD1', stage: 'interior release ×1' },
    { file: 'lods/airport_lod2.obj', label: 'LOD2', stage: 'interior release ×2' },
    { file: 'lods/airport_lod3.obj', label: 'LOD3', stage: 'interior release ×3' },
    { file: 'lods/airport_lod3.obj', label: 'LOD4', stage: 'shared with LOD3' },
    { file: 'lods/airport_lod3.obj', label: 'LOD5', stage: 'shared with LOD3' },
];

const viewport = document.getElementById('viewport');
const slider = document.getElementById('lodSlider');
const lodVal = document.getElementById('lodVal');
const lodLabel = document.getElementById('lodLabel');
const triLabel = document.getElementById('triLabel');
const stageLabel = document.getElementById('stageLabel');
const wire = document.getElementById('wire');
const spin = document.getElementById('spin');

// --- minimal OBJ parser ("v x y z" + "f a b c", 1-based) -------------------
function parseObj(text) {
    const positions = [];
    const index = [];
    for (const line of text.split('\n')) {
        if (line.charCodeAt(0) === 118 && line[1] === ' ') {          // "v "
            const p = line.split(/\s+/);
            positions.push(parseFloat(p[1]), parseFloat(p[2]), parseFloat(p[3]));
        } else if (line.charCodeAt(0) === 102 && line[1] === ' ') {   // "f "
            const p = line.split(/\s+/);
            for (let i = 1; i <= 3; i++) {
                const vi = parseInt(p[i].split('/')[0], 10);
                index.push(vi > 0 ? vi - 1 : positions.length / 3 + vi);
            }
        }
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(positions), 3));
    geo.setIndex(index);
    geo.computeVertexNormals();
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

const edges = new THREE.LineSegments(
    new THREE.BufferGeometry(),
    new THREE.LineBasicMaterial({ color: 0x2c3a52, transparent: true, opacity: 0.35 }));
edges.visible = false;
scene.add(edges);

// --- minimal orbit controls -------------------------------------------------
let yaw = -0.6, pitch = 0.35, dist = 90, target = new THREE.Vector3();
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
    dist = Math.min(400, Math.max(15, dist * (e.deltaY > 0 ? 1.1 : 0.9)));
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
const geos = new Array(LODS.length).fill(null);
let current = -1;

async function geometryAt(i) {
    if (!geos[i]) {
        const res = await fetch(LODS[i].file);
        geos[i] = parseObj(await res.text());
    }
    return geos[i];
}

async function showLOD(i) {
    if (i === current) return;
    current = i;
    lodVal.textContent = i;
    const geo = await geometryAt(i);
    if (current !== i) return;                    // stale fetch, slider moved on
    model.geometry = geo;
    edges.geometry = new THREE.EdgesGeometry(geo, 30);

    const tris = geo.index.count / 3;
    const srcTris = geos[0] ? geos[0].index.count / 3 : tris;
    lodLabel.textContent = LODS[i].label;
    triLabel.textContent = `${tris.toLocaleString()} tris · ${(100 * tris / srcTris).toFixed(1)}% of source`;
    stageLabel.textContent = LODS[i].stage;
}

slider.addEventListener('input', () => showLOD(+slider.value));
wire.addEventListener('change', () => edges.visible = wire.checked);

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

// --- boot --------------------------------------------------------------------
geometryAt(0).then(geo => {
    geo.computeBoundingSphere();
    const c = geo.boundingSphere.center, r = geo.boundingSphere.radius;
    target.copy(c);
    dist = r * 2.4;
    return showLOD(0);
}).then(() => {
    slider.disabled = false;
    // Preload the remaining levels in the background.
    for (let i = 1; i < LODS.length; i++) geometryAt(i);
}).catch(err => {
    triLabel.textContent = 'failed to load demo meshes';
    console.error(err);
});
