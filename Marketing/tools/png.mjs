// Minimal PNG codec — zero dependencies, Node zlib does the deflate work.
// encodePng: RGBA Uint8Array -> PNG Buffer.
// decodePng: PNG Buffer -> { w, h, data: RGB Uint8Array } (8-bit, non-interlaced).
import zlib from 'node:zlib';

const SIG = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
let CRC_T = null;
function crc32(buf) {
    if (!CRC_T) {
        CRC_T = new Uint32Array(256);
        for (let n = 0; n < 256; n++) {
            let c = n;
            for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
            CRC_T[n] = c >>> 0;
        }
    }
    let c = 0xffffffff;
    for (let i = 0; i < buf.length; i++) c = CRC_T[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
    const t = Buffer.from(type, 'ascii');
    const out = Buffer.alloc(12 + data.length);
    out.writeUInt32BE(data.length, 0);
    t.copy(out, 4);
    data.copy(out, 8);
    out.writeUInt32BE(crc32(Buffer.concat([t, data])), 8 + data.length);
    return out;
}

export function encodePng(w, h, rgba) {
    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
    ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
    const stride = w * 4;
    const raw = Buffer.alloc(h * (stride + 1));
    for (let y = 0; y < h; y++) {
        raw[y * (stride + 1)] = 0;
        rgba.copy ? rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride)
            : raw.set(rgba.subarray(y * stride, (y + 1) * stride), y * (stride + 1) + 1);
    }
    return Buffer.concat([SIG, chunk('IHDR', ihdr),
        chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

export function decodePng(buf, maxDim = 0) {
    if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
    let w = 0, h = 0, ct = 0, depth = 0, interlace = 0;
    const idat = [];
    for (let o = 8; o < buf.length;) {
        const len = buf.readUInt32BE(o), type = buf.toString('ascii', o + 4, o + 8);
        const data = buf.subarray(o + 8, o + 8 + len);
        if (type === 'IHDR') {
            w = data.readUInt32BE(0); h = data.readUInt32BE(4);
            depth = data[8]; ct = data[9]; interlace = data[12];
        } else if (type === 'IDAT') idat.push(data);
        else if (type === 'IEND') break;
        o += 12 + len;
    }
    if ((depth !== 8 && depth !== 16) || interlace !== 0 || (ct !== 2 && ct !== 6))
        throw new Error(`unsupported PNG ct=${ct} depth=${depth} interlace=${interlace}`);
    const ch = ct === 6 ? 4 : 3;
    const bps = depth === 16 ? 2 : 1;      // bytes per sample
    const chB = ch * bps;                  // bytes per pixel channel set
    const stride = w * chB;
    const raw = zlib.inflateSync(Buffer.concat(idat));
    const px = Buffer.alloc(h * stride);
    const paeth = (a, b, c) => {
        const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    };
    for (let y = 0; y < h; y++) {
        const f = raw[y * (stride + 1)];
        const src = y * (stride + 1) + 1, dst = y * stride;
        for (let x = 0; x < stride; x++) {
            const a = x >= chB ? px[dst + x - chB] : 0;
            const b = y > 0 ? px[dst - stride + x] : 0;
            const c = x >= chB && y > 0 ? px[dst - stride + x - chB] : 0;
            const v = raw[src + x];
            px[dst + x] = (f === 0 ? v : f === 1 ? v + a : f === 2 ? v + b :
                f === 3 ? v + ((a + b) >> 1) : v + paeth(a, b, c)) & 255;
        }
    }
    // Downsample to <= maxDim for texture use.
    const scale = maxDim > 0 && Math.max(w, h) > maxDim ? Math.ceil(Math.max(w, h) / maxDim) : 1;
    const ow = Math.round(w / scale), oh = Math.round(h / scale);
    const out = new Uint8Array(ow * oh * 3);
    for (let y = 0; y < oh; y++) {
        const sy = Math.min(h - 1, y * scale);
        for (let x = 0; x < ow; x++) {
            const sx = Math.min(w - 1, x * scale);
            const si = (sy * w + sx) * chB, di = (y * ow + x) * 3;
            out[di] = px[si]; out[di + 1] = px[si + bps]; out[di + 2] = px[si + 2 * bps];
        }
    }
    return { w: ow, h: oh, data: out };
}
