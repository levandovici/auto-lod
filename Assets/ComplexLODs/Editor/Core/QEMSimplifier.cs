using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace ComplexLODs.Core
{
    /// <summary>
    /// Quadric Error Metric edge-collapse simplifier with feature preservation:
    /// boundary constraint planes (holes/openings), crease constraints, UV/normal
    /// seam constraints, per-vertex importance weights, vertex locking and
    /// normal-flip rejection. Produces crack-free, submesh-preserving LOD meshes.
    /// </summary>
    internal static class QEMSimplifier
    {
        // Maximum allowed collapse edge length, in units of the endpoints'
        // local feature scale (mean incident edge length at build time).
        private const float MaxCollapseDistance = 4f;

        internal static int debugWedgeMismatch;

        private struct HeapEntry
        {
            public double cost;
            public Edge edge;
            public int version;
        }

        private sealed class MinHeap
        {
            private readonly List<HeapEntry> data = new List<HeapEntry>(1024);
            public int Count => data.Count;
            public double PeekCost => data.Count > 0 ? data[0].cost : double.PositiveInfinity;

            public void Push(double cost, Edge e, int version)
            {
                data.Add(new HeapEntry { cost = cost, edge = e, version = version });
                int i = data.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (data[p].cost <= data[i].cost) break;
                    (data[p], data[i]) = (data[i], data[p]);
                    i = p;
                }
            }

            public HeapEntry Pop()
            {
                HeapEntry top = data[0];
                int last = data.Count - 1;
                data[0] = data[last];
                data.RemoveAt(last);
                if (data.Count > 0)
                {
                    int i = 0;
                    for (;;)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        if (l < data.Count && data[l].cost < data[m].cost) m = l;
                        if (r < data.Count && data[r].cost < data[m].cost) m = r;
                        if (m == i) break;
                        (data[m], data[i]) = (data[i], data[m]);
                        i = m;
                    }
                }
                return top;
            }
        }

        /// <summary>
        /// Static surface-deviation guard. A uniform grid over all SOURCE
        /// vertex positions; when a collapse produces a triangle, every source
        /// vertex projecting inside its footprint must stay within epsilon of
        /// the triangle's plane. This rejects "spanning" triangles that cover
        /// recessed windows, door insets and frames at the wrong depth - the
        /// shard/criss-cross failure on architectural facades - while still
        /// allowing large fills over genuinely flat regions. The grid is
        /// read-only, so it costs a fraction of a live collision index.
        /// </summary>
        private sealed class SurfaceGuard
        {
            private readonly MeshGraph g;
            private readonly Vector3[] srcPos;
            private readonly float cell;
            private readonly float maxDepth;
            private readonly Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>(1024);
            private readonly List<int> scratch = new List<int>(256);
            private readonly List<Vector3Int> scratchCells = new List<Vector3Int>(8);

            public SurfaceGuard(MeshGraph g, Bounds bounds)
            {
                this.g = g;
                srcPos = (Vector3[])g.pos.Clone();
                var lens = new List<float>(g.triAlive.Length);
                for (int t = 0; t < g.triAlive.Length; t++)
                {
                    if (!g.triAlive[t]) continue;
                    int c0 = g.triCorners[t * 3], c1 = g.triCorners[t * 3 + 1], c2 = g.triCorners[t * 3 + 2];
                    Vector3 p0 = srcPos[g.cornerVert[c0]], p1 = srcPos[g.cornerVert[c1]], p2 = srcPos[g.cornerVert[c2]];
                    lens.Add(Mathf.Max((p1 - p0).magnitude, Mathf.Max((p2 - p1).magnitude, (p0 - p2).magnitude)));
                }
                lens.Sort();
                float med = lens.Count > 0 ? lens[lens.Count / 2] : 1f;
                cell = Mathf.Max(med * 0.5f, 1e-4f);
                // Depth tolerance: fine enough to catch window/door insets but
                // loose enough for bevelled edges and coplanar decal sheets.
                maxDepth = Mathf.Max(bounds.size.magnitude * 0.004f, med * 0.15f);
                for (int v = 0; v < srcPos.Length; v++)
                {
                    var c = CellOf(srcPos[v]);
                    if (!grid.TryGetValue(c, out var l)) { l = new List<int>(8); grid[c] = l; }
                    l.Add(v);
                }
            }

            private Vector3Int CellOf(Vector3 p) =>
                new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));

            /// <summary>
            /// Would collapsing edge (a,b) to p push any modified triangle
            /// across a surface-depth discontinuity?
            /// </summary>
            public bool Violates(int a, int b, Vector3 p, List<int> aTris, List<int> bTris)
            {
                for (int side = 0; side < 2; side++)
                {
                    var list = side == 0 ? aTris : bTris;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        int t = list[i];
                        if (!g.triAlive[t]) continue;
                        int c0 = g.triCorners[t * 3], c1 = g.triCorners[t * 3 + 1], c2 = g.triCorners[t * 3 + 2];
                        int v0 = g.cornerVert[c0], v1 = g.cornerVert[c1], v2 = g.cornerVert[c2];
                        bool hasA = v0 == a || v1 == a || v2 == a, hasB = v0 == b || v1 == b || v2 == b;
                        if (hasA && hasB) continue; // dies in the collapse
                        Vector3 p0 = (v0 == a || v0 == b) ? p : g.pos[v0];
                        Vector3 p1 = (v1 == a || v1 == b) ? p : g.pos[v1];
                        Vector3 p2 = (v2 == a || v2 == b) ? p : g.pos[v2];
                        if (CoversOffPlane(p0, p1, p2)) return true;
                    }
                }
                return false;
            }

            private bool CoversOffPlane(Vector3 p0, Vector3 p1, Vector3 p2)
            {
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
                float nMag = n.magnitude;
                if (nMag < 1e-10f) return false;
                n /= nMag;

                // Query cells overlapped by the triangle's bounding box.
                scratchCells.Clear();
                int x0 = Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)) / cell);
                int x1 = Mathf.FloorToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)) / cell);
                int y0 = Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)) / cell);
                int y1 = Mathf.FloorToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y)) / cell);
                int z0 = Mathf.FloorToInt(Mathf.Min(p0.z, Mathf.Min(p1.z, p2.z)) / cell);
                int z1 = Mathf.FloorToInt(Mathf.Max(p0.z, Mathf.Max(p1.z, p2.z)) / cell);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                            scratchCells.Add(new Vector3Int(x, y, z));

                scratch.Clear();
                foreach (var cl in scratchCells)
                    if (grid.TryGetValue(cl, out var l)) scratch.AddRange(l);
                if (scratch.Count == 0) return false;

                // Dominant axis for the 2D footprint projection.
                int ax = Mathf.Abs(n.x) >= Mathf.Abs(n.y) && Mathf.Abs(n.x) >= Mathf.Abs(n.z) ? 0
                       : Mathf.Abs(n.y) >= Mathf.Abs(n.z) ? 1 : 2;
                for (int i = 0; i < scratch.Count; i++)
                {
                    Vector3 v = srcPos[scratch[i]];
                    float dist = Vector3.Dot(n, v - p0);
                    if (Mathf.Abs(dist) <= maxDepth) continue;
                    if (Inside2D(v, p0, p1, p2, ax)) return true;
                }
                return false;
            }

            // Point-in-triangle via barycentric coords on the dominant-axis
            // projection; small margin excludes verts sitting on the boundary.
            private static bool Inside2D(Vector3 v, Vector3 p0, Vector3 p1, Vector3 p2, int ax)
            {
                Vector2 q, a, b, c;
                if (ax == 0) { q = new Vector2(v.y, v.z); a = new Vector2(p0.y, p0.z); b = new Vector2(p1.y, p1.z); c = new Vector2(p2.y, p2.z); }
                else if (ax == 1) { q = new Vector2(v.x, v.z); a = new Vector2(p0.x, p0.z); b = new Vector2(p1.x, p1.z); c = new Vector2(p2.x, p2.z); }
                else { q = new Vector2(v.x, v.y); a = new Vector2(p0.x, p0.y); b = new Vector2(p1.x, p1.y); c = new Vector2(p2.x, p2.y); }
                float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Mathf.Abs(d) < 1e-12f) return false;
                float u = ((b.y - c.y) * (q.x - c.x) + (c.x - b.x) * (q.y - c.y)) / d;
                float w = ((c.y - a.y) * (q.x - c.x) + (a.x - c.x) * (q.y - c.y)) / d;
                float z = 1f - u - w;
                const float margin = 0.02f;
                return u > margin && w > margin && z > margin;
            }
        }

        public static SimplifyResult Simplify(Mesh source, SimplifyOptions opt)
        {
            var sw = Stopwatch.StartNew();
            var res = new SimplifyResult();
            if (source == null) { res.warning = "Null mesh"; return res; }
            if (source.blendShapeCount > 0)
                res.warning = "Blendshapes are not preserved by the simplifier and were dropped.";

            MeshGraph g = MeshGraph.Build(source, opt.weldTolerance);
            res.sourceTriangles = g.aliveTris;
            res.sourceVertices = g.pos.Length;
            res.sourceBoundaryComponents = g.CountBoundaryComponents();

            var vertEdges = BuildVertexEdges(g);
            var ci = BuildComponents(g);
            ApplyVertexWeightsAndLocks(g, source, opt, ci);

            Quadric[] quadrics = BuildQuadrics(g, opt);
            var guard = new SurfaceGuard(g, source.bounds);
            var holeInfo = BuildHoleLoops(g);

            // Whole-component removal queue for small sheets (panes, frames):
            // price = the sheet's cheapest natural collapse, so it dies when
            // its own detail would have been consumed anyway - but atomically.
            var compDeaths = new List<CompDeath>();
            for (int cid = 0; cid < ci.compSize.Length; cid++)
            {
                if (!ci.atomic[cid]) continue;
                double best = double.PositiveInfinity;
                var seen = new HashSet<Edge>();
                var cv = ci.compVerts[cid];
                for (int i = 0; i < cv.Count; i++)
                {
                    var ve = vertEdges[cv[i]];
                    for (int j = 0; j < ve.Count; j++)
                    {
                        Edge e2 = ve[j];
                        if (e2.removed || !seen.Add(e2)) continue;
                        bool internalEdge = e2.tris.Count > 0;
                        for (int k = 0; k < e2.tris.Count; k++)
                            if (ci.triComp[e2.tris[k]] != cid) { internalEdge = false; break; }
                        if (!internalEdge) continue;
                        double c = EdgeCost(g, quadrics, e2, opt, out _);
                        if (c < best) best = c;
                    }
                }
                if (!double.IsInfinity(best))
                    compDeaths.Add(new CompDeath { cid = cid, tris = ci.compTris[cid], verts = ci.compVerts[cid], cost = best });
            }
            compDeaths.Sort((x, y) => x.cost.CompareTo(y.cost));
            int compIdx = 0;

            var heap = new MinHeap();
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;
                double cost = EdgeCost(g, quadrics, e, opt, out _);
                if (!double.IsInfinity(cost)) heap.Push(cost, e, e.version);
            }

            int target = Mathf.Max(0, opt.targetTriangleCount);
            int collapses = 0;
            double errSum = 0, errMax = 0;
            int iteration = 0;

            // Relaxation ladder: on heavily fragmented meshes (modular kits of
            // disconnected sheets) the guards legitimately exhaust every legal
            // collapse far above the target. Rather than stalling, drop the
            // geometric guards in order of increasing structural impact so
            // deep LODs can fuse sheets and fill openings into a solid shell.
            // Flips, slivers, locks and the distance cap always stay on.
            int relax = 0;
            while (g.aliveTris > target)
            {
                while (heap.Count > 0 && g.aliveTris > target)
                {
                    // Whole-component removals cheaper than the next edge
                    // collapse fire first: a small sheet dies atomically at
                    // roughly the moment its own collapse cost was due.
                    while (compIdx < compDeaths.Count && compDeaths[compIdx].cost <= heap.PeekCost && g.aliveTris > target)
                    {
                        var cd = compDeaths[compIdx++];
                        if (RemoveComponent(g, vertEdges, cd, ci)) collapses++;
                    }
                    if (g.aliveTris <= target) break;

                    HeapEntry h = heap.Pop();
                    Edge e = h.edge;
                    if (e.removed || h.version != e.version) continue;

                    if ((iteration++ & 511) == 0)
                    {
                        opt.progress?.Invoke(collapses, res.sourceTriangles, g.aliveTris, target);
                        if (opt.cancelRequested != null && opt.cancelRequested())
                        {
                            res.cancelled = true;
                            break;
                        }
                    }

                    double cost = EdgeCost(g, quadrics, e, opt, out Vector3 p);
                    if (double.IsInfinity(cost)) { e.removed = true; continue; }

                    // Bounded displacement: refuse collapses whose edge is much
                    // longer than the local feature scale. Without this a vertex
                    // can jump across a whole pane/region, stretching its triangle
                    // fan over unrelated coplanar geometry (the z-fighting "shard"
                    // look on glass facades and window cells).
                    float limit = MaxCollapseDistance * Mathf.Max(g.localScale[e.a], g.localScale[e.b]);
                    if ((g.pos[e.a] - g.pos[e.b]).magnitude > limit) continue;

                    if (opt.preventFlips && WouldFlip(g, e, p)) continue;
                    // Surface-deviation guard: a new triangle may not cover
                    // recessed detail (window/door insets, frames) at the wrong
                    // depth - the cut-through shard failure on facades.
                    if (relax < 1 && guard.Violates(e.a, e.b, p, g.vertTris[e.a], g.vertTris[e.b])) continue;
                    // Opening guard: two verts of the same hole loop may only
                    // collapse along the loop itself. Interior chords would let a
                    // triangle span the opening and fill it (doors, windows,
                    // entrances losing their hole).
                    if (relax < 2 && CreatesHoleChord(g, vertEdges, e, holeInfo)) continue;
                    // Atomic-sheet guard: small sheets die whole or survive whole.
                    var dying = DyingTris(g, e);
                    if (relax < 3 && PartialSheetKill(g, dying, ci)) continue;
                    if (relax < 3 && opt.preserveBoundaryLoops && ViolatesBoundaryTopology(g, vertEdges, e)) continue;
                    if (relax < 4 && opt.preserveTopology && !PassesLinkCondition(g, vertEdges, e)) continue;

                    CommitCollapse(g, quadrics, vertEdges, e, p, holeInfo.vertLoop);
                    for (int i = 0; i < dying.Count; i++)
                    {
                        int dc = ci.triComp[dying[i]];
                        if (dc >= 0) ci.compAlive[dc]--;
                    }
                    collapses++;
                    double derr = Math.Sqrt(Math.Max(0.0, cost));
                    errSum += derr;
                    if (derr > errMax) errMax = derr;

                    // Re-push all edges incident to the surviving vertex.
                    var incident = vertEdges[e.a];
                    for (int i = incident.Count - 1; i >= 0; i--)
                    {
                        Edge e2 = incident[i];
                        if (e2.removed) { incident.RemoveAt(i); continue; }
                        e2.version++;
                        double c = EdgeCost(g, quadrics, e2, opt, out _);
                        if (!double.IsInfinity(c)) heap.Push(c, e2, e2.version);
                        else e2.removed = true;
                    }
                }
                if (res.cancelled || g.aliveTris <= target || relax >= 4) break;
                relax++;
                // Re-push every live edge under the relaxed regime.
                foreach (var kv in g.edges)
                {
                    Edge e = kv.Value;
                    if (e.removed) continue;
                    e.version++;
                    double c = EdgeCost(g, quadrics, e, opt, out _);
                    if (!double.IsInfinity(c)) heap.Push(c, e, e.version);
                    else e.removed = true;
                }
            }

            res.collapses = collapses;
            res.maxError = (float)errMax;
            res.rmsError = collapses > 0 ? (float)(errSum / collapses) : 0f;
            res.resultTriangles = g.aliveTris;
            res.reachedTarget = g.aliveTris <= target;
            res.resultBoundaryComponents = g.CountBoundaryComponents();

            debugWedgeMismatch = 0;
            res.mesh = RebuildMesh(g, source, opt, out int outVerts);
            res.resultVertices = outVerts;
            res.wedgeMismatch = debugWedgeMismatch;
            res.elapsedMs = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        private static List<Edge>[] BuildVertexEdges(MeshGraph g)
        {
            var ve = new List<Edge>[g.pos.Length];
            for (int i = 0; i < ve.Length; i++) ve[i] = new List<Edge>(8);
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;
                ve[e.a].Add(e);
                ve[e.b].Add(e);
            }
            return ve;
        }

        private static void ApplyVertexWeightsAndLocks(MeshGraph g, Mesh source, SimplifyOptions opt, CompInfo ci)
        {
            int n = g.pos.Length;

            // Radial extremity weighting: vertices far from the bbox center are
            // silhouette landmarks (roof peaks, corners) - make them costly to move.
            if (opt.extremesWeight > 0f)
            {
                Bounds b = source.bounds;
                Vector3 c = b.center;
                float maxD = 0.0001f;
                var dist = new float[n];
                for (int i = 0; i < n; i++)
                {
                    dist[i] = (g.pos[i] - c).magnitude;
                    if (dist[i] > maxD) maxD = dist[i];
                }
                for (int i = 0; i < n; i++)
                {
                    float t = Mathf.Pow(dist[i] / maxD, opt.extremesPower);
                    g.weight[i] *= Mathf.Lerp(1f, opt.extremesWeight, t);
                }
            }

            // Vertex color importance mask.
            if (opt.useVertexColorImportance)
            {
                var cols = source.colors32;
                if (cols != null && cols.Length == source.vertexCount)
                {
                    var val = new float[n];
                    foreach (Wedge w in g.wedges)
                    {
                        if (w.srcVertex < 0 || w.srcVertex >= cols.Length) continue;
                        Color32 col = cols[w.srcVertex];
                        float f = opt.vertexColorChannel == 0 ? col.r :
                                  opt.vertexColorChannel == 1 ? col.g :
                                  opt.vertexColorChannel == 2 ? col.b : col.a;
                        float fn = f / 255f;
                        if (fn > val[w.vertex]) val[w.vertex] = fn;
                    }
                    for (int i = 0; i < n; i++)
                        g.weight[i] *= Mathf.Lerp(1f, opt.importanceMax, val[i]);
                }
            }

            // Boundary locking
            if (opt.lockBoundaries)
            {
                foreach (var kv in g.edges)
                {
                    Edge e = kv.Value;
                    if (e.boundary) { g.locked[e.a] = true; g.locked[e.b] = true; }
                }
            }

            // Lock top-N highest vertices.
            if (opt.lockTopVertices > 0)
            {
                var order = new List<int>(n);
                for (int i = 0; i < n; i++) order.Add(i);
                order.Sort((x, y) => g.pos[y].y.CompareTo(g.pos[x].y));
                int k = Mathf.Min(opt.lockTopVertices, n);
                for (int i = 0; i < k; i++) g.locked[order[i]] = true;
            }

            // Lock the 6 bounding-box axis extremes.
            if (opt.lockBoundsExtremes)
            {
                int minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
                for (int i = 1; i < n; i++)
                {
                    Vector3 p = g.pos[i];
                    if (p.x < g.pos[minX].x) minX = i; if (p.x > g.pos[maxX].x) maxX = i;
                    if (p.y < g.pos[minY].y) minY = i; if (p.y > g.pos[maxY].y) maxY = i;
                    if (p.z < g.pos[minZ].z) minZ = i; if (p.z > g.pos[maxZ].z) maxZ = i;
                }
                g.locked[minX] = g.locked[maxX] = g.locked[minY] =
                g.locked[maxY] = g.locked[minZ] = g.locked[maxZ] = true;
            }

            // Component-size weighting: verts belonging to small disconnected
            // sheets (glass panes, window frames, decals - modular kits split
            // these into dozens of 2-16 tri components) become expensive to
            // collapse. A pane then survives as a unit until the wall around
            // it has simplified, instead of dying one corner at a time and
            // leaving see-through slits in the facade.
            if (ci != null)
            {
                const float CompRef = 128f, MaxCompW = 32f;
                var seen = new HashSet<int>();
                for (int i = 0; i < n; i++)
                {
                    var lt = g.vertTris[i];
                    for (int j = 0; j < lt.Count; j++)
                    {
                        int t = lt[j];
                        if (!g.triAlive[t]) continue;
                        int cid = ci.triComp[t];
                        if (!seen.Add(cid)) continue;
                        float w = Mathf.Min(MaxCompW, CompRef / ci.compSize[cid]);
                        if (w > 1f)
                        {
                            // weight every vert of this component
                            foreach (int vv in ci.compVerts[cid]) g.weight[vv] *= w;
                        }
                    }
                }
            }
        }

        /// <summary>Connected-component data over triangles sharing welded verts.</summary>
        private sealed class CompInfo
        {
            public int[] triComp;         // comp id per tri
            public int[] compSize;        // tris per comp
            public int[] compAlive;       // remaining alive tris per comp
            public bool[] atomic;         // small comp -> dies whole or not at all
            public List<int>[] compTris;  // tri ids (atomic comps only)
            public List<int>[] compVerts; // vert ids (atomic comps only)
        }

        /// <summary>A whole-component removal queued in cost order.</summary>
        private sealed class CompDeath
        {
            public int cid;
            public List<int> tris;
            public List<int> verts;
            public double cost;
        }

        // Components of <= AtomicSheetTris triangles are treated as single
        // sheets: panes, frames, decals. They may only die atomically.
        private const int AtomicSheetTris = 24;

        private static CompInfo BuildComponents(MeshGraph g)
        {
            int tc = g.triAlive.Length;
            int n = g.pos.Length;
            var first = new int[n];
            for (int i = 0; i < n; i++) first[i] = -1;
            var par = new int[tc];
            for (int t = 0; t < tc; t++) par[t] = t;
            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
            for (int t = 0; t < tc; t++)
            {
                if (!g.triAlive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = g.cornerVert[g.triCorners[t * 3 + k]];
                    if (first[v] < 0) first[v] = t;
                    else { int rt = Find(t), rf = Find(first[v]); if (rt != rf) par[rf] = rt; }
                }
            }
            var index = new Dictionary<int, int>();
            var ci = new CompInfo
            {
                triComp = new int[tc],
            };
            var sizes = new List<int>();
            for (int t = 0; t < tc; t++)
            {
                if (!g.triAlive[t]) { ci.triComp[t] = -1; continue; }
                int r = Find(t);
                if (!index.TryGetValue(r, out int id)) { id = sizes.Count; index[r] = id; sizes.Add(0); }
                ci.triComp[t] = id;
                sizes[id]++;
            }
            int c = sizes.Count;
            ci.compSize = sizes.ToArray();
            ci.compAlive = (int[])ci.compSize.Clone();
            ci.atomic = new bool[c];
            ci.compTris = new List<int>[c];
            ci.compVerts = new List<int>[c];
            var vset = new HashSet<int>[c];
            for (int t = 0; t < tc; t++)
            {
                int cid = ci.triComp[t];
                if (cid < 0 || ci.compSize[cid] > 128) continue; // only comps that can be weighted/atomic need lists
                if (ci.compSize[cid] <= AtomicSheetTris) ci.atomic[cid] = true;
                (ci.compTris[cid] ??= new List<int>(4)).Add(t);
                for (int k = 0; k < 3; k++)
                {
                    int v = g.cornerVert[g.triCorners[t * 3 + k]];
                    (vset[cid] ??= new HashSet<int>()).Add(v);
                }
            }
            for (int i = 0; i < c; i++)
                if (vset[i] != null) ci.compVerts[i] = new List<int>(vset[i]);
            return ci;
        }

        /// <summary>Tris that a collapse of e would kill (contain both endpoints).</summary>
        private static List<int> DyingTris(MeshGraph g, Edge e)
        {
            var list = new List<int>(4);
            var lt = g.vertTris[e.b];
            for (int i = 0; i < lt.Count; i++)
            {
                int t = lt[i];
                if (g.triAlive[t] && TriHasVert(g, t, e.a)) list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// Atomic-sheet guard: a collapse may only kill ALL remaining tris of a
        /// small component (or none). Partial death leaves half-sheets: the
        /// see-through slits and shard teeth on glass facades.
        /// </summary>
        private static bool PartialSheetKill(MeshGraph g, List<int> dying, CompInfo ci)
        {
            for (int i = 0; i < dying.Count; i++)
            {
                int cid = ci.triComp[dying[i]];
                if (cid < 0 || !ci.atomic[cid]) continue;
                int killed = 0;
                for (int j = 0; j < dying.Count; j++)
                    if (ci.triComp[dying[j]] == cid) killed++;
                if (killed < ci.compAlive[cid]) return true;
            }
            return false;
        }

        /// <summary>
        /// Whole-component removal: every surviving tri of the sheet dies at
        /// once. Edges that lose all their tris are retired.
        /// </summary>
        private static bool RemoveComponent(MeshGraph g, List<Edge>[] vertEdges, CompDeath cd, CompInfo ci)
        {
            bool any = false;
            for (int i = 0; i < cd.tris.Count; i++)
                if (g.triAlive[cd.tris[i]]) { g.RemoveTri(cd.tris[i]); any = true; }
            if (!any) return false;
            ci.compAlive[cd.cid] = 0;
            for (int i = 0; i < cd.verts.Count; i++)
            {
                var list = vertEdges[cd.verts[i]];
                for (int j = 0; j < list.Count; j++)
                {
                    Edge e2 = list[j];
                    if (e2.removed) continue;
                    bool alive = false;
                    for (int k = 0; k < e2.tris.Count; k++)
                        if (g.triAlive[e2.tris[k]]) { alive = true; break; }
                    if (!alive) e2.removed = true;
                }
            }
            return true;
        }

        private static Quadric[] BuildQuadrics(MeshGraph g, SimplifyOptions opt)
        {
            int n = g.pos.Length;
            var quadrics = new Quadric[n];

            // Probabilistic face quadrics, area-normalised, with per-triangle
            // noise sigma = 5% of mean edge length.
            const double relSigma = 0.05;
            for (int t = 0; t < g.triSub.Length; t++)
            {
                int c0 = g.triCorners[t * 3], c1 = g.triCorners[t * 3 + 1], c2 = g.triCorners[t * 3 + 2];
                int v0 = g.cornerVert[c0], v1 = g.cornerVert[c1], v2 = g.cornerVert[c2];
                Quadric q = Quadric.FromProbabilisticTriangle(g.pos[v0], g.pos[v1], g.pos[v2], relSigma);
                quadrics[v0].Add(ref q);
                quadrics[v1].Add(ref q);
                quadrics[v2].Add(ref q);
            }

            float creaseDeg = opt.creaseAngle;
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;

                if (e.tris.Count != 2 && opt.boundaryWeight > 0f)
                {
                    // Boundary edges (openings) and non-manifold edges (3+ faces
                    // sharing an edge - T-junctions and stacked sheets common in
                    // modular building kits) both need hard protection.
                    AddEdgeConstraintPlane(g, quadrics, e, e.tris[0], opt.boundaryWeight);
                }
                else if (e.tris.Count == 2)
                {
                    if (e.creaseAngle > creaseDeg && opt.creaseWeight > 0f)
                    {
                        double w = opt.creaseWeight * (e.creaseAngle / 180.0);
                        AddEdgeConstraintPlane(g, quadrics, e, e.tris[0], w);
                        AddEdgeConstraintPlane(g, quadrics, e, e.tris[1], w);
                    }
                    if (e.seam && opt.seamWeight > 0f)
                    {
                        AddEdgeConstraintPlane(g, quadrics, e, e.tris[0], opt.seamWeight);
                        AddEdgeConstraintPlane(g, quadrics, e, e.tris[1], opt.seamWeight);
                    }
                }
            }

            for (int i = 0; i < n; i++)
                if (g.weight[i] != 1f) quadrics[i].Scale(g.weight[i]);

            return quadrics;
        }

        private static void AddEdgeConstraintPlane(MeshGraph g, Quadric[] quadrics, Edge e, int tri, double weight)
        {
            Vector3 pa = g.pos[e.a], pb = g.pos[e.b];
            Vector3 edgeDir = pb - pa;
            float len = edgeDir.magnitude;
            if (len < 1e-12f) return;
            edgeDir /= len;

            int c0 = g.triCorners[tri * 3], c1 = g.triCorners[tri * 3 + 1], c2 = g.triCorners[tri * 3 + 2];
            Vector3 p0 = g.pos[g.cornerVert[c0]], p1 = g.pos[g.cornerVert[c1]], p2 = g.pos[g.cornerVert[c2]];
            Vector3 tn = Vector3.Cross(p1 - p0, p2 - p0);
            float m = tn.magnitude;
            if (m < 1e-12f) return;
            tn /= m;
            double area = m * 0.5;

            Vector3 cn = Vector3.Cross(edgeDir, tn);
            if (cn.sqrMagnitude < 1e-12f) return;
            cn.Normalize();
            double d = -Vector3.Dot(cn, pa);
            Quadric q = Quadric.FromPlane(cn.x, cn.y, cn.z, d, weight * area);
            quadrics[e.a].Add(ref q);
            quadrics[e.b].Add(ref q);
        }

        // ------------------------------------------------------------------
        // Collapse
        // ------------------------------------------------------------------

        private static double EdgeCost(MeshGraph g, Quadric[] quadrics, Edge e, SimplifyOptions opt, out Vector3 best)
        {
            best = Vector3.zero;
            bool la = g.locked[e.a], lb = g.locked[e.b];
            if (la && lb) return double.PositiveInfinity;

            Quadric q = quadrics[e.a];
            q.Add(ref quadrics[e.b]);

            double cost = double.PositiveInfinity;
            Vector3 pa = g.pos[e.a], pb = g.pos[e.b];

            if (!lb)
            {
                double c = q.Eval(pa);
                if (c < cost) { cost = c; best = pa; }
            }
            if (!la)
            {
                double c = q.Eval(pb);
                if (c < cost) { cost = c; best = pb; }
            }
            if (!la && !lb && !e.phantom)
            {
                // Midpoints are only valid along edges that still coincide with a
                // real source edge. Phantom edges (inherited through a moved
                // vertex) can bridge separate surface sheets; their midpoint
                // would float in space and create crossing triangles.
                Vector3 mid = (pa + pb) * 0.5f;
                double c = q.Eval(mid);
                if (c < cost) { cost = c; best = mid; }
            }
            return cost;
        }

        private static Vector3 CrossWithOverride(MeshGraph g, int tri, int ovA, int ovB, Vector3 p)
        {
            int c0 = g.triCorners[tri * 3], c1 = g.triCorners[tri * 3 + 1], c2 = g.triCorners[tri * 3 + 2];
            Vector3 r0 = Pos(g, c0, ovA, ovB, p);
            Vector3 r1 = Pos(g, c1, ovA, ovB, p);
            Vector3 r2 = Pos(g, c2, ovA, ovB, p);
            return Vector3.Cross(r1 - r0, r2 - r0);
        }

        private static Vector3 Pos(MeshGraph g, int corner, int ovA, int ovB, Vector3 p)
        {
            int v = g.cornerVert[corner];
            if (v == ovA || v == ovB) return p;
            return g.pos[v];
        }

        private static bool TriHasVert(MeshGraph g, int tri, int v)
        {
            for (int k = 0; k < 3; k++)
                if (g.cornerVert[g.triCorners[tri * 3 + k]] == v) return true;
            return false;
        }

        /// <summary>
        /// Normalized triangle quality: 4*sqrt(3)*A / sum(edge^2).
        /// 1.0 = equilateral, near 0 = needle/sliver.
        /// </summary>
        private static float ShapeFactor(MeshGraph g, int tri, int ovA, int ovB, Vector3 p, bool useNew)
        {
            int c0 = g.triCorners[tri * 3], c1 = g.triCorners[tri * 3 + 1], c2 = g.triCorners[tri * 3 + 2];
            Vector3 r0 = useNew ? Pos(g, c0, ovA, ovB, p) : g.pos[g.cornerVert[c0]];
            Vector3 r1 = useNew ? Pos(g, c1, ovA, ovB, p) : g.pos[g.cornerVert[c1]];
            Vector3 r2 = useNew ? Pos(g, c2, ovA, ovB, p) : g.pos[g.cornerVert[c2]];
            float e1 = (r1 - r0).sqrMagnitude;
            float e2 = (r2 - r1).sqrMagnitude;
            float e3 = (r0 - r2).sqrMagnitude;
            float sum = e1 + e2 + e3;
            if (sum < 1e-12f) return 0f;
            return 2f * 1.7320508f * Vector3.Cross(r1 - r0, r2 - r0).magnitude / sum;
        }

        private static bool WouldFlip(MeshGraph g, Edge e, Vector3 p)
        {
            // Check surviving triangles adjacent to either endpoint.
            for (int side = 0; side < 2; side++)
            {
                var list = g.vertTris[side == 0 ? e.a : e.b];
                for (int i = 0; i < list.Count; i++)
                {
                    int t = list[i];
                    if (!g.triAlive[t]) continue;
                    if (TriHasVert(g, t, e.a) && TriHasVert(g, t, e.b)) continue; // degenerate, dies anyway
                    Vector3 c0 = g.TriCross(t);
                    Vector3 c1 = CrossWithOverride(g, t, e.a, e.b, p);
                    float m0 = c0.magnitude, m1 = c1.magnitude;
                    // Reject flips, slivers (area crushed to near zero) and
                    // disproportionate growth (a surviving triangle ballooning
                    // into a stretched spike across the mesh).
                    if (m1 < 1e-8f || m1 < m0 * 1e-3f) return true;
                    if (m1 > m0 * 4f) return true;
                    if (Vector3.Dot(c0, c1) < 0f) return true;
                    // Sliver guard: normalized shape factor 4*sqrt(3)*A / sum(e^2).
                    // Rejects long needle triangles that still pass the area tests.
                    float s0 = ShapeFactor(g, t, e.a, e.b, p, false);
                    float s1 = ShapeFactor(g, t, e.a, e.b, p, true);
                    if (s1 < 0.02f && s1 < s0 * 0.5f) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Link condition (Dey et al. / Hoppe): collapsing edge (a,b) keeps the
        /// mesh manifold iff every vertex adjacent to BOTH a and b is an apex of
        /// a triangle containing that edge. Violations would weld unrelated
        /// regions together or tear the surface.
        /// </summary>
        private static bool PassesLinkCondition(MeshGraph g, List<Edge>[] vertEdges, Edge e)
        {
            int a = e.a, b = e.b;

            // Apices: third vertex of every alive face containing both a and b.
            var apex = new HashSet<int>();
            var aTris = g.vertTris[a];
            for (int i = 0; i < aTris.Count; i++)
            {
                int t = aTris[i];
                if (!g.triAlive[t] || !TriHasVert(g, t, b)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = g.cornerVert[g.triCorners[t * 3 + k]];
                    if (v != a && v != b) apex.Add(v);
                }
            }

            // Every neighbor of b that is also a neighbor of a must be an apex.
            var bEdges = vertEdges[b];
            for (int i = 0; i < bEdges.Count; i++)
            {
                Edge be = bEdges[i];
                if (be.removed) continue;
                int x = be.a == b ? be.b : be.a;
                if (x == a || apex.Contains(x)) continue;
                if (IsNeighbor(vertEdges[a], x)) return false;
            }
            return true;
        }

        private static bool IsNeighbor(List<Edge> edges, int v)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                Edge e = edges[i];
                if (e.removed) continue;
                if (e.a == v || e.b == v) return true;
            }
            return false;
        }

        /// <summary>
        /// Boundary-loop topology guard:
        /// - interior edge between verts of DIFFERENT loops would merge two
        ///   openings into one;
        /// - interior edge between verts of the SAME loop would split one
        ///   opening into two;
        /// - a boundary edge may only collapse if its loop keeps >= 3 edges.
        /// </summary>
        private static bool ViolatesBoundaryTopology(MeshGraph g, List<Edge>[] vertEdges, Edge e)
        {
            if (!TouchesBoundary(vertEdges[e.a]) || !TouchesBoundary(vertEdges[e.b]))
                return false;

            // Walk the boundary-edge component containing a; find whether b is
            // on it and how many edges the component has.
            var seenVert = new HashSet<int>();
            var seenEdge = new HashSet<Edge>();
            var stack = new Stack<int>();
            stack.Push(e.a);
            seenVert.Add(e.a);
            bool reachedB = false;
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                var list = vertEdges[v];
                for (int i = 0; i < list.Count; i++)
                {
                    Edge be = list[i];
                    if (be.removed || !be.boundary) continue;
                    seenEdge.Add(be);
                    int o = be.a == v ? be.b : be.a;
                    if (o == e.b) reachedB = true;
                    if (seenVert.Add(o)) stack.Push(o);
                }
            }

            if (!reachedB) return true;                       // loop merge
            if (e.boundary) return seenEdge.Count <= 3;       // loop annihilation
            return true;                                      // loop split
        }

        private static bool TouchesBoundary(List<Edge> edges)
        {
            for (int i = 0; i < edges.Count; i++)
                if (!edges[i].removed && edges[i].boundary) return true;
            return false;
        }

        /// <summary>
        /// Boundary-loop map with hole classification. A boundary component is
        /// a hole when its polygon interior contains no source geometry
        /// (door/window openings); the outer silhouette contains the whole
        /// surface instead and must not be hole-guarded.
        /// </summary>
        private sealed class HoleInfo
        {
            public int[] vertLoop;   // boundary component id per vert, -1 none
            public bool[] isHole;
        }

        private static HoleInfo BuildHoleLoops(MeshGraph g)
        {
            int n = g.pos.Length;
            var vertLoop = new int[n];
            for (int i = 0; i < n; i++) vertLoop[i] = -1;

            // Union-find over boundary-edge endpoints.
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                int r = x;
                while (parent.TryGetValue(r, out int p) && p != r) r = p;
                int p2;
                while (parent.TryGetValue(x, out p2) && p2 != r) { parent[x] = r; x = p2; }
                return r;
            }
            void Union(int a, int b)
            {
                if (!parent.ContainsKey(a)) parent[a] = a;
                if (!parent.ContainsKey(b)) parent[b] = b;
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[rb] = ra;
            }
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;
                if (e.removed || !e.boundary) continue;
                Union(e.a, e.b);
            }

            // Group boundary verts per component.
            var comps = new Dictionary<int, List<int>>();
            var compId = new Dictionary<int, int>();
            var compSet = new Dictionary<int, HashSet<int>>();
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;
                if (e.removed || !e.boundary) continue;
                int r = Find(e.a);
                if (!compId.TryGetValue(r, out int id))
                {
                    id = comps.Count;
                    compId[r] = id;
                    comps[id] = new List<int>();
                    compSet[id] = new HashSet<int>();
                }
                if (compSet[id].Add(e.a)) comps[id].Add(e.a);
                if (compSet[id].Add(e.b)) comps[id].Add(e.b);
                if (vertLoop[e.a] < 0) vertLoop[e.a] = id;
                if (vertLoop[e.b] < 0) vertLoop[e.b] = id;
            }

            var isHole = new bool[comps.Count];
            foreach (var kv in comps)
            {
                var verts = kv.Value;
                if (verts.Count < 3) continue;
                var c = Vector3.zero;
                foreach (int v in verts) c += g.pos[v];
                c /= verts.Count;
                // Order-free plane estimate: the loop is flat along the axis
                // with the smallest positional variance -> drop that axis.
                float vx = 0f, vy = 0f, vz = 0f;
                foreach (int v in verts)
                {
                    Vector3 d = g.pos[v] - c;
                    vx += d.x * d.x; vy += d.y * d.y; vz += d.z * d.z;
                }
                int ax = vx <= vy && vx <= vz ? 0 : vy <= vz ? 1 : 2;
                Vector2 cent2 = Proj2(c, ax);
                // The loop bounds a hole if NO source triangle footprint
                // contains the centroid in this projection.
                bool covered = false;
                for (int t = 0; t < g.triAlive.Length && !covered; t++)
                {
                    if (!g.triAlive[t]) continue;
                    int c0 = g.triCorners[t * 3], c1 = g.triCorners[t * 3 + 1], c2 = g.triCorners[t * 3 + 2];
                    Vector3 p0 = g.pos[g.cornerVert[c0]], p1 = g.pos[g.cornerVert[c1]], p2 = g.pos[g.cornerVert[c2]];
                    // fast bbox reject in the projection plane
                    Vector2 a2 = Proj2(p0, ax), b2 = Proj2(p1, ax), d2 = Proj2(p2, ax);
                    float mnx = Mathf.Min(a2.x, Mathf.Min(b2.x, d2.x)), mxx = Mathf.Max(a2.x, Mathf.Max(b2.x, d2.x));
                    float mny = Mathf.Min(a2.y, Mathf.Min(b2.y, d2.y)), mxy = Mathf.Max(a2.y, Mathf.Max(b2.y, d2.y));
                    if (cent2.x < mnx || cent2.x > mxx || cent2.y < mny || cent2.y > mxy) continue;
                    float den = (b2.y - d2.y) * (a2.x - d2.x) + (d2.x - b2.x) * (a2.y - d2.y);
                    if (Mathf.Abs(den) < 1e-12f) continue;
                    float u = ((b2.y - d2.y) * (cent2.x - d2.x) + (d2.x - b2.x) * (cent2.y - d2.y)) / den;
                    float w = ((d2.y - a2.y) * (cent2.x - d2.x) + (a2.x - d2.x) * (cent2.y - d2.y)) / den;
                    float z = 1f - u - w;
                    if (u >= -0.001f && w >= -0.001f && z >= -0.001f) covered = true;
                }
                isHole[kv.Key] = !covered;
            }
            return new HoleInfo { vertLoop = vertLoop, isHole = isHole };
        }

        private static Vector2 Proj2(Vector3 p, int ax) =>
            ax == 0 ? new Vector2(p.y, p.z) : ax == 1 ? new Vector2(p.x, p.z) : new Vector2(p.x, p.y);

        /// <summary>
        /// Rejects collapses that would bridge a hole loop's interior:
        /// (a) interior edges between two verts of the same hole loop are
        /// chords across the opening; (b) repointing b's edges to a may create
        /// chord pairings to loop verts that aren't loop-adjacent to a.
        /// </summary>
        private static bool CreatesHoleChord(MeshGraph g, List<Edge>[] vertEdges, Edge e, HoleInfo info)
        {
            int la = info.vertLoop[e.a], lb = info.vertLoop[e.b];
            if (la >= 0 && la == lb && info.isHole[la] && !e.boundary) return true;
            int surv = la >= 0 ? la : lb;
            if (surv < 0 || !info.isHole[surv]) return false;
            var bEdges = vertEdges[e.b];
            for (int i = 0; i < bEdges.Count; i++)
            {
                Edge e2 = bEdges[i];
                if (e2.removed) continue;
                int x = e2.a == e.b ? e2.b : e2.a;
                if (x == e.a || info.vertLoop[x] != surv) continue;
                Edge ax = g.GetEdge(e.a, x);
                if (ax == null || ax.removed || !ax.boundary) return true;
            }
            return false;
        }

        private static void CommitCollapse(MeshGraph g, Quadric[] quadrics, List<Edge>[] vertEdges, Edge e, Vector3 p,
            int[] vertLoop)
        {
            int a = e.a, b = e.b;
            Vector3 oldA = g.pos[a];
            g.pos[a] = p;
            if (vertLoop[a] < 0) vertLoop[a] = vertLoop[b];

            // When the survivor moved, its own incident edges no longer coincide
            // with source edges - they are phantom now (b's edges are handled below).
            if ((p - oldA).sqrMagnitude > 1e-12f)
            {
                var aList = vertEdges[a];
                for (int i = 0; i < aList.Count; i++)
                    if (aList[i] != e) aList[i].phantom = true;
            }

            // 1. Migrate wedges of b to a (merge attribute-identical ones).
            List<int> bWedges = g.vertWedges[b];
            for (int i = 0; i < bWedges.Count; i++)
            {
                int wid = bWedges[i];
                Wedge w = g.wedges[wid];
                if (w.removed) continue;
                int match = FindMatchingWedge(g, a, w);
                if (match >= 0)
                {
                    w.alias = match;
                    w.removed = true;
                }
                else
                {
                    w.vertex = a;
                    g.vertWedges[a].Add(wid);
                }
            }
            bWedges.Clear();

            // 2. Kill triangles that contain both endpoints, repoint the rest.
            var aTris = new List<int>();
            foreach (int t in g.vertTris[b])
            {
                if (!g.triAlive[t]) continue;
                if (TriHasVert(g, t, a))
                {
                    g.RemoveTri(t);
                    continue;
                }
                for (int k = 0; k < 3; k++)
                {
                    int c = g.triCorners[t * 3 + k];
                    if (g.cornerVert[c] == b) g.cornerVert[c] = a;
                }
                aTris.Add(t);
            }
            foreach (int t in g.vertTris[a])
                if (g.triAlive[t]) aTris.Add(t);
            g.vertTris[a] = aTris;
            g.vertTris[b].Clear();

            // 3. Repoint edges of b; merge duplicates into existing (a,x) edges.
            // An inherited edge keeps "real" status only when a lands exactly on
            // b's old position (endpoint-b placement preserves edge geometry).
            bool atB = (p - g.pos[b]).sqrMagnitude < 1e-12f;
            var bEdges = new List<Edge>(vertEdges[b]);
            vertEdges[b].Clear();
            foreach (Edge e2 in bEdges)
            {
                if (e2.removed) continue;
                int other = e2.a == b ? e2.b : e2.a;
                g.edges.Remove(MeshGraph.EdgeKey(e2.a, e2.b));
                if (other == a)
                {
                    e2.removed = true;
                    vertEdges[a].Remove(e2);
                    continue;
                }
                Edge existing = g.GetEdge(a, other);
                if (existing != null && !existing.removed)
                {
                    foreach (int t in e2.tris)
                        if (g.triAlive[t] && !existing.tris.Contains(t)) existing.tris.Add(t);
                    existing.seam |= e2.seam;
                    e2.removed = true;
                    List<Edge> oe = vertEdges[other];
                    if (!oe.Contains(existing)) oe.Add(existing);
                    RefreshEdge(g, existing);
                }
                else
                {
                    e2.phantom |= !atB;
                    if (a < other) { e2.a = a; e2.b = other; } else { e2.a = other; e2.b = a; }
                    g.edges[MeshGraph.EdgeKey(e2.a, e2.b)] = e2;
                    vertEdges[a].Add(e2);
                    RefreshEdge(g, e2);
                }
            }

            // 4. Accumulate quadric + lock state.
            Quadric qa = quadrics[a];
            qa.Add(ref quadrics[b]);
            quadrics[a] = qa;
            g.locked[a] |= g.locked[b];

            e.removed = true;
            g.edges.Remove(MeshGraph.EdgeKey(a, b));

        }

        private static int FindMatchingWedge(MeshGraph g, int v, Wedge candidate)
        {
            List<int> owned = g.vertWedges[v];
            for (int i = 0; i < owned.Count; i++)
            {
                Wedge w = g.wedges[owned[i]];
                if (w.removed) continue;
                if ((w.normal - candidate.normal).sqrMagnitude < 1e-4f &&
                    (w.uv0 - candidate.uv0).sqrMagnitude < 1e-4f &&
                    (w.uv1 - candidate.uv1).sqrMagnitude < 1e-4f &&
                    (w.uv2 - candidate.uv2).sqrMagnitude < 1e-4f &&
                    (w.uv3 - candidate.uv3).sqrMagnitude < 1e-4f &&
                    w.color.r == candidate.color.r && w.color.g == candidate.color.g &&
                    w.color.b == candidate.color.b && w.color.a == candidate.color.a)
                    return owned[i];
            }
            return -1;
        }

        private static void RefreshEdge(MeshGraph g, Edge e)
        {
            for (int i = e.tris.Count - 1; i >= 0; i--)
                if (!g.triAlive[e.tris[i]]) e.tris.RemoveAt(i);
            e.boundary = e.tris.Count == 1;
            e.creaseAngle = 0f;
            if (e.tris.Count == 2)
            {
                Vector3 n0 = g.TriNormal(e.tris[0]);
                Vector3 n1 = g.TriNormal(e.tris[1]);
                e.creaseAngle = Vector3.Angle(n0, n1);
            }
        }

        // ------------------------------------------------------------------
        // Output
        // ------------------------------------------------------------------

        private static Mesh RebuildMesh(MeshGraph g, Mesh source, SimplifyOptions opt, out int outVerts)
        {
            bool hasN = source.normals != null && source.normals.Length == source.vertexCount;
            bool hasT = source.tangents != null && source.tangents.Length == source.vertexCount;
            bool hasC = source.colors32 != null && source.colors32.Length == source.vertexCount;
            bool hasU0 = source.uv != null && source.uv.Length == source.vertexCount;
            bool hasU1 = source.uv2 != null && source.uv2.Length == source.vertexCount;
            bool hasU2 = source.uv3 != null && source.uv3.Length == source.vertexCount;
            bool hasU3 = source.uv4 != null && source.uv4.Length == source.vertexCount;
            bool hasB = source.boneWeights != null && source.boneWeights.Length == source.vertexCount;

            var wedgeToOut = new Dictionary<int, int>();
            var outPos = new List<Vector3>();
            var outN = hasN ? new List<Vector3>() : null;
            var outT = hasT ? new List<Vector4>() : null;
            var outC = hasC ? new List<Color32>() : null;
            var outU0 = hasU0 ? new List<Vector2>() : null;
            var outU1 = hasU1 ? new List<Vector2>() : null;
            var outU2 = hasU2 ? new List<Vector2>() : null;
            var outU3 = hasU3 ? new List<Vector2>() : null;
            var outB = hasB ? new List<BoneWeight>() : null;

            var subTris = new List<int>[source.subMeshCount];
            for (int s = 0; s < subTris.Length; s++) subTris[s] = new List<int>();

            for (int t = 0; t < g.triSub.Length; t++)
            {
                if (!g.triAlive[t]) continue;
                int sub = g.triSub[t];
                for (int k = 0; k < 3; k++)
                {
                    int c = g.triCorners[t * 3 + k];
                    int w = g.ResolveWedge(g.cornerWedge[c]);
                    g.cornerWedge[c] = w;
                    if (g.wedges[w].vertex != g.cornerVert[c]) debugWedgeMismatch++;
                    int idx;
                    if (!wedgeToOut.TryGetValue(w, out idx))
                    {
                        idx = outPos.Count;
                        wedgeToOut.Add(w, idx);
                        Wedge wd = g.wedges[w];
                        outPos.Add(g.pos[wd.vertex]);
                        outN?.Add(wd.normal.normalized);
                        outT?.Add(wd.tangent);
                        outC?.Add(wd.color);
                        outU0?.Add(wd.uv0);
                        outU1?.Add(wd.uv1);
                        outU2?.Add(wd.uv2);
                        outU3?.Add(wd.uv3);
                        outB?.Add(wd.boneWeight);
                    }
                    subTris[sub].Add(idx);
                }
            }

            var mesh = new Mesh();
            mesh.name = source.name + "_simplified";
            if (outPos.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = outPos.ToArray();
            if (hasN && !opt.recalculateNormals) mesh.normals = outN.ToArray();
            if (hasT && !opt.recalculateNormals) mesh.tangents = outT.ToArray();
            if (hasC) mesh.colors32 = outC.ToArray();
            if (hasU0) mesh.uv = outU0.ToArray();
            if (hasU1) mesh.uv2 = outU1.ToArray();
            if (hasU2) mesh.uv3 = outU2.ToArray();
            if (hasU3) mesh.uv4 = outU3.ToArray();
            if (hasB) mesh.boneWeights = outB.ToArray();
            mesh.subMeshCount = source.subMeshCount;
            for (int s = 0; s < subTris.Length; s++)
                mesh.SetTriangles(subTris[s].ToArray(), s);
            if (hasB) mesh.bindposes = source.bindposes;
            if (opt.recalculateNormals)
            {
                mesh.RecalculateNormals();
                if (hasU0) mesh.RecalculateTangents();
            }
            mesh.RecalculateBounds();
            outVerts = outPos.Count;
            return mesh;
        }
    }
}
