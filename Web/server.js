// ComplexLODs demo site — zero-dependency static server for Hostinger Node.js hosting.
// Serves ./public at the site root (lod.michitai.com). No database, no npm install needed.
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = process.env.PORT || 3000;
const ROOT = path.join(__dirname, 'public');

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
    '.woff2': 'font/woff2'
};

http.createServer((req, res) => {
    let urlPath;
    try {
        urlPath = decodeURIComponent(req.url.split('?')[0]);
    } catch {
        res.writeHead(400);
        return res.end();
    }
    if (urlPath === '/' || urlPath === '') urlPath = '/index.html';
    const file = path.normalize(path.join(ROOT, urlPath));
    if (!file.startsWith(ROOT + path.sep) && file !== ROOT) {
        res.writeHead(403);
        return res.end('Forbidden');
    }
    fs.readFile(file, (err, data) => {
        if (err) {
            res.writeHead(404, { 'Content-Type': 'text/plain' });
            return res.end('Not found');
        }
        res.writeHead(200, {
            'Content-Type': MIME[path.extname(file).toLowerCase()] || 'application/octet-stream',
            'Cache-Control': 'public, max-age=3600'
        });
        res.end(data);
    });
}).listen(PORT, () => console.log(`ComplexLODs demo listening on :${PORT}`));
