using System;
using System.Collections.Generic;
using UnityEngine;

namespace ComplexLODs.Core
{
    /// <summary>
    /// Exterior/interior classification. A source triangle is "exterior" when
    /// it wins at least one pixel in an unculled conservative z-buffer render
    /// of the welded graph from any sampled viewing direction; a vertex is
    /// exterior when any incident triangle is. Classification is deliberately
    /// one-sided: sampling may hide a truly interior feature behind another
    /// interior one (harmless - it is kept), so the only dangerous direction,
    /// an exterior feature classified interior, is closed with a per-vertex
    /// ray-escape rescue pass on a uniform triangle grid.
    /// </summary>
    internal static class Visibility
    {
        public const int RasterResolution = 384;
        private const int FibonacciDirections = 42;  // plus the 6 axes = 48
        private const float DilatePx = 1.25f;        // conservative pixel dilation
        private const int GridMaxDim = 96;

        /// <summary>6 axis directions + a Fibonacci sphere sample set.</summary>
        private static List<Vector3> Directions()
        {
            var dirs = new List<Vector3>(6 + FibonacciDirections)
            {
                Vector3.right, Vector3.left, Vector3.up,
                Vector3.down, Vector3.forward, Vector3.back
            };
            const double golden = 2.399963229728653; // pi * (3 - sqrt(5))
            for (int i = 0; i < FibonacciDirections; i++)
            {
                double y = 1.0 - 2.0 * (i + 0.5) / FibonacciDirections;
                double r = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
                double phi = i * golden;
                dirs.Add(new Vector3((float)(r * Math.Cos(phi)), (float)y, (float)(r * Math.Sin(phi))));
            }
            return dirs;
        }

        /// <summary>
        /// Per-vertex exterior flags plus per-source-triangle exterior flags.
        /// A triangle is exterior iff any of its vertices is exterior (the
        /// raster pass marks it directly; the ray rescue marks it via verts).
        /// </summary>
        public static bool[] ExteriorVertices(MeshGraph g, out bool[] exteriorTri)
        {
            int nt = g.triSub.Length;
            exteriorTri = new bool[nt];
            var dirs = Directions();
            var proj = new Vector3[g.pos.Length];
            var zbuf = new float[RasterResolution * RasterResolution];

            foreach (Vector3 d in dirs)
                RasterizePass(g, d, proj, zbuf, exteriorTri);

            // Vertex = exterior when any incident alive triangle won a pixel.
            var exterior = new bool[g.pos.Length];
            for (int v = 0; v < g.pos.Length; v++)
            {
                var tris = g.vertTris[v];
                for (int i = 0; i < tris.Count; i++)
                {
                    int t = tris[i];
                    if (g.triAlive[t] && exteriorTri[t]) { exterior[v] = true; break; }
                }
            }

            // Rescue pass: a vertex whose rays can all escape to infinity is
            // visible from outside even if the raster grid missed it (thin
            // antennae, wires, lone spikes between sample pixels).
            RescuePass(g, dirs, exterior, exteriorTri);
            return exterior;
        }

        // ------------------------------------------------------------------
        // Rasterised visibility
        // ------------------------------------------------------------------

        private static void RasterizePass(MeshGraph g, Vector3 dir, Vector3[] proj,
            float[] zbuf, bool[] exteriorTri)
        {
            Vector3 d = dir.normalized;
            Vector3 u = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(d, u);

            float mnx = float.MaxValue, mxx = float.MinValue, mny = float.MaxValue, mxy = float.MinValue;
            for (int i = 0; i < g.pos.Length; i++)
            {
                Vector3 p = g.pos[i];
                float x = Vector3.Dot(p, u), y = Vector3.Dot(p, v), z = Vector3.Dot(p, d);
                proj[i] = new Vector3(x, y, z);
                if (x < mnx) mnx = x; if (x > mxx) mxx = x;
                if (y < mny) mny = y; if (y > mxy) mxy = y;
            }
            float span = Mathf.Max(mxx - mnx, mxy - mny);
            if (span < 1e-12f) return;
            float scale = (RasterResolution - 4f) / span;
            float ox = 2f - mnx * scale, oy = 2f - mny * scale;
            float zeps = Mathf.Max(1e-6f, span * 1e-4f);
            const int R = RasterResolution;
            for (int i = 0; i < zbuf.Length; i++) zbuf[i] = float.NegativeInfinity;

            for (int t = 0; t < g.triSub.Length; t++)
            {
                if (!g.triAlive[t]) continue;
                Vector3 A = proj[g.cornerVert[g.triCorners[t * 3]]];
                Vector3 B = proj[g.cornerVert[g.triCorners[t * 3 + 1]]];
                Vector3 C = proj[g.cornerVert[g.triCorners[t * 3 + 2]]];
                float ax = A.x * scale + ox, ay = A.y * scale + oy;
                float bx = B.x * scale + ox, by = B.y * scale + oy;
                float cx = C.x * scale + ox, cy = C.y * scale + oy;
                float area2 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (Mathf.Abs(area2) < 1e-7f) continue;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx)) - DilatePx));
                int x1 = Mathf.Min(R - 1, Mathf.FloorToInt(Mathf.Max(ax, Mathf.Max(bx, cx)) + DilatePx));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy)) - DilatePx));
                int y1 = Mathf.Min(R - 1, Mathf.FloorToInt(Mathf.Max(ay, Mathf.Max(by, cy)) + DilatePx));
                // Barycentric tolerance that dilates the triangle by DilatePx:
                // w_i >= -dilate / altitude_i (altitude in pixels).
                float lenA = (float)Math.Sqrt((bx - cx) * (bx - cx) + (by - cy) * (by - cy));
                float lenB = (float)Math.Sqrt((cx - ax) * (cx - ax) + (cy - ay) * (cy - ay));
                float lenC = (float)Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                float inv = 1f / area2;
                float tolA = lenA > 1e-6f ? DilatePx * lenA / Mathf.Abs(area2) : 1f;
                float tolB = lenB > 1e-6f ? DilatePx * lenB / Mathf.Abs(area2) : 1f;
                float tolC = lenC > 1e-6f ? DilatePx * lenC / Mathf.Abs(area2) : 1f;
                for (int py = y0; py <= y1; py++)
                {
                    float qy = py + 0.5f;
                    int row = py * R;
                    for (int px = x0; px <= x1; px++)
                    {
                        float qx = px + 0.5f;
                        float w0 = ((bx - qx) * (cy - qy) - (by - qy) * (cx - qx)) * inv;
                        float w1 = ((cx - qx) * (ay - qy) - (cy - qy) * (ax - qx)) * inv;
                        float w2 = 1f - w0 - w1;
                        if (w0 < -tolA || w1 < -tolB || w2 < -tolC) continue;
                        float z = w0 * A.z + w1 * B.z + w2 * C.z;
                        if (z > zbuf[row + px] + zeps)
                        {
                            zbuf[row + px] = z;
                            exteriorTri[t] = true;
                        }
                        else if (z > zbuf[row + px] - zeps)
                        {
                            exteriorTri[t] = true; // coplanar tie: both surfaces count
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Ray-escape rescue on a uniform triangle grid
        // ------------------------------------------------------------------

        private sealed class TriGrid
        {
            public Vector3 origin;
            public float cell;
            public int nx, ny, nz;
            public List<int>[] cells;
            public int[] stamp;

            public int Index(int x, int y, int z) => (z * ny + y) * nx + x;
        }

        private static TriGrid BuildGrid(MeshGraph g)
        {
            var mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < g.pos.Length; i++)
            {
                Vector3 p = g.pos[i];
                if (p.x < mn.x) mn.x = p.x; if (p.x > mx.x) mx.x = p.x;
                if (p.y < mn.y) mn.y = p.y; if (p.y > mx.y) mx.y = p.y;
                if (p.z < mn.z) mn.z = p.z; if (p.z > mx.z) mx.z = p.z;
            }
            float ext = Mathf.Max(mx.x - mn.x, Mathf.Max(mx.y - mn.y, mx.z - mn.z));
            var gr = new TriGrid { cell = Mathf.Max(ext / GridMaxDim, ext * 1e-4f), origin = mn, stamp = new int[g.triSub.Length] };
            gr.nx = Mathf.Max(1, Mathf.Min(GridMaxDim, Mathf.FloorToInt((mx.x - mn.x) / gr.cell) + 1));
            gr.ny = Mathf.Max(1, Mathf.Min(GridMaxDim, Mathf.FloorToInt((mx.y - mn.y) / gr.cell) + 1));
            gr.nz = Mathf.Max(1, Mathf.Min(GridMaxDim, Mathf.FloorToInt((mx.z - mn.z) / gr.cell) + 1));
            gr.cells = new List<int>[gr.nx * gr.ny * gr.nz];
            for (int t = 0; t < g.triSub.Length; t++)
            {
                if (!g.triAlive[t]) continue;
                Vector3 a = g.pos[g.cornerVert[g.triCorners[t * 3]]];
                Vector3 b = g.pos[g.cornerVert[g.triCorners[t * 3 + 1]]];
                Vector3 c = g.pos[g.cornerVert[g.triCorners[t * 3 + 2]]];
                int x0 = CellOf((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - mn.x) / gr.cell, gr.nx);
                int x1 = CellOf((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - mn.x) / gr.cell, gr.nx);
                int y0 = CellOf((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - mn.y) / gr.cell, gr.ny);
                int y1 = CellOf((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - mn.y) / gr.cell, gr.ny);
                int z0 = CellOf((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - mn.z) / gr.cell, gr.nz);
                int z1 = CellOf((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - mn.z) / gr.cell, gr.nz);
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int ci = gr.Index(x, y, z);
                            if (gr.cells[ci] == null) gr.cells[ci] = new List<int>(4);
                            gr.cells[ci].Add(t);
                        }
            }
            return gr;
        }

        private static int CellOf(float f, int n)
        {
            int i = (int)f;
            return i < 0 ? 0 : i >= n ? n - 1 : i;
        }

        // Moller-Trumbore; hit distance > eps counts.
        private static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, float eps)
        {
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 tv = o - a;
            float uu = Vector3.Dot(tv, pv) * inv;
            if (uu < -1e-5f || uu > 1.00001f) return false;
            Vector3 qv = Vector3.Cross(tv, e1);
            float vv = Vector3.Dot(d, qv) * inv;
            if (vv < -1e-5f || uu + vv > 1.00001f) return false;
            return Vector3.Dot(e2, qv) * inv > eps;
        }

        // True when the ray reaches the grid border without hitting any
        // triangle that is not incident to skipVert.
        private static bool Escapes(MeshGraph g, TriGrid gr, Vector3 o, Vector3 d, int skipVert, float eps, int stampId)
        {
            int x = CellOf((o.x - gr.origin.x) / gr.cell, gr.nx);
            int y = CellOf((o.y - gr.origin.y) / gr.cell, gr.ny);
            int z = CellOf((o.z - gr.origin.z) / gr.cell, gr.nz);
            int sx = d.x > 0 ? 1 : -1, sy = d.y > 0 ? 1 : -1, sz = d.z > 0 ? 1 : -1;
            float tdx = Mathf.Abs(d.x) > 1e-9f ? gr.cell / Mathf.Abs(d.x) : float.MaxValue;
            float tdy = Mathf.Abs(d.y) > 1e-9f ? gr.cell / Mathf.Abs(d.y) : float.MaxValue;
            float tdz = Mathf.Abs(d.z) > 1e-9f ? gr.cell / Mathf.Abs(d.z) : float.MaxValue;
            float tmx = Mathf.Abs(d.x) > 1e-9f
                ? ((d.x > 0 ? (x + 1) * gr.cell + gr.origin.x - o.x : o.x - (x * gr.cell + gr.origin.x)) / Mathf.Abs(d.x))
                : float.MaxValue;
            float tmy = Mathf.Abs(d.y) > 1e-9f
                ? ((d.y > 0 ? (y + 1) * gr.cell + gr.origin.y - o.y : o.y - (y * gr.cell + gr.origin.y)) / Mathf.Abs(d.y))
                : float.MaxValue;
            float tmz = Mathf.Abs(d.z) > 1e-9f
                ? ((d.z > 0 ? (z + 1) * gr.cell + gr.origin.z - o.z : o.z - (z * gr.cell + gr.origin.z)) / Mathf.Abs(d.z))
                : float.MaxValue;
            int guard = gr.nx + gr.ny + gr.nz + 4;
            while (guard-- > 0)
            {
                var cell = gr.cells[gr.Index(x, y, z)];
                if (cell != null)
                {
                    for (int i = 0; i < cell.Count; i++)
                    {
                        int t = cell[i];
                        if (gr.stamp[t] == stampId) continue;
                        gr.stamp[t] = stampId;
                        bool incident = false;
                        for (int k = 0; k < 3; k++)
                            if (g.cornerVert[g.triCorners[t * 3 + k]] == skipVert) { incident = true; break; }
                        if (incident) continue;
                        Vector3 a = g.pos[g.cornerVert[g.triCorners[t * 3]]];
                        Vector3 b = g.pos[g.cornerVert[g.triCorners[t * 3 + 1]]];
                        Vector3 c = g.pos[g.cornerVert[g.triCorners[t * 3 + 2]]];
                        if (RayTri(o, d, a, b, c, eps)) return false;
                    }
                }
                if (tmx <= tmy && tmx <= tmz) { x += sx; tmx += tdx; if (x < 0 || x >= gr.nx) return true; }
                else if (tmy <= tmz) { y += sy; tmy += tdy; if (y < 0 || y >= gr.ny) return true; }
                else { z += sz; tmz += tdz; if (z < 0 || z >= gr.nz) return true; }
            }
            return true;
        }

        private static void RescuePass(MeshGraph g, List<Vector3> dirs, bool[] exterior, bool[] exteriorTri)
        {
            var gr = BuildGrid(g);
            float diag = gr.cell * (float)Math.Sqrt(gr.nx * gr.nx + gr.ny * gr.ny + gr.nz * gr.nz);
            float eps = Mathf.Max(1e-5f, diag * 1e-5f);
            int stampId = 0;
            for (int vIdx = 0; vIdx < g.pos.Length; vIdx++)
            {
                if (exterior[vIdx]) continue;
                bool hasAlive = false;
                var vt = g.vertTris[vIdx];
                for (int i = 0; i < vt.Count; i++) if (g.triAlive[vt[i]]) { hasAlive = true; break; }
                if (!hasAlive) continue;
                foreach (Vector3 d in dirs)
                {
                    if (Escapes(g, gr, g.pos[vIdx] + d * (eps * 4f), d, vIdx, eps, ++stampId))
                    {
                        exterior[vIdx] = true;
                        for (int i = 0; i < vt.Count; i++)
                            if (g.triAlive[vt[i]]) exteriorTri[vt[i]] = true;
                        break;
                    }
                }
            }
        }
    }
}
