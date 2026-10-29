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

        // Debug counters: which guard rejected the last heap pops.
        public static readonly long[] Reject = new long[8];
        // 0 bothLocked 1 noPlacement(feature/attr) 2 distance 3 flip 4 link 5 boundary

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

            // Exterior shell: vertices on the 2D convex hull of some viewing
            // projection form the model's outline. They are locked for the
            // whole run so the staged interior release below can sacrifice
            // detail without ever moving the outer bounds.
            bool[] silhouette = MarkSilhouette(g);

            // Interior classification: anything that cannot be seen from any
            // outside view is interior. Interior vertices collapse under fully
            // relaxed rules and interior-interior edges are cheap, so hidden
            // detail is consumed before the visible shell is touched. With
            // stripInterior the fully-interior triangles are deleted outright;
            // the cut rim becomes boundary edges, which the feature/boundary
            // machinery below then protects like any other opening.
            bool[] interior = null;
            if (opt.interiorRemoval)
            {
                bool[] exterior = Visibility.ExteriorVertices(g, out _);
                interior = new bool[g.pos.Length];
                int intVerts = 0;
                for (int i = 0; i < interior.Length; i++)
                {
                    interior[i] = !exterior[i] && !silhouette[i];
                    if (interior[i]) intVerts++;
                }
                res.interiorVertices = intVerts;
                for (int t = 0; t < g.triSub.Length; t++)
                {
                    if (!g.triAlive[t]) continue;
                    if (interior[g.cornerVert[g.triCorners[t * 3]]] &&
                        interior[g.cornerVert[g.triCorners[t * 3 + 1]]] &&
                        interior[g.cornerVert[g.triCorners[t * 3 + 2]]])
                        res.interiorTriangles++;
                }
                if (opt.stripInterior)
                {
                    for (int t = 0; t < g.triSub.Length; t++)
                    {
                        if (!g.triAlive[t]) continue;
                        int v0 = g.cornerVert[g.triCorners[t * 3]];
                        int v1 = g.cornerVert[g.triCorners[t * 3 + 1]];
                        int v2 = g.cornerVert[g.triCorners[t * 3 + 2]];
                        if (!interior[v0] || !interior[v1] || !interior[v2]) continue;
                        g.RemoveTri(t);
                        res.strippedInteriorTriangles++;
                    }
                    if (res.strippedInteriorTriangles > 0)
                    {
                        foreach (var kv in g.edges)
                        {
                            Edge e = kv.Value;
                            for (int i = e.tris.Count - 1; i >= 0; i--)
                                if (!g.triAlive[e.tris[i]]) e.tris.RemoveAt(i);
                            if (e.tris.Count == 0) e.removed = true;
                            else e.boundary = e.tris.Count == 1;
                        }
                    }
                }
            }

            var vertEdges = BuildVertexEdges(g);
            ApplyVertexWeightsAndLocks(g, source, opt);

            // Feature curves - boundary, sharp crease, attribute-seam,
            // submesh-change and non-manifold edges - may only collapse along
            // the chain itself; corners and junctions stay in place.
            // Non-manifold endpoints are locked outright.
            foreach (var kv in g.edges)
            {
                Edge edge = kv.Value;
                if (edge.removed) continue;
                edge.feature = edge.boundary || edge.seam || edge.tris.Count > 2 ||
                    edge.creaseAngle >= opt.creaseAngle ||
                    (edge.tris.Count == 2 && g.triSub[edge.tris[0]] != g.triSub[edge.tris[1]]);
                if (edge.tris.Count > 2)
                    g.locked[edge.a] = g.locked[edge.b] = true;
            }

            // Silhouette verts stay locked for the whole run: the outer
            // bounds can never move, no matter how deep the target is.
            for (int i = 0; i < silhouette.Length; i++)
                if (silhouette[i]) g.locked[i] = true;

            var quadrics = BuildQuadrics(g, opt);
            var heap = new MinHeap();
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;
                if (e.removed) continue;
                double cost = EdgeCost(g, quadrics, vertEdges, e, opt, silhouette, interior, 0, out _);
                if (!double.IsInfinity(cost)) heap.Push(cost, e, e.version);
            }
            int target = Mathf.Max(0, opt.targetTriangleCount);

            // Interior-release ladder, capped by how deep the target is. When
            // the heap stalls above target the next stage unlocks interior
            // (non-silhouette) detail; silhouette verts stay locked at every
            // stage, and flips/slivers/growth, the link condition, attribute
            // charts and the collapse-distance bound are never relaxed.
            //   0 - full preservation: feature verts move only along collinear chains
            //   1 - interior verts may collapse along their feature chain, corners too
            //   2 - interior verts move freely (seams/creases may be consumed)
            //   3 - interior boundary loops may additionally dissolve
            // With interior classification the ladder relaxes only the
            // exterior: interior freedom and interior-loop dissolution are
            // active from the start; stage 1 lets exterior verts collapse
            // along their feature chains past corners; stage 2 frees exterior
            // verts to endpoint/midpoint placements like the legacy ladder -
            // but boundary loops touching exterior verts never dissolve (the
            // legacy stage-3 could erase whole openings), and the interior
            // cost bias keeps hidden detail first in line at every stage.
            float depth = (float)target / Mathf.Max(1, g.aliveTris);
            int maxStage = interior != null
                ? (depth >= 0.75f ? 0 : depth >= 0.5f ? 1 : depth >= 0.25f ? 2 : 3)
                : (depth >= 0.75f ? 0 : depth >= 0.5f ? 1 : depth >= 0.25f ? 2 : 3);
            // Deepest-stage threshold: exterior openings whose loop fits
            // inside this length may dissolve; larger openings never do.
            float openingLimit = 0f;
            if (interior != null && opt.dissolveOpeningBelow > 0f)
            {
                var mn = g.pos[0]; var mx = g.pos[0];
                for (int i = 1; i < g.pos.Length; i++)
                {
                    Vector3 p = g.pos[i];
                    mn.x = Mathf.Min(mn.x, p.x); mn.y = Mathf.Min(mn.y, p.y); mn.z = Mathf.Min(mn.z, p.z);
                    mx.x = Mathf.Max(mx.x, p.x); mx.y = Mathf.Max(mx.y, p.y); mx.z = Mathf.Max(mx.z, p.z);
                }
                openingLimit = opt.dissolveOpeningBelow * (mx - mn).magnitude;
            }
            int stage = 0;
            int collapses = 0, iteration = 0;
            double errSum = 0, errMax = 0;
            for (;;)
            {
                while (heap.Count > 0 && g.aliveTris > target)
                {
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
                    double cost = EdgeCost(g, quadrics, vertEdges, e, opt, silhouette, interior, stage, out Vector3 p);
                    if (double.IsInfinity(cost)) continue;
                    float limit = MaxCollapseDistance * Mathf.Max(g.localScale[e.a], g.localScale[e.b]);
                    if ((g.pos[e.a] - g.pos[e.b]).magnitude > limit) { Reject[2]++; continue; }
                    if (opt.preventFlips && WouldFlip(g, e, p)) { Reject[3]++; continue; }
                    if (opt.preserveTopology && !PassesLinkCondition(g, vertEdges, e)) { Reject[4]++; continue; }
                    if (opt.preserveBoundaryLoops && ViolatesBoundaryTopology(g, vertEdges, e, silhouette, interior, stage, openingLimit)) { Reject[5]++; continue; }
                    // Component floor: a component may shrink to a legal stub
                    // but never below its floor, so thin parts degrade rather
                    // than vanish.
                    if (opt.componentMinTris > 0)
                    {
                        int c = g.vertComp[e.a], killed = 0;
                        for (int i = 0; i < e.tris.Count; i++)
                            if (g.triAlive[e.tris[i]]) killed++;
                        int floor = Math.Min(opt.componentMinTris, g.compOrig[c]);
                        if (g.compTris[c] - killed < floor) { Reject[6]++; continue; }
                    }
                    CommitCollapse(g, quadrics, vertEdges, e, p, stage);
                    collapses++;
                    double errorSquared = Math.Max(0.0, cost);
                    errSum += errorSquared;
                    errMax = Math.Max(errMax, Math.Sqrt(errorSquared));
                    var incident = vertEdges[e.a];
                    for (int i = incident.Count - 1; i >= 0; i--)
                    {
                        Edge next = incident[i];
                        if (next.removed) { incident.RemoveAt(i); continue; }
                        next.version++;
                        double nextCost = EdgeCost(g, quadrics, vertEdges, next, opt, silhouette, interior, stage, out _);
                        if (!double.IsInfinity(nextCost)) heap.Push(nextCost, next, next.version);
                    }
                }

                // Stalled above the target: open the next release stage and
                // re-offer every surviving edge under the relaxed rules.
                if (res.cancelled || g.aliveTris <= target || stage >= maxStage) break;
                stage++;
                foreach (var kv in g.edges)
                {
                    Edge e = kv.Value;
                    if (e.removed) continue;
                    e.version++;
                    double c = EdgeCost(g, quadrics, vertEdges, e, opt, silhouette, interior, stage, out _);
                    if (!double.IsInfinity(c)) heap.Push(c, e, e.version);
                }
            }
            res.releaseStage = stage;

            if (!res.cancelled && g.aliveTris > target)
                res.warning = string.IsNullOrEmpty(res.warning)
                    ? "Stopped above the triangle target to preserve the exterior bounds and feature topology."
                    : res.warning + " Stopped above the triangle target to preserve the exterior bounds and feature topology.";

            res.collapses = collapses;
            res.maxError = (float)errMax;
            res.rmsError = collapses > 0 ? (float)Math.Sqrt(errSum / collapses) : 0f;
            res.resultTriangles = g.aliveTris;
            res.reachedTarget = g.aliveTris <= target;
            res.resultBoundaryComponents = g.CountBoundaryComponents();

            res.mesh = RebuildMesh(g, source, opt, out int outVerts);
            res.resultVertices = outVerts;
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
                if (e.removed) continue;
                ve[e.a].Add(e);
                ve[e.b].Add(e);
            }
            return ve;
        }

        private static void ApplyVertexWeightsAndLocks(MeshGraph g, Mesh source, SimplifyOptions opt)
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
        }

        // Projection directions for silhouette marking: the six axes plus the
        // eight body diagonals cover the outline from every cardinal view.
        private static readonly Vector3[] SilhouetteDirections = {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down,
            Vector3.forward, Vector3.back,
            new Vector3(1, 1, 1), new Vector3(1, 1, -1),
            new Vector3(1, -1, 1), new Vector3(1, -1, -1),
            new Vector3(-1, 1, 1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(-1, -1, -1)
        };

        private struct ProjPoint { public float x, y; public int index; }

        private static double Cross2D(ProjPoint o, ProjPoint a, ProjPoint b)
        {
            return (double)(a.x - o.x) * (b.y - o.y) - (double)(a.y - o.y) * (b.x - o.x);
        }

        /// <summary>
        /// A vertex lying on the 2D convex hull of the mesh projected along any
        /// viewing direction contributes to the visible outline from that
        /// direction. Collinear hull-edge points are redundant: with the hull
        /// corners locked, boundary edges may still shorten along the outline
        /// but the outline itself cannot move.
        /// </summary>
        private static bool[] MarkSilhouette(MeshGraph g)
        {
            int n = g.pos.Length;
            var silhouette = new bool[n];
            var pts = new ProjPoint[n];
            foreach (Vector3 raw in SilhouetteDirections)
            {
                Vector3 d = raw.normalized;
                Vector3 u = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 v = Vector3.Cross(d, u);
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = g.pos[i];
                    pts[i].x = Vector3.Dot(p, u);
                    pts[i].y = Vector3.Dot(p, v);
                    pts[i].index = i;
                }
                Array.Sort(pts, (a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));

                // Andrew monotone chain - drops collinear hull-edge points.
                var hull = new List<int>(64);
                for (int i = 0; i < n; i++)
                {
                    while (hull.Count >= 2 &&
                        Cross2D(pts[hull[hull.Count - 2]], pts[hull[hull.Count - 1]], pts[i]) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(i);
                }
                int lower = hull.Count;
                for (int i = n - 2; i >= 0; i--)
                {
                    while (hull.Count > lower &&
                        Cross2D(pts[hull[hull.Count - 2]], pts[hull[hull.Count - 1]], pts[i]) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(i);
                }
                for (int i = 0; i < hull.Count; i++) silhouette[pts[hull[i]].index] = true;
            }
            return silhouette;
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
                if (!g.triAlive[t]) continue;
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
                if (e.removed || e.tris.Count == 0) continue;

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

        private static double EdgeCost(MeshGraph g, Quadric[] quadrics,
            List<Edge>[] vertEdges, Edge e, SimplifyOptions opt,
            bool[] silhouette, bool[] interior, int stage, out Vector3 best)
        {
            best = Vector3.zero;
            bool la = g.locked[e.a], lb = g.locked[e.b];
            if (la && lb) { Reject[0]++; return double.PositiveInfinity; }
            Quadric q = quadrics[e.a];
            q.Add(ref quadrics[e.b]);
            Vector3 pa = g.pos[e.a], pb = g.pos[e.b], chosen = Vector3.zero;
            double cost = double.PositiveInfinity;
            void Consider(Vector3 p)
            {
                if (!PreservesFeatureVertex(g, vertEdges, e.a, e.b, p, silhouette, interior, stage) ||
                    !PreservesFeatureVertex(g, vertEdges, e.b, e.a, p, silhouette, interior, stage) ||
                    !CanMoveAttributes(g, e, p, stage)) return;
                double c = q.Eval(p);
                if (double.IsNaN(c) || double.IsInfinity(c)) return;
                if (c < cost) { cost = c; chosen = p; }
            }
            if (!lb) Consider(pa);
            if (!la) Consider(pb);
            if (!la && !lb && !e.phantom) Consider((pa + pb) * .5f);
            if (double.IsInfinity(cost)) Reject[1]++;
            best = chosen;
            // Pure-interior collapses cannot move or delete anything visible
            // from outside - bias them so hidden detail is consumed first.
            if (interior != null && interior[e.a] && interior[e.b])
                cost *= opt.interiorCostBias;
            return cost;
        }

        // A vertex on a feature curve may only move along the chain itself:
        // endpoint placement on its collinear feature neighbour. Corners and
        // junctions (non-collinear or 3+ feature edges) never move.
        // With interior classification the distinction is exact: interior
        // vertices are free at every stage. Exterior vertices keep the strict
        // chain rule at stage 0 and may collapse along their feature chain
        // past corners/junctions at stage 1 - the chain itself is followed,
        // so openings and frames shrink along their own curves instead of
        // being torn. Free off-chain movement (the old stage 2, which ate
        // roofs and small attached parts) is never granted to exterior
        // vertices. Without classification the legacy ladder relaxes
        // non-silhouette vertices: stage 1 chain-past-corners, stage 2 free.
        private static bool PreservesFeatureVertex(MeshGraph g, List<Edge>[] vertEdges,
            int vertex, int other, Vector3 p, bool[] silhouette, bool[] interior, int stage)
        {
            if (p.Equals(g.pos[vertex])) return true;
            bool shell = silhouette[vertex];
            if ((interior != null && interior[vertex]) || (!shell && stage >= 2)) return true;
            int count = 0;
            Vector3 first = Vector3.zero, second = Vector3.zero;
            bool alongFeature = false;
            foreach (Edge edge in vertEdges[vertex])
            {
                if (edge.removed || !edge.feature) continue;
                int neighbor = edge.a == vertex ? edge.b : edge.a;
                alongFeature |= neighbor == other;
                if (++count == 1) first = g.pos[neighbor] - g.pos[vertex];
                else if (count == 2) second = g.pos[neighbor] - g.pos[vertex];
            }
            if (count == 0) return true;
            if (!alongFeature || !p.Equals(g.pos[other])) return false;
            if (!shell && stage >= 1) return true;
            if (count != 2) return false; // shell corner/junction stays
            double product = (double)first.sqrMagnitude * second.sqrMagnitude;
            return product > 0 && Vector3.Dot(first, second) < 0 &&
                Vector3.Cross(first, second).sqrMagnitude <= product * 1e-10;
        }

        // ------------------------------------------------------------------
        // Attribute transport
        // ------------------------------------------------------------------

        private static HashSet<int> UsedWedges(MeshGraph g, int vertex)
        {
            var used = new HashSet<int>();
            foreach (int t in g.vertTris[vertex])
            {
                if (!g.triAlive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int c = g.triCorners[t * 3 + k];
                    if (g.cornerVert[c] == vertex)
                        used.Add(g.ResolveWedge(g.cornerWedge[c]));
                }
            }
            return used;
        }

        private static int PartnerWedge(MeshGraph g, Edge e, int vertex, int wedge)
        {
            int partner = -1, other = vertex == e.a ? e.b : e.a;
            foreach (int t in e.tris)
            {
                if (!g.triAlive[t]) continue;
                bool hasWedge = false;
                int candidate = -1;
                for (int k = 0; k < 3; k++)
                {
                    int c = g.triCorners[t * 3 + k];
                    int w = g.ResolveWedge(g.cornerWedge[c]);
                    if (g.cornerVert[c] == vertex && w == wedge) hasWedge = true;
                    if (g.cornerVert[c] == other) candidate = w;
                }
                if (!hasWedge || candidate < 0) continue;
                if (partner >= 0 && !MeshGraph.AttributesEqual(g.wedges[partner], g.wedges[candidate]))
                    return -1;
                partner = candidate;
            }
            return partner;
        }

        private static bool CanMoveAttributes(MeshGraph g, Edge e, Vector3 p, int stage)
        {
            for (int side = 0; side < 2; side++)
            {
                int v = side == 0 ? e.a : e.b;
                if (p.Equals(g.pos[v])) continue;
                foreach (int wid in UsedWedges(g, v))
                {
                    int partner = PartnerWedge(g, e, v, wid);
                    // At deep stages a wedge without an edge partner no longer
                    // vetoes the collapse: it keeps its own attributes (a tiny
                    // local seam artifact, invisible at LOD distance). Bone
                    // weights and tangent handedness always stay strict.
                    if (partner < 0)
                    {
                        if (stage >= 2) continue;
                        return false;
                    }
                    Wedge a = g.wedges[wid], b = g.wedges[partner];
                    if (!a.boneWeight.Equals(b.boneWeight) || a.tangent.w != b.tangent.w) return false;
                }
            }
            return true;
        }

        private static void MoveAttributes(MeshGraph g, Edge e, Vector3 p, int stage)
        {
            Vector3 edge = g.pos[e.b] - g.pos[e.a];
            float t = edge.sqrMagnitude > 0 ?
                Mathf.Clamp01(Vector3.Dot(p - g.pos[e.a], edge) / edge.sqrMagnitude) : 0;
            var changes = new List<(Wedge destination, Wedge value)>();
            for (int side = 0; side < 2; side++)
            {
                int v = side == 0 ? e.a : e.b;
                if (p.Equals(g.pos[v])) continue;
                foreach (int wid in UsedWedges(g, v))
                {
                    int partner = PartnerWedge(g, e, v, wid);
                    // Missing partner is allowed from stage 2 (deep targets):
                    // the wedge keeps its own attributes instead of vetoing.
                    if (partner < 0)
                    {
                        if (stage >= 2) continue;
                        throw new InvalidOperationException("Missing edge attribute chart");
                    }
                    Wedge a = g.wedges[side == 0 ? wid : partner];
                    Wedge b = g.wedges[side == 0 ? partner : wid];
                    Vector4 tangent = Vector4.Lerp(a.tangent, b.tangent, t);
                    Vector3 direction = new Vector3(tangent.x, tangent.y, tangent.z).normalized;
                    changes.Add((g.wedges[wid], new Wedge {
                        normal = Vector3.Lerp(a.normal, b.normal, t).normalized,
                        tangent = new Vector4(direction.x, direction.y, direction.z, a.tangent.w),
                        uv0 = Vector2.Lerp(a.uv0,b.uv0,t), uv1 = Vector2.Lerp(a.uv1,b.uv1,t),
                        uv2 = Vector2.Lerp(a.uv2,b.uv2,t), uv3 = Vector2.Lerp(a.uv3,b.uv3,t),
                        color = Color32.Lerp(a.color,b.color,t), boneWeight = a.boneWeight
                    }));
                }
            }
            foreach (var update in changes)
            {
                Wedge d = update.destination, s = update.value;
                d.normal=s.normal; d.tangent=s.tangent; d.uv0=s.uv0; d.uv1=s.uv1;
                d.uv2=s.uv2; d.uv3=s.uv3; d.color=s.color; d.boneWeight=s.boneWeight;
            }
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

            // Reject collapses that leave two surviving faces on the same
            // vertex pair (a tetrahedron degrading to a double-sided fin).
            var faces = new HashSet<ulong>();
            var visited = new HashSet<int>();
            for (int side = 0; side < 2; side++)
            {
                foreach (int t in g.vertTris[side == 0 ? a : b])
                {
                    if (!g.triAlive[t] || !visited.Add(t)) continue;
                    if (TriHasVert(g, t, a) && TriHasVert(g, t, b)) continue;
                    int x = -1, y = -1;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = g.cornerVert[g.triCorners[t * 3 + k]];
                        if (v == a || v == b) continue;
                        if (x < 0) x = v; else y = v;
                    }
                    if (x >= 0 && y >= 0 && !faces.Add(MeshGraph.EdgeKey(x, y))) return false;
                }
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
        private static bool ViolatesBoundaryTopology(MeshGraph g, List<Edge>[] vertEdges, Edge e,
            bool[] silhouette, bool[] interior, int stage, float openingLimit)
        {
            if (!TouchesBoundary(vertEdges[e.a]) || !TouchesBoundary(vertEdges[e.b]))
                return false;

            // Walk the boundary-edge component containing a; find whether b is
            // on it, how many edges the component has and whether it touches
            // anything visible from outside (silhouette or exterior surface).
            var seenVert = new HashSet<int>();
            var seenEdge = new HashSet<Edge>();
            var stack = new Stack<int>();
            stack.Push(e.a);
            seenVert.Add(e.a);
            bool ExteriorVert(int v) => silhouette[v] || (interior != null && !interior[v]);
            bool reachedB = false, onShell = ExteriorVert(e.a) || ExteriorVert(e.b);
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                onShell |= ExteriorVert(v);
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

            // A boundary component touching nothing visible from outside is a
            // fully interior opening - it may shrink away. With interior
            // classification that is allowed immediately; otherwise it waits
            // for the deepest release stage.
            if (!onShell && (interior != null || stage >= 3)) return false;

            // Deepest stage: small exterior openings (portholes, railing
            // gaps) whose whole loop fits under the size limit may dissolve;
            // larger openings stay protected forever.
            if (onShell && stage >= 3 && interior != null && openingLimit > 0f)
            {
                var mn = g.pos[e.a]; var mx = g.pos[e.a];
                foreach (int v in seenVert)
                {
                    Vector3 p = g.pos[v];
                    mn.x = Mathf.Min(mn.x, p.x); mn.y = Mathf.Min(mn.y, p.y); mn.z = Mathf.Min(mn.z, p.z);
                    mx.x = Mathf.Max(mx.x, p.x); mx.y = Mathf.Max(mx.y, p.y); mx.z = Mathf.Max(mx.z, p.z);
                }
                if ((mx - mn).magnitude < openingLimit) return false;
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

        private static void CommitCollapse(MeshGraph g, Quadric[] quadrics, List<Edge>[] vertEdges, Edge e, Vector3 p, int stage)
        {
            int a = e.a, b = e.b;
            // Transport attributes along the source edge chart before any
            // position or wedge changes below.
            MoveAttributes(g, e, p, stage);
            Vector3 oldA = g.pos[a];
            g.pos[a] = p;

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
                    existing.feature |= e2.feature;
                    e2.removed = true;
                    List<Edge> oe = vertEdges[other];
                    if (!oe.Contains(existing)) oe.Add(existing);
                }
                else
                {
                    e2.phantom |= !atB;
                    if (a < other) { e2.a = a; e2.b = other; } else { e2.a = other; e2.b = a; }
                    g.edges[MeshGraph.EdgeKey(e2.a, e2.b)] = e2;
                    vertEdges[a].Add(e2);
                }
            }
            foreach (Edge incident in vertEdges[a])
            {
                if (incident.removed || incident == e) continue;
                RefreshEdge(g, incident);
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
                if (MeshGraph.AttributesEqual(w, candidate))
                    return owned[i];
            }
            return -1;
        }

        private static void RefreshEdge(MeshGraph g, Edge e)
        {
            for (int i = e.tris.Count - 1; i >= 0; i--)
                if (!g.triAlive[e.tris[i]]) e.tris.RemoveAt(i);
            if (e.tris.Count == 0) { e.removed = true; return; }
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
