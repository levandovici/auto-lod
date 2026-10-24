// ComplexLODs demo + generation server — zero-dependency Node.js (Hostinger).
// Serves ./public statically and runs the simplifier server-side (worker
// threads) so the algorithm is never shipped to the browser.
//
// API:
//   POST /api/generate?preset=balanced   body = raw model bytes, X-File-Name
//                                        header; responds x-ndjson stream of
//                                        {parsed|progress|level|done|error}
//   GET  /api/preview?id&i               LOD i as .obj text (viewer preview)
//   GET  /api/file?id&i                  LOD i in the uploaded format
//   GET  /api/zip?id                     all levels in one .zip
//
// Lifetime: uploads and generated files live under ./store/<jobId> and are
// deleted once TTL expires (default 30 min, env LODS_TTL_MIN). Nothing else
// is kept.
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { Worker } = require('worker_threads');

const PORT = process.env.PORT || 3000;
const ROOT = path.join(__dirname, 'public');
const STORE = path.join(__dirname, 'store');
const TTL_MS = Math.max(1, +process.env.LODS_TTL_MIN || 30) * 60 * 1000;
const MAX_UPLOAD = 64 * 1024 * 1024;          // 64 MB input cap
const MAX_STORE_BYTES = 1024 * 1024 * 1024;   // 1 GB total store cap
const MAX_JOBS_PER_HOUR = 30;                  // per-IP soft rate limit
const SWEEP_MS = 60 * 1000;

const MIME = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.obj': 'text/plain; charset=utf-8',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.svg': 'image/svg+xml',
    '.ico': 'image/x-icon',
    '.woff2': 'font/woff2',
    '.txt': 'text/plain; charset=utf-8',
    '.xml': 'application/xml'
};

fs.mkdirSync(STORE, { recursive: true });

// ---------- job store helpers ----------
const jobDir = (id) => path.join(STORE, id);
const validId = (id) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(id || '');

function readManifest(id) {
    try { return JSON.parse(fs.readFileSync(path.join(jobDir(id), 'job.json'), 'utf8')); }
    catch { return null; }
}

function expired(m) { return !m || Date.now() > m.expiresAt; }

function deleteJob(id) {
    try { fs.rmSync(jobDir(id), { recursive: true, force: true }); } catch {}
}

function dirSize(dir) {
    let n = 0;
    try {
        for (const f of fs.readdirSync(dir)) {
            const st = fs.statSync(path.join(dir, f));
            n += st.isFile() ? st.size : 0;
        }
    } catch {}
    return n;
}

function sweep() {
    let total = 0;
    const entries = [];
    for (const d of fs.readdirSync(STORE)) {
        const dir = jobDir(d);
        try {
            const st = fs.statSync(dir);
            if (!st.isDirectory()) continue;
            const m = readManifest(d);
            const created = m ? m.created : st.mtimeMs;
            if (expired(m || { expiresAt: created + TTL_MS })) { deleteJob(d); continue; }
            entries.push({ d, created, size: dirSize(dir) });
            total += entries[entries.length - 1].size;
        } catch {}
    }
    // Over the store cap -> evict oldest first
    entries.sort((a, b) => a.created - b.created);
    for (const e of entries) {
        if (total <= MAX_STORE_BYTES) break;
        deleteJob(e.d);
        total -= e.size;
    }
}
setInterval(sweep, SWEEP_MS).unref();

// ---------- rate limiting (in-memory, per IP) ----------
const rateMap = new Map();   // ip -> {count, windowStart}
const running = new Set();   // ips with a job in flight
function rateOk(ip) {
    const now = Date.now();
    const r = rateMap.get(ip);
    if (!r || now - r.windowStart > 3600_000) { rateMap.set(ip, { count: 1, windowStart: now }); return true; }
    if (++r.count > MAX_JOBS_PER_HOUR) return false;
    return true;
}

// ---------- API ----------
const ndjson = (res, obj) => res.write(JSON.stringify(obj) + '\n');

function safeName(s) { return String(s || 'model').replace(/[^\w.\- ]+/g, '_').slice(0, 80); }

function apiGenerate(req, res, q) {
    const ip = req.socket.remoteAddress || '';
    if (running.has(ip)) { res.writeHead(429); return res.end('{"type":"error","message":"A generation job is already running for this connection"}'); }
    if (!rateOk(ip)) { res.writeHead(429); return res.end('{"type":"error","message":"Rate limit exceeded — try again later"}'); }

    const fileName = safeName(req.headers['x-file-name'] || q.get('name') || 'model');
    const preset = q.get('preset') || 'balanced';
    const chunks = [];
    let size = 0, aborted = false;
    req.on('data', (c) => {
        size += c.length;
        if (size > MAX_UPLOAD && !aborted) {
            aborted = true;
            res.writeHead(413); res.end('{"type":"error","message":"File too large (64 MB max)"}');
            req.destroy();
            return;
        }
        chunks.push(c);
    });
    req.on('end', () => {
        if (aborted) return;
        if (!size) { res.writeHead(400); return res.end('{"type":"error","message":"Empty upload"}'); }
        const id = crypto.randomUUID();
        const dir = jobDir(id);
        fs.mkdirSync(dir, { recursive: true });

        res.writeHead(200, {
            'Content-Type': 'application/x-ndjson; charset=utf-8',
            'Cache-Control': 'no-store'
        });
        req.socket.setTimeout(0);
        running.add(ip);

        const worker = new Worker(path.join(__dirname, 'engine', 'jobworker.mjs'), {
            workerData: {
                buffer: Buffer.concat(chunks),
                fileName,
                name: fileName.replace(/\.[^.]+$/, ''),
                preset, jobDir: dir, ttlMs: TTL_MS
            }
        });
        worker.on('message', (m) => {
            ndjson(res, m);
            if (m.type === 'done' || m.type === 'error') {
                running.delete(ip);
                if (m.type === 'error') deleteJob(id);
                res.end();
                worker.terminate();
            }
        });
        worker.on('error', (e) => {
            running.delete(ip); deleteJob(id);
            ndjson(res, { type: 'error', message: String(e.message || e) });
            res.end();
        });
        req.on('close', () => { /* client navigated away mid-run */ });
        res.on('close', () => {
            if (!res.writableEnded) { running.delete(ip); deleteJob(id); worker.terminate(); }
        });
    });
    req.on('error', () => {});
}

function apiFile(res, id, i, preview) {
    if (!validId(id)) { res.writeHead(400); return res.end('bad id'); }
    const m = readManifest(id);
    if (!m) { res.writeHead(404); return res.end('job not found or expired'); }
    if (expired(m)) { deleteJob(id); res.writeHead(410); return res.end('expired — generated files are deleted after ' + Math.round(TTL_MS / 60000) + ' min'); }
    const lv = m.levels && m.levels[i | 0];
    if (!lv) { res.writeHead(404); return res.end('no such level'); }
    const file = path.join(jobDir(id), preview ? lv.preview : lv.file);
    if (!file.startsWith(STORE) || !fs.existsSync(file)) { res.writeHead(404); return res.end('not found'); }
    const headers = {
        'Content-Type': preview ? 'model/gltf-binary' : 'application/octet-stream',
        'Cache-Control': 'private, no-store'
    };
    if (!preview) headers['Content-Disposition'] = `attachment; filename="${safeName(lv.file)}"`;
    res.writeHead(200, headers);
    fs.createReadStream(file).pipe(res);
}

async function apiZip(res, id) {
    if (!validId(id)) { res.writeHead(400); return res.end('bad id'); }
    const m = readManifest(id);
    if (!m) { res.writeHead(404); return res.end('job not found or expired'); }
    if (expired(m)) { deleteJob(id); res.writeHead(410); return res.end('expired'); }
    const { zipStore } = await import('./engine/zip.js');
    const entries = m.levels.map(lv => ({
        name: lv.file,
        data: new Uint8Array(fs.readFileSync(path.join(jobDir(id), lv.file)))
    }));
    const zip = zipStore(entries);
    res.writeHead(200, {
        'Content-Type': 'application/zip',
        'Content-Disposition': `attachment; filename="${safeName(m.name)}_lods.zip"`,
        'Cache-Control': 'private, no-store'
    });
    res.end(Buffer.from(zip));
}

// ---------- static ----------
function serveStatic(req, res, urlPath) {
    if (urlPath === '/' || urlPath === '') urlPath = '/index.html';
    const file = path.normalize(path.join(ROOT, urlPath));
    if (!file.startsWith(ROOT + path.sep) && file !== ROOT) {
        res.writeHead(403);
        return res.end('Forbidden');
    }
    fs.stat(file, (err, st) => {
        if (err || !st.isFile()) {
            res.writeHead(404, { 'Content-Type': 'text/plain' });
            return res.end('Not found');
        }
        const etag = `W/"${st.size}-${Math.floor(st.mtimeMs)}"`;
        const headers = {
            'Content-Type': MIME[path.extname(file).toLowerCase()] || 'application/octet-stream',
            'Cache-Control': file.endsWith('.html') ? 'no-cache' : 'public, max-age=3600',
            'ETag': etag
        };
        if (req.headers['if-none-match'] === etag) {
            res.writeHead(304, headers);
            return res.end();
        }
        fs.readFile(file, (err2, data) => {
            if (err2) {
                res.writeHead(404, { 'Content-Type': 'text/plain' });
                return res.end('Not found');
            }
            res.writeHead(200, headers);
            res.end(data);
        });
    });
}

http.createServer((req, res) => {
    let urlPath, q;
    try {
        const u = new URL(req.url, 'http://x');
        urlPath = decodeURIComponent(u.pathname);
        q = u.searchParams;
    } catch {
        res.writeHead(400);
        return res.end();
    }
    if (req.method === 'POST' && urlPath === '/api/generate') return apiGenerate(req, res, q);
    if (req.method === 'GET' && urlPath === '/api/preview') return apiFile(res, q.get('id'), q.get('i'), true);
    if (req.method === 'GET' && urlPath === '/api/file') return apiFile(res, q.get('id'), q.get('i'), false);
    if (req.method === 'GET' && urlPath === '/api/zip') { apiZip(res, q.get('id')); return; }
    if (urlPath.startsWith('/api/')) { res.writeHead(404); return res.end('not found'); }
    serveStatic(req, res, urlPath);
}).listen(PORT, () => console.log(`ComplexLODs listening on :${PORT} (store TTL ${Math.round(TTL_MS / 60000)} min)`));
