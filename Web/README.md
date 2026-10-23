# ComplexLODs demo site

Zero-dependency Node.js static site for **lod.michitai.com** — an interactive
three.js viewer over real ComplexLODs output (JC_LP_MegaCity `Airport_01`,
Quality preset). No database, no build step, no `npm install`.

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

## Regenerating demo meshes

The `.obj` files are real simplifier output produced by the standalone test
harness (`%TEMP%\lodharness`) against the actual airport FBX:

```
lodharness Assets/JC_LP_MegaCity/Models/Buildings/SM_Buildings_Airport_01.fbx 3877 2423 1514
```

LOD3–LOD5 intentionally share `airport_lod3.obj` — the Unity plugin dedupes
identical geometry the same way.
