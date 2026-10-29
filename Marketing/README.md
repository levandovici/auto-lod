# Marketing

Vertical 9:16 (1080x1920) marketing cards rendered from the real demo assets —
no screenshots, no Photoshop: the script loads the site's GLBs, rasterizes them
with a software renderer (z-buffer + Lambert + texture sampling from the 4K
PNGs), and composes pixel-type cards as PNGs. Zero dependencies, Node >= 18.

## Run

```bat
node Marketing\render-cards.mjs
```

Outputs to `Marketing/out/`:

- `card1-hero.png` — 196K -> 18.7K tris hero shot
- `card2-vs.png` — V1 (old algorithm) vs V2 (current) at LOD5, -43%
- `card3-bars.png` — per-level tris bars, every target reached
- `card4-openings.png` — LOD0 vs LOD5 zoom, portholes/hatches/rigging
- `card5-brand.png` — brand card with feature bullets

## Options

- `--lods <dir>` — demo GLB folder (default `../Web/public/lods`)
- `--tex <dir>` — pinnace 4K PNG textures (default the Downloads path);
  missing dir falls back to a flat per-submesh palette
- `--out <dir>` — output folder (default `./out`)

## Files

- `tools/png.mjs` — PNG encode (zlib deflate) + decode (8/16-bit RGB/RGBA)
- `tools/font.mjs` — chunky 5x7 pixel font + `drawText` (add glyphs as needed)
- `tools/render.mjs` — software mesh renderer (perspective, z-buffer, uv textures)
- `tools/cards.mjs` — backdrop gradients, paste, rects, accent bars

Regenerate after changing `Web/public/lods/*.glb` or the copy — the numbers in
`render-cards.mjs` (`stats` table, labels) are hardcoded to the current demo
results.
