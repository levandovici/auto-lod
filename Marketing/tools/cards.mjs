// Card composition helpers for 9:16 marketing images.
export function makeImg(w, h, [r, g, b, a] = [10, 14, 20, 255]) {
    const data = new Uint8Array(w * h * 4);
    for (let i = 0; i < w * h; i++) {
        data[i * 4] = r; data[i * 4 + 1] = g; data[i * 4 + 2] = b; data[i * 4 + 3] = a;
    }
    return { w, h, data };
}

// Vertical gradient + a soft radial glow at (fx, fy) fraction of the frame.
export function backdrop(img, { top = [10, 14, 20], bottom = [16, 22, 34], glow = [56, 80, 140], fx = 0.5, fy = 0.32, glowAmt = 0.55 } = {}) {
    const { w, h, data } = img;
    const gx = fx * w, gy = fy * h;
    const gr = Math.hypot(w, h) * 0.62;
    for (let y = 0; y < h; y++) {
        const t = y / (h - 1);
        const br = top[0] + (bottom[0] - top[0]) * t;
        const bg = top[1] + (bottom[1] - top[1]) * t;
        const bb = top[2] + (bottom[2] - top[2]) * t;
        for (let x = 0; x < w; x++) {
            const d = Math.hypot(x - gx, y - gy) / gr;
            const g = Math.max(0, 1 - d) * glowAmt;
            const i = (y * w + x) * 4;
            data[i] = Math.min(255, br + glow[0] * g * (1 - t * 0.7));
            data[i + 1] = Math.min(255, bg + glow[1] * g * (1 - t * 0.7));
            data[i + 2] = Math.min(255, bb + glow[2] * g * (1 - t * 0.7));
            data[i + 3] = 255;
        }
    }
    return img;
}

export function paste(dst, src, dx, dy) {
    for (let y = 0; y < src.h; y++) {
        const py = dy + y;
        if (py < 0 || py >= dst.h) continue;
        for (let x = 0; x < src.w; x++) {
            const px = dx + x;
            if (px < 0 || px >= dst.w) continue;
            const si = (y * src.w + x) * 4;
            const a = src.data[si + 3] / 255;
            if (a <= 0) continue;
            const di = (py * dst.w + px) * 4;
            dst.data[di] = src.data[si] * a + dst.data[di] * (1 - a);
            dst.data[di + 1] = src.data[si + 1] * a + dst.data[di + 1] * (1 - a);
            dst.data[di + 2] = src.data[si + 2] * a + dst.data[di + 2] * (1 - a);
            dst.data[di + 3] = Math.min(255, src.data[si + 3] + dst.data[di + 3]);
        }
    }
}

export function fillRect(img, x0, y0, w, h, [r, g, b], alpha = 1) {
    for (let y = Math.max(0, y0); y < Math.min(img.h, y0 + h); y++)
        for (let x = Math.max(0, x0); x < Math.min(img.w, x0 + w); x++) {
            const i = (y * img.w + x) * 4;
            img.data[i] = r * alpha + img.data[i] * (1 - alpha);
            img.data[i + 1] = g * alpha + img.data[i + 1] * (1 - alpha);
            img.data[i + 2] = b * alpha + img.data[i + 2] * (1 - alpha);
            img.data[i + 3] = 255;
        }
}

// Gradient accent bar (brand style: blue -> light blue).
export function accentBar(img, x0, y0, w, h) {
    const c0 = [79, 124, 255], c1 = [122, 162, 255];
    for (let y = y0; y < y0 + h; y++)
        for (let x = x0; x < x0 + w; x++) {
            const t = (x - x0) / w;
            const i = (y * img.w + x) * 4;
            img.data[i] = c0[0] + (c1[0] - c0[0]) * t;
            img.data[i + 1] = c0[1] + (c1[1] - c0[1]) * t;
            img.data[i + 2] = c0[2] + (c1[2] - c0[2]) * t;
            img.data[i + 3] = 255;
        }
}

// Horizontal pair of bars (V1 gray vs V2 blue) with labels drawn by caller.
export function pairBar(img, x, y, wMax, w1, w2, [c1, c2], h = 22, gap = 8) {
    fillRect(img, x, y, Math.round(w1), h, c1);
    fillRect(img, x, y + h + gap, Math.round(w2), h, c2);
    return { v1End: x + w1, v2End: x + w2 };
}
