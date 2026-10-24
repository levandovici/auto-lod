# ComplexLODs demo site

Zero-dependency Node.js site for **lod.michitai.com** — an interactive
three.js viewer over real ComplexLODs output (Poly Haven `Ship Pinnace`,
CC0, Quality preset — textured preview). No database, no build step, no
`npm install`. Demo LODs regenerate via `node tools/make-demo.mjs <model>
<basename>`; diffuse textures live next to them in `public/lods/` as
`<material>_diff.jpg` (1K downscales via `tools/downscale.ps1`).

## Upload-your-own-mesh

The "Try it on your own mesh" card accepts **`.obj`, `.fbx` (binary +
ASCII), `.stl`, `.glb`/`.gltf` and `.ply`** and generates LODs **on the
server**: `engine/` is a faithful port of the Unity editor's `Core/*.cs`
stack (probabilistic quadrics, feature chains, silhouette locks,
interior-release ladder) plus format loaders/exporters — it is **never
served to the browser**. `server.js` runs jobs in worker_threads behind
`POST /api/generate` (NDJSON progress stream); `GET /api/preview|file|zip`
serve results. LOD targets follow the same preset table as
`ComplexLODProfile` (Performance 3×/2.5, Balanced 4×/2.0, Quality 6×/1.6),
each level decimated from the LOD0 source, byte-identical levels share a
mesh, and downloads match the uploaded format (or all-in-one `.zip`).

**Lifetime**: uploads and generated files live under `store/<jobId>` and
are deleted after `LODS_TTL_MIN` (default 30 min); a sweeper also evicts
oldest-first past 1 GB. Limits: 64 MB upload, one running job per
connection, ~30 jobs/hour/IP.

Known parser limits: ASCII-FBX node transforms are not applied (binary FBX
bakes model TRS into vertices); glTF needs a single-file .glb or
embedded/data-URI buffers (no external .bin fetch); PLY supports ascii +
binary_little_endian only.

Nothing is sent to the server — the site stays static-hostable.

## SEO / AI surface

`public/` also ships `robots.txt` (AI crawlers explicitly allowed),
`sitemap.xml`, `llms.txt` + `llms-full.txt` (machine-readable product
docs), `humans.txt`, `site.webmanifest`, `favicon.svg` and `og-cover.png`
(regenerate the OG card with `node tools/make-og.mjs`). Landing-page FAQ
carries FAQPage + SoftwareApplication JSON-LD.

## Testing the port

`test/compare.mjs` runs the JS simplifier on the harness's synthetic tower
(`demo_src.obj`) and prints per-target tris/stage/boundary stats to compare
with `lodharness synth` output:

```
node Web/test/compare.mjs
```

## Run locally

```
cd Web
node server.js        # listens on PORT or 3000
```

Open http://localhost:3000

## Deploy on Hostinger (Node.js hosting)

1. hPanel → **Advanced → Node.js** → Create Application:
   - Node.js version: 18 LTS or newer
   - Application root: the `Web` folder
   - Application URL: your subdomain (`lod.michitai.com` — create the
     subdomain under **Domains → Subdomains** first)
   - Startup file: `server.js`
2. Upload this `Web` folder (File Manager or Git deployment).
3. Start/restart the app in the Node.js panel — Hostinger binds the port
   automatically (`process.env.PORT`).

That's it — `public/` is served at the domain root; `/` resolves to
`index.html`. Meshes under `public/lods/` are plain `.obj` text, parsed in
the browser.

## Cache policy

HTML is served `Cache-Control: no-cache` + `ETag` (revalidated every load,
304s are free); everything else is `public, max-age=3600`. All asset
references carry a `?v=N` query — **bump `?v=` on every URL together when
deploying changed assets** (index.html, app.js, worker.js, importmap) so
no client can mix old and new files.

## Regenerating demo meshes

The demo `.glb` files are real simplifier output produced by the JS engine
(`engine/`) itself:

```
node tools/make-demo.mjs "path\to\ship_pinnace_4k.fbx" ship
```

LOD4–LOD5 intentionally share one mesh — the Unity plugin dedupes identical
geometry the same way. The standalone C# harness (`%TEMP%\lodharness`) can
still cross-check a source FBX directly:

```
lodharness <model.fbx> <lod1target> <lod2target> ...
```
