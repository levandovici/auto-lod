// Minimal ZIP writer — STORE method (no compression), zero dependencies.
// zipStore([{name, data:Uint8Array}]) -> Uint8Array of a valid .zip.

const CRC_TABLE = new Uint32Array(256).map((_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
});
function crc32(buf) {
    let c = 0xffffffff;
    for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 255] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
}

function dosDate(d = new Date()) {
    const time = ((d.getHours() & 31) << 11) | ((d.getMinutes() & 63) << 5) | ((d.getSeconds() >> 1) & 31);
    const date = (((d.getFullYear() - 1980) & 127) << 9) | (((d.getMonth() + 1) & 15) << 5) | (d.getDate() & 31);
    return { time, date };
}

export function zipStore(entries) {
    const enc = new TextEncoder();
    const { time, date } = dosDate();
    const parts = [];
    const central = [];
    let off = 0;

    for (const e of entries) {
        const name = enc.encode(e.name);
        const crc = crc32(e.data);
        const head = new Uint8Array(30 + name.length);
        const hv = new DataView(head.buffer);
        hv.setUint32(0, 0x04034b50, true);
        hv.setUint16(4, 20, true);          // version needed
        hv.setUint16(6, 0x0800, true);      // UTF-8 flag
        hv.setUint16(8, 0, true);           // STORE
        hv.setUint16(10, time, true);
        hv.setUint16(12, date, true);
        hv.setUint32(14, crc, true);
        hv.setUint32(18, e.data.length, true);
        hv.setUint32(22, e.data.length, true);
        hv.setUint16(26, name.length, true);
        head.set(name, 30);
        parts.push(head, e.data);
        central.push({ name, crc, size: e.data.length, off });
        off += head.length + e.data.length;
    }

    const cdStart = off;
    for (const c of central) {
        const rec = new Uint8Array(46 + c.name.length);
        const rv = new DataView(rec.buffer);
        rv.setUint32(0, 0x02014b50, true);
        rv.setUint16(4, 20, true);
        rv.setUint16(6, 20, true);
        rv.setUint16(8, 0x0800, true);
        rv.setUint16(10, 0, true);
        rv.setUint16(12, time, true);
        rv.setUint16(14, date, true);
        rv.setUint32(16, c.crc, true);
        rv.setUint32(20, c.size, true);
        rv.setUint32(24, c.size, true);
        rv.setUint16(28, c.name.length, true);
        rv.setUint32(42, c.off, true);
        rec.set(c.name, 46);
        parts.push(rec);
        off += rec.length;
    }
    const cdSize = off - cdStart;

    const eocd = new Uint8Array(22);
    const ev = new DataView(eocd.buffer);
    ev.setUint32(0, 0x06054b50, true);
    ev.setUint16(8, central.length, true);
    ev.setUint16(10, central.length, true);
    ev.setUint32(12, cdSize, true);
    ev.setUint32(16, cdStart, true);
    parts.push(eocd);
    off += 22;

    const out = new Uint8Array(off);
    let o = 0;
    for (const part of parts) { out.set(part, o); o += part.length; }
    return out;
}
