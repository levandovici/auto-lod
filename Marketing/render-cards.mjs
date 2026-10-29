// Render 9:16 (1080x1920) TikTok marketing cards from the real demo GLBs.
//   node Marketing/render-cards.mjs [--lods <dir>] [--tex <dir>] [--out <dir>]
// Defaults: --lods ../Web/public/lods --tex <pinnace textures> --out ./out
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseGlb } from '../Web/public/js/glbmini.js';
import { encodePng, decodePng } from './tools/png.mjs';
import { drawText, textWidth } from './tools/font.mjs';
import { renderMesh, meshBounds } from './tools/render.mjs';
import { makeImg, backdrop, paste, fillRect, accentBar } from './tools/cards.mjs';

const DIR = path.dirname(fileURLToPath(import.meta.url));
const arg = (k, d) => { const i = process.argv.indexOf(k); return i >= 0 ? process.argv[i + 1] : d; };
const LODS = path.resolve(DIR, arg('--lods', '../Web/public/lods'));
const TEXDIR = arg('--tex', 'C:/Users/nichi/Downloads/ship_pinnace_4k.fbx/textures');
const OUT = path.resolve(DIR, arg('--out', './out'));
mkdirSync(OUT, { recursive: true });

const W = 1080, H = 1920;
const WHITE = [230, 238, 255];
const DIM = [150, 165, 195];
const ACC = [122, 162, 255];
const GRAY = [110, 120, 140];

const load = (f) => {
    const b = readFileSync(path.join(LODS, f));
    return parseGlb(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength));
};

// --- textures (optional; palette fallback) -----------------------------------
const textures = {};
if (existsSync(TEXDIR)) {
    for (const n of ['aft', 'rigging', 'details', 'hull', 'deck', 'interior', 'sails']) {
        const f = path.join(TEXDIR, `ship_pinnace_${n}_diff_4k.png`);
        if (existsSync(f)) {
            try { textures['ship_pinnace_' + n] = decodePng(readFileSync(f), 1024); }
            catch (e) { console.warn('tex skip', n, e.message); }
        }
    }
    console.log('textures:', Object.keys(textures).length);
} else console.log('no texture dir - palette fallback');

const render = (mesh, o) => {
    const layer = makeImg(W, o.layerH || 1200, [0, 0, 0, 0]);
    renderMesh(layer, mesh, { textures, ...o });
    return layer;
};

const num = (n) => n.toLocaleString('en-US');
const stats = [
    ['LOD0', 196169, 196169], ['LOD1', 122605, 122606], ['LOD2', 76629, 76628],
    ['LOD3', 47892, 47892], ['LOD4', 33068, 29933], ['LOD5', 33068, 28584],
];

const footer = (img) => {
    accentBar(img, 0, H - 64, W, 4);
    drawText(img, 'COMPLEXLODS', 60, H - 52, { scale: 4, color: DIM });
    drawText(img, 'AUTO-LOD · UNITY 6000+ URP', W - 60, H - 52, { scale: 4, color: DIM, align: 'right' });
};
const save = (img, name) => {
    writeFileSync(path.join(OUT, name), encodePng(img.w, img.h, img.data));
    console.log('wrote', name);
};

// ---------------------------------------------------------------- card 1: hero
{
    const img = backdrop(makeImg(W, H), { fy: 0.36, glowAmt: 0.7 });
    paste(img, render(load('ship_lod0.glb'), { layerH: 1150, fit: 0.92, yaw: -0.65, pitch: 0.12, cy: -0.02 }), 0, 110);
    accentBar(img, 60, 1310, 220, 6);
    drawText(img, '196,169', 60, 1360, { scale: 13, color: WHITE, shadow: [0, 0, 0] });
    drawText(img, '→ 28,584 TRIS', 60, 1470, { scale: 8, color: ACC, shadow: [0, 0, 0] });
    drawText(img, 'AND IT STILL LOOKS LIKE A SHIP.', 60, 1580, { scale: 5, color: DIM });
    drawText(img, 'FEATURE-PRESERVING LOD GENERATION', 60, 1650, { scale: 4, color: DIM });
    footer(img);
    save(img, 'card1-hero.png');
}

// --------------------------------------------------------------- card 2: V1vsV2
{
    const img = backdrop(makeImg(W, H), { glow: [80, 60, 120], fy: 0.3 });
    drawText(img, 'SAME BUDGET. NEW ENGINE.', W / 2, 90, { scale: 7, color: WHITE, align: 'center', shadow: [0, 0, 0] });
    const v1 = load('shipv1_lod5.glb'), v2 = load('ship_lod5.glb');
    paste(img, render(v1, { layerH: 720, fit: 0.88, yaw: -0.65, pitch: 0.1 }), 0, 210);
    paste(img, render(v2, { layerH: 720, fit: 0.88, yaw: -0.65, pitch: 0.1 }), 0, 950);
    // panel labels
    fillRect(img, 0, 205, W, 46, [10, 14, 20], 0.72);
    drawText(img, 'V1 · PREVIOUS — 33,068 TRIS', 60, 215, { scale: 4, color: DIM });
    fillRect(img, 0, 945, W, 46, [10, 14, 20], 0.72);
    drawText(img, 'V2 · CURRENT — 28,584 TRIS', 60, 955, { scale: 4, color: ACC });
    drawText(img, 'MASTS STAY. GAPS DON\'T GROW.', W / 2, 1730, { scale: 8, color: WHITE, align: 'center', shadow: [0, 0, 0] });
    drawText(img, 'THIN PARTS DEGRADE — NEVER VANISH', W / 2, 1800, { scale: 4, color: DIM, align: 'center' });
    footer(img);
    save(img, 'card2-vs.png');
}

// ------------------------------------------------------------- card 3: bar stats
{
    const img = backdrop(makeImg(W, H), { fy: 0.22 });
    drawText(img, 'EVERY TARGET REACHED', W / 2, 110, { scale: 7, color: WHITE, align: 'center', shadow: [0, 0, 0] });
    drawText(img, 'QUALITY PRESET · SHIP PINNACE 196K', W / 2, 190, { scale: 4, color: DIM, align: 'center' });
    const x = 60, maxW = 840, y0 = 320, rowH = 148, barH = 40, gap = 10;
    drawText(img, 'V1 OLD', W - 60, 270, { scale: 4, color: GRAY, align: 'right' });
    drawText(img, 'V2 NEW', W - 60, 270 + 36, { scale: 4, color: ACC, align: 'right' });
    for (let i = 0; i < stats.length; i++) {
        const [lv, t1, t2] = stats[i];
        const y = y0 + i * (rowH + 40);
        drawText(img, lv, x, y - 40, { scale: 5, color: WHITE });
        const w1 = Math.max(8, Math.round(maxW * t1 / 196169));
        const w2 = Math.max(8, Math.round(maxW * t2 / 196169));
        fillRect(img, x, y, w1, barH, GRAY);
        fillRect(img, x, y + barH + gap, w2, barH, ACC);
        drawText(img, num(t1), x + w1 + 16, y + 6, { scale: 3, color: GRAY });
        drawText(img, num(t2), x + w2 + 16, y + barH + gap + 6, { scale: 3, color: ACC });
    }
    drawText(img, 'V1 STALLED AT 33K.', W / 2, 1690, { scale: 6, color: DIM, align: 'center' });
    drawText(img, 'V2 SAILED THROUGH.', W / 2, 1760, { scale: 6, color: WHITE, align: 'center', shadow: [0, 0, 0] });
    footer(img);
    save(img, 'card3-bars.png');
}

// ------------------------------------------------------------ card 4: openings
{
    const img = backdrop(makeImg(W, H), { glow: [60, 90, 60], fy: 0.3 });
    drawText(img, 'OPENINGS SURVIVE', W / 2, 100, { scale: 8, color: WHITE, align: 'center', shadow: [0, 0, 0] });
    const z = { fit: 0.9, zoom: 2.4, yaw: -0.9, pitch: 0.02, cy: 0.16 };
    paste(img, render(load('ship_lod0.glb'), { layerH: 640, ...z }), 0, 260);
    paste(img, render(load('ship_lod5.glb'), { layerH: 640, ...z }), 0, 1000);
    fillRect(img, 0, 255, W, 46, [10, 14, 20], 0.72);
    drawText(img, 'LOD0 · 196,169 TRIS', 60, 265, { scale: 4, color: DIM });
    fillRect(img, 0, 995, W, 46, [10, 14, 20], 0.72);
    drawText(img, 'V2 LOD5 · 28,584 TRIS', 60, 1005, { scale: 4, color: ACC });
    drawText(img, 'PORTHOLES · GUN HATCHES · RIGGING', W / 2, 1710, { scale: 4, color: DIM, align: 'center' });
    drawText(img, 'STILL THERE AT LOD5', W / 2, 1770, { scale: 5, color: WHITE, align: 'center', shadow: [0, 0, 0] });
    footer(img);
    save(img, 'card4-openings.png');
}

// --------------------------------------------------------------- card 5: brand
{
    const img = backdrop(makeImg(W, H), { fy: 0.28, glowAmt: 0.8 });
    paste(img, render(load('ship_lod3.glb'), { layerH: 900, fit: 0.8, yaw: -0.5, pitch: 0.1, cy: -0.05 }), 0, 120);
    drawText(img, 'COMPLEX', W / 2, 1060, { scale: 12, color: WHITE, align: 'center', shadow: [0, 0, 0] });
    drawText(img, 'LODS', W / 2, 1180, { scale: 12, color: ACC, align: 'center', shadow: [0, 0, 0] });
    const items = [
        'SILHOUETTE LOCKED', 'OPENINGS PRESERVED', 'HIDDEN INTERIOR STRIPPED',
        'EVERY LOD FROM SOURCE', 'HONEST TARGET REPORTING',
    ];
    items.forEach((s, i) => {
        fillRect(img, 300, 1330 + i * 78, 14, 14, ACC);
        drawText(img, s, 336, 1318 + i * 78, { scale: 4, color: WHITE });
    });
    drawText(img, 'FEATURE-PRESERVING LOD GENERATION', W / 2, 1770, { scale: 4, color: DIM, align: 'center' });
    footer(img);
    save(img, 'card5-brand.png');
}

console.log('done ->', OUT);
