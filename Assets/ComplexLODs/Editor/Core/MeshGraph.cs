using System.Collections.Generic;
using UnityEngine;

namespace ComplexLODs.Core
{
    /// <summary>
    /// One output vertex worth of attributes (a "corner attribute set").
    /// Several wedges can share the same welded vertex (UV/normal seams).
    /// </summary>
    internal sealed class Wedge
    {
        public int vertex;              // welded vertex that owns this wedge
        public int alias = -1;          // set when merged into another wedge
        public bool removed;
        public int srcVertex = -1;      // original vertex index in the source mesh

        public Vector3 normal;
        public Vector4 tangent;
        public Color32 color;
        public Vector2 uv0, uv1, uv2, uv3;
        public BoneWeight boneWeight;
    }

    internal sealed class Edge
    {
        public int a, b;
        public readonly List<int> tris = new List<int>(4);
        public bool removed;
        public int version;
        public bool boundary;
        public bool seam;
        public bool phantom;        // edge no longer coincides with a real source edge
        public float creaseAngle;
    }

    /// <summary>
    /// Position-welded mesh graph: welded vertices, corners, wedges,
    /// triangles and the undirected edge map used by the simplifier.
    /// </summary>
    internal sealed class MeshGraph
    {
        public Vector3[] pos;
        public bool[] locked;
        public float[] weight;
        public float[] localScale;    // mean incident edge length at build time

        public List<int>[] vertWedges;
        public List<int>[] vertTris;

        public Wedge[] wedges;

        // corner -> welded vertex / wedge
        public int[] cornerVert;
        public int[] cornerWedge;

        public int[] triCorners;    // 3 corner ids per triangle
        public int[] triSub;
        public bool[] triAlive;
        public int aliveTris;

        public readonly Dictionary<ulong, Edge> edges = new Dictionary<ulong, Edge>();

        public static ulong EdgeKey(int a, int b)
        {
            return a < b ? ((ulong)(uint)a << 32) | (uint)b : ((ulong)(uint)b << 32) | (uint)a;
        }

        public Edge GetEdge(int a, int b)
        {
            Edge e;
            edges.TryGetValue(EdgeKey(a, b), out e);
            return e;
        }

        public Vector3 TriNormal(int tri)
        {
            Vector3 n = TriCross(tri);
            float m = n.magnitude;
            return m > 1e-20f ? n / m : Vector3.zero;
        }

        /// <summary>Unnormalized face cross product (magnitude = 2 * area).</summary>
        public Vector3 TriCross(int tri)
        {
            int c0 = triCorners[tri * 3], c1 = triCorners[tri * 3 + 1], c2 = triCorners[tri * 3 + 2];
            Vector3 p0 = pos[cornerVert[c0]], p1 = pos[cornerVert[c1]], p2 = pos[cornerVert[c2]];
            return Vector3.Cross(p1 - p0, p2 - p0);
        }

        public void RemoveTri(int tri)
        {
            if (!triAlive[tri]) return;
            triAlive[tri] = false;
            aliveTris--;
        }

        public int ResolveWedge(int wedge)
        {
            int guard = 0;
            while (wedges[wedge].alias >= 0 && guard++ < 1024)
                wedge = wedges[wedge].alias;
            return wedge;
        }

        /// <summary>Number of connected components formed by boundary edges.</summary>
        public int CountBoundaryComponents()
        {
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                int r = x;
                while (parent.TryGetValue(r, out int p) && p != r) r = p;
                int p2;
                while (parent.TryGetValue(x, out p2) && p2 != r) { parent[x] = r; x = p2; }
                return r;
            }
            var roots = new HashSet<int>();
            foreach (var kv in edges)
            {
                Edge e = kv.Value;
                if (e.removed || !e.boundary) continue;
                if (!parent.ContainsKey(e.a)) parent[e.a] = e.a;
                if (!parent.ContainsKey(e.b)) parent[e.b] = e.b;
                int ra = Find(e.a), rb = Find(e.b);
                if (ra != rb) parent[rb] = ra;
            }
            foreach (var kv in edges)
            {
                Edge e = kv.Value;
                if (e.removed || !e.boundary) continue;
                roots.Add(Find(e.a));
            }
            return roots.Count;
        }

        // ------------------------------------------------------------------
        // Build
        // ------------------------------------------------------------------

        private const float AttrEps = 1e-4f;

        public static MeshGraph Build(Mesh mesh, float weldTolerance)
        {
            Vector3[] srcPos = mesh.vertices;
            var weld = WeldPositions(srcPos, weldTolerance);

            var g = new MeshGraph();
            int vCount = weld.Count;
            g.pos = new Vector3[vCount];
            foreach (var kv in weld) g.pos[kv.Value] = srcPos[kv.Key];

            var srcNormals = mesh.normals;
            var srcTangents = mesh.tangents;
            var srcColors = mesh.colors32;
            var srcUv = mesh.uv;
            var srcUv2 = mesh.uv2;
            var srcUv3 = mesh.uv3;
            var srcUv4 = mesh.uv4;
            var srcBone = mesh.boneWeights;

            bool hasN = srcNormals != null && srcNormals.Length == srcPos.Length;
            bool hasT = srcTangents != null && srcTangents.Length == srcPos.Length;
            bool hasC = srcColors != null && srcColors.Length == srcPos.Length;
            bool hasU0 = srcUv != null && srcUv.Length == srcPos.Length;
            bool hasU1 = srcUv2 != null && srcUv2.Length == srcPos.Length;
            bool hasU2 = srcUv3 != null && srcUv3.Length == srcPos.Length;
            bool hasU3 = srcUv4 != null && srcUv4.Length == srcPos.Length;
            bool hasB = srcBone != null && srcBone.Length == srcPos.Length;

            int triTotal = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) triTotal += mesh.GetTriangles(s).Length / 3;

            var cornerVert = new int[triTotal * 3];
            var cornerWedge = new int[triTotal * 3];
            var triCorners = new int[triTotal * 3];
            var triSub = new int[triTotal];

            var wedgeList = new List<Wedge>();
            g.vertWedges = new List<int>[vCount];
            g.vertTris = new List<int>[vCount];
            for (int i = 0; i < vCount; i++)
            {
                g.vertWedges[i] = new List<int>(2);
                g.vertTris[i] = new List<int>(8);
            }

            var weldLookup = new int[srcPos.Length];
            foreach (var kv in weld) weldLookup[kv.Key] = kv.Value;

            int triIndex = 0;
            int cornerIndex = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] idx = mesh.GetTriangles(s);
                for (int i = 0; i < idx.Length; i += 3)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int ov = idx[i + k];
                        int v = weldLookup[ov];
                        cornerVert[cornerIndex] = v;
                        cornerWedge[cornerIndex] = FindOrAddWedge(g, wedgeList, v, ov,
                            hasN ? srcNormals[ov] : Vector3.up,
                            hasT ? srcTangents[ov] : Vector4.zero,
                            hasC ? srcColors[ov] : new Color32(255, 255, 255, 255),
                            hasU0 ? srcUv[ov] : Vector2.zero,
                            hasU1 ? srcUv2[ov] : Vector2.zero,
                            hasU2 ? srcUv3[ov] : Vector2.zero,
                            hasU3 ? srcUv4[ov] : Vector2.zero,
                            hasB ? srcBone[ov] : new BoneWeight());
                        cornerIndex++;
                    }
                    triCorners[triIndex * 3] = cornerIndex - 3;
                    triCorners[triIndex * 3 + 1] = cornerIndex - 2;
                    triCorners[triIndex * 3 + 2] = cornerIndex - 1;
                    triSub[triIndex] = s;
                    triIndex++;
                }
            }

            g.wedges = wedgeList.ToArray();
            g.cornerVert = cornerVert;
            g.cornerWedge = cornerWedge;
            g.triCorners = triCorners;
            g.triSub = triSub;
            g.triAlive = new bool[triIndex];
            for (int i = 0; i < triIndex; i++) g.triAlive[i] = true;
            g.aliveTris = triIndex;
            g.locked = new bool[vCount];
            g.weight = new float[vCount];
            for (int i = 0; i < vCount; i++) g.weight[i] = 1f;

            // Edges + vertex adjacency
            for (int t = 0; t < triIndex; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int c = triCorners[t * 3 + k];
                    int v = cornerVert[c];
                    g.vertTris[v].Add(t);
                }
                for (int k = 0; k < 3; k++)
                {
                    int cA = triCorners[t * 3 + k];
                    int cB = triCorners[t * 3 + (k + 1) % 3];
                    int va = cornerVert[cA], vb = cornerVert[cB];
                    if (va == vb) continue;
                    ulong key = EdgeKey(va, vb);
                    Edge e;
                    if (!g.edges.TryGetValue(key, out e))
                    {
                        e = new Edge { a = Mathf.Min(va, vb), b = Mathf.Max(va, vb) };
                        g.edges.Add(key, e);
                    }
                    if (!e.tris.Contains(t)) e.tris.Add(t);
                }
            }

            // Preflight: drop zero-area source triangles so they cannot
            // propagate into every generated LOD.
            for (int t = 0; t < g.triAlive.Length; t++)
                if (g.TriCross(t).sqrMagnitude < 1e-12f) g.RemoveTri(t);

            // Classification
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;
                e.boundary = e.tris.Count == 1;
                if (e.tris.Count == 2)
                {
                    Vector3 n0 = g.TriNormal(e.tris[0]);
                    Vector3 n1 = g.TriNormal(e.tris[1]);
                    e.creaseAngle = Vector3.Angle(n0, n1);
                    e.seam = IsSeamEdge(g, e);
                }
            }

            // Per-vertex local feature scale: mean incident edge length.
            // Used to cap collapse distance so a vertex can never jump across
            // a pane/region far larger than its own neighbourhood.
            var eSum = new float[vCount];
            var eCnt = new int[vCount];
            float global = 0f; int gn = 0;
            foreach (var kv in g.edges)
            {
                Edge e = kv.Value;
                float l = (g.pos[e.a] - g.pos[e.b]).magnitude;
                eSum[e.a] += l; eCnt[e.a]++;
                eSum[e.b] += l; eCnt[e.b]++;
                global += l; gn++;
            }
            float gMean = gn > 0 ? global / gn : 1f;
            g.localScale = new float[vCount];
            for (int i = 0; i < vCount; i++)
                g.localScale[i] = eCnt[i] > 0 ? eSum[i] / eCnt[i] : gMean;

            return g;
        }

        private static bool IsSeamEdge(MeshGraph g, Edge e)
        {
            // For each endpoint, compare the wedge used in tri0 vs tri1.
            for (int k = 0; k < 2; k++)
            {
                int v = k == 0 ? e.a : e.b;
                int w0 = -1, w1 = -1;
                for (int c = 0; c < 3; c++)
                {
                    int corner = g.triCorners[e.tris[0] * 3 + c];
                    if (g.cornerVert[corner] == v) w0 = g.cornerWedge[corner];
                    corner = g.triCorners[e.tris[1] * 3 + c];
                    if (g.cornerVert[corner] == v) w1 = g.cornerWedge[corner];
                }
                if (w0 >= 0 && w1 >= 0 && w0 != w1) return true;
            }
            return false;
        }

        private static int FindOrAddWedge(MeshGraph g, List<Wedge> wedgeList, int v, int ov,
            Vector3 n, Vector4 t, Color32 c, Vector2 u0, Vector2 u1, Vector2 u2, Vector2 u3, BoneWeight bw)
        {
            List<int> owned = g.vertWedges[v];
            for (int i = 0; i < owned.Count; i++)
            {
                Wedge w = wedgeList[owned[i]];
                if (Approx(w.normal, n) && Approx(w.tangent, t) &&
                    Approx(w.uv0, u0) && Approx(w.uv1, u1) && Approx(w.uv2, u2) && Approx(w.uv3, u3) &&
                    w.color.r == c.r && w.color.g == c.g && w.color.b == c.b && w.color.a == c.a &&
                    Approx(w.boneWeight, bw))
                {
                    return owned[i];
                }
            }
            var nw = new Wedge
            {
                vertex = v, normal = n, tangent = t, color = c,
                uv0 = u0, uv1 = u1, uv2 = u2, uv3 = u3, boneWeight = bw,
                srcVertex = ov
            };
            wedgeList.Add(nw);
            owned.Add(wedgeList.Count - 1);
            return wedgeList.Count - 1;
        }

        private static bool Approx(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < AttrEps;
        private static bool Approx(Vector4 a, Vector4 b) => (a - b).sqrMagnitude < AttrEps;
        private static bool Approx(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < AttrEps;
        private static bool Approx(BoneWeight a, BoneWeight b)
        {
            return Mathf.Abs(a.weight0 - b.weight0) < AttrEps &&
                   Mathf.Abs(a.weight1 - b.weight1) < AttrEps &&
                   Mathf.Abs(a.weight2 - b.weight2) < AttrEps &&
                   Mathf.Abs(a.weight3 - b.weight3) < AttrEps &&
                   a.boneIndex0 == b.boneIndex0 && a.boneIndex1 == b.boneIndex1 &&
                   a.boneIndex2 == b.boneIndex2 && a.boneIndex3 == b.boneIndex3;
        }

        private static Dictionary<int, int> WeldPositions(Vector3[] src, float tolerance)
        {
            var map = new Dictionary<int, int>(src.Length);
            if (tolerance <= 0f)
            {
                var exact = new Dictionary<Vector3, int>(src.Length);
                for (int i = 0; i < src.Length; i++)
                {
                    int id;
                    if (!exact.TryGetValue(src[i], out id))
                    {
                        id = exact.Count;
                        exact.Add(src[i], id);
                    }
                    map[i] = id;
                }
            }
            else
            {
                var grid = new Dictionary<Vector3Int, List<int>>();
                var welded = new List<Vector3>();
                for (int i = 0; i < src.Length; i++)
                {
                    Vector3 p = src[i];
                    Vector3Int cell = new Vector3Int(
                        Mathf.FloorToInt(p.x / tolerance),
                        Mathf.FloorToInt(p.y / tolerance),
                        Mathf.FloorToInt(p.z / tolerance));
                    int found = -1;
                    float best = tolerance * tolerance;
                    for (int dx = -1; dx <= 1 && found < 0; dx++)
                    for (int dy = -1; dy <= 1 && found < 0; dy++)
                    for (int dz = -1; dz <= 1 && found < 0; dz++)
                    {
                        List<int> bucket;
                        if (!grid.TryGetValue(cell + new Vector3Int(dx, dy, dz), out bucket)) continue;
                        for (int k = 0; k < bucket.Count; k++)
                        {
                            float d = (welded[bucket[k]] - p).sqrMagnitude;
                            if (d <= best) { best = d; found = bucket[k]; }
                        }
                    }
                    if (found < 0)
                    {
                        found = welded.Count;
                        welded.Add(p);
                        List<int> bucket;
                        if (!grid.TryGetValue(cell, out bucket))
                        {
                            bucket = new List<int>();
                            grid.Add(cell, bucket);
                        }
                        bucket.Add(found);
                    }
                    map[i] = found;
                }
            }
            return map;
        }
    }
}
