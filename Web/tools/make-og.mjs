// Generates public/og-cover.png (1200x630) — zero-dependency PNG writer.
// Run: node Web/tools/make-og.mjs
import { deflateSync } from 'zlib';
import { writeFileSync } from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const W = 1200, H = 630;
const px = new Uint8Array(W * H * 4);
const set = (x, y, r, g, b, a = 255) => {
    if (x < 0 || y < 0 || x >= W || y >= H) return;
    const i = (y * W + x) * 4;
    // simple alpha-over blend
    const t = a / 255;
    px[i] = px[i] * (1 - t) + r * t;
    px[i + 1] = px[i + 1] * (1 - t) + g * t;
    px[i + 2] = px[i + 2] * (1 - t) + b * t;
    px[i + 3] = 255;
};

// background: navy -> blue diagonal gradient
for (let y = 0; y < H; y++)
    for (let x = 0; x < W; x++) {
        const t = (x / W) * 0.55 + (y / H) * 0.45;
        const r = 11 + (37 - 11) * t;
        const g = 27 + (99 - 27) * t;
        const b = 51 + (235 - 51) * t;
        set(x, y, r, g, b);
    }

// faint horizontal "LOD level" bands
for (const [yy, a] of [[140, 26], [240, 40], [340, 58], [440, 80], [540, 110]])
    for (let y = yy; y < yy + 3 && y < H; y++)
        for (let x = 0; x < W; x++) set(x, y, 255, 255, 255, a);

// thick line via disc brush
const line = (x0, y0, x1, y1, r, g, b, a, w = 6) => {
    const steps = Math.max(Math.abs(x1 - x0), Math.abs(y1 - y0)) * 2;
    for (let i = 0; i <= steps; i++) {
        const t = i / steps;
        const cx = x0 + (x1 - x0) * t, cy = y0 + (y1 - y0) * t;
        for (let dy = -w; dy <= w; dy++)
            for (let dx = -w; dx <= w; dx++)
                if (dx * dx + dy * dy <= w * w)
                    set(Math.round(cx) + dx, Math.round(cy) + dy, r, g, b, a);
    }
};

// central triangle (LOD mark), white, plus two inner level lines
const cx = 600, top = 90, baseY = 540, half = 300;
line(cx, top, cx - half, baseY, 255, 255, 255, 255, 9);
line(cx, top, cx + half, baseY, 255, 255, 255, 255, 9);
line(cx - half, baseY, cx + half, baseY, 255, 255, 255, 255, 9);
for (const t of [0.45, 0.7]) {
    const y = top + (baseY - top) * t;
    const hw = half * t;
    line(cx - hw, y, cx + hw, y, 127, 176, 255, 230, 7);
}

// crc32
const crcTable = new Uint32Array(256).map((_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
});
const crc32 = (buf) => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
};
const chunk = (type, data) => {
    const out = Buffer.alloc(12 + data.length);
    out.writeUInt32BE(data.length, 0);
    out.write(type, 4, 'ascii');
    data.copy(out, 8);
    out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
    return out;
};

const raw = Buffer.alloc(H * (W * 4 + 1));
for (let y = 0; y < H; y++) {
    raw[y * (W * 4 + 1)] = 0;
    Buffer.from(px.buffer, y * W * 4, W * 4).copy(raw, y * (W * 4 + 1) + 1);
}
const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4);
ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
const png = Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0))
]);
const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'og-cover.png');
writeFileSync(out, png);
console.log('wrote', out, png.length, 'bytes');
