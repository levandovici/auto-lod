// Temporary Unity regression checks, driven by Logs/MegaCityRegressions.request.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComplexLODs;
using ComplexLODs.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

[InitializeOnLoad]
internal static class MegaCityRegressions
{
    static MegaCityRegressions() { EditorApplication.delayCall += Run; }

    // Batch entry point: create Logs/MegaCityRegressions.request, then run
    // Unity.exe -batchmode -projectPath <proj> -executeMethod MegaCityRegressions.RunBatch -quit
    public static void RunBatch() { Run(); }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static Mesh Make(List<Vector3> positions, List<int> indices)
    {
        var m = new Mesh { vertices = positions.ToArray(), triangles = indices.ToArray() };
        m.RecalculateNormals();
        m.uv = positions.Select(p => new Vector2(p.x, p.y)).ToArray();
        return m;
    }
    private static Mesh Grid(int count, bool hole)
    {
        var v = new List<Vector3>();
        var f = new List<int>();
        for (int y = 0; y <= count; y++)
            for (int x = 0; x <= count; x++) v.Add(new Vector3(x, y, 0));
        for (int y = 0; y < count; y++)
            for (int x = 0; x < count; x++)
            {
                if (hole && x >= count / 3 && x < 2 * count / 3 && y >= count / 3 && y < 2 * count / 3) continue;
                int a = y * (count + 1) + x, b = a + 1, c = a + count + 1, d = c + 1;
                f.AddRange(new[] { a, b, c, b, d, c });
            }
        return Make(v, f);
    }
    private static List<(Vector3, Vector3)> Boundary(Mesh mesh)
    {
        var ids = new Dictionary<Vector3, int>();
        var v = mesh.vertices;
        int[] weld = v.Select(p => { if (!ids.ContainsKey(p)) ids[p] = ids.Count; return ids[p]; }).ToArray();
        var edges = new Dictionary<(int, int), int>();
        int[] f = mesh.triangles;
        for (int t = 0; t < f.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = weld[f[t + k]], b = weld[f[t + (k + 1) % 3]];
                var key = a < b ? (a, b) : (b, a);
                edges.TryGetValue(key, out int n);
                edges[key] = n + 1;
            }
        var pById = new Vector3[ids.Count];
        foreach (var kv in ids) pById[kv.Value] = kv.Key;
        return edges.Where(e => e.Value == 1).Select(e => (pById[e.Key.Item1], pById[e.Key.Item2])).ToList();
    }
    private static float Distance(Vector3 p, Vector3 a, Vector3 b)
    {
        float t = Mathf.Clamp01(Vector3.Dot(p - a, b - a) / (b - a).sqrMagnitude);
        return (p - Vector3.Lerp(a, b, t)).magnitude;
    }
    private static float Area(Mesh mesh)
    {
        var p = mesh.vertices; var f = mesh.triangles; float area = 0;
        for (int i = 0; i < f.Length; i += 3)
            area += Vector3.Cross(p[f[i + 1]] - p[f[i]], p[f[i + 2]] - p[f[i]]).magnitude / 2;
        return area;
    }
    private static void Run()
    {
        const string request = "Logs/MegaCityRegressions.request";
        if (!File.Exists(request)) return;
        File.Delete(request);
        var results = new List<string>();
        void Test(string name, Action action)
        {
            try { action(); results.Add("PASS " + name); }
            catch (Exception e) { results.Add("FAIL " + name + ": " + e.Message); }
        }
        Test("welded vertices contain no phantom origin entries", () => {
            var m = Make(new List<Vector3> {
                new Vector3(10,10), new Vector3(11,10), new Vector3(10,11),
                new Vector3(11,10), new Vector3(11,11), new Vector3(10,11)
            }, new List<int> { 0,1,2,3,4,5 });
            try { Require(MeshGraph.Build(m,0).pos.Length == 4, "Expected four welded vertices"); }
            finally { Object.DestroyImmediate(m); }
        });
        Test("degenerate preflight removes stale edge incidence", () => {
            var m = Make(new List<Vector3> { Vector3.zero, Vector3.right, Vector3.up },
                new List<int> { 0,1,2,0,1,0 });
            try {
                var g = MeshGraph.Build(m,0);
                Require(g.aliveTris == 1, "Wrong face count");
                Require(g.edges.Values.Where(e => !e.removed).All(e => e.boundary && e.tris.Count == 1 && g.triAlive[e.tris[0]]),
                    "Dead triangles still influence edge classification");
            } finally { Object.DestroyImmediate(m); }
        });
        Test("nearby UV islands remain distinct corner attributes", () => {
            var m = Make(new List<Vector3> {
                new Vector3(10,10), new Vector3(11,10), new Vector3(10,11),
                new Vector3(11,10), new Vector3(11,11), new Vector3(10,11)
            }, new List<int> { 0,1,2,3,4,5 });
            var uv=m.uv; uv[3].x+=.005f; uv[5].x+=.005f; m.uv=uv;
            try { Require(MeshGraph.Build(m,0).wedges.Length==6, "Distinct UV islands were merged"); }
            finally { Object.DestroyImmediate(m); }
        });
        Test("disconnected panels survive unreachable triangle budget", () => {
            var v = new List<Vector3>(); var f = new List<int>();
            for (int i = 0; i < 40; i++) {
                int a = v.Count; float x = i * 3;
                v.AddRange(new[] { new Vector3(x,0), new Vector3(x+1,0), new Vector3(x,1), new Vector3(x+1,1) });
                f.AddRange(new[] { a,a+1,a+2,a+1,a+3,a+2 });
            }
            var m = Make(v,f); Mesh output = null;
            try {
                // 64/80 = 0.8 depth -> stage 0, full preservation.
                var result = QEMSimplifier.Simplify(m, new SimplifyOptions { targetTriangleCount=64 });
                output = result.mesh;
                Require(result.resultTriangles == 80, "Panels were removed or eroded");
                Require(!result.reachedTarget, "Unreachable target reported as reached");
            } finally { Object.DestroyImmediate(m); if(output) Object.DestroyImmediate(output); }
        });
        Test("deep targets release interior panels but keep outer bounds", () => {
            var v = new List<Vector3>(); var f = new List<int>();
            for (int i = 0; i < 40; i++) {
                int a = v.Count; float x = i * 3;
                v.AddRange(new[] { new Vector3(x,0), new Vector3(x+1,0), new Vector3(x,1), new Vector3(x+1,1) });
                f.AddRange(new[] { a,a+1,a+2,a+1,a+3,a+2 });
            }
            var m = Make(v,f); Mesh output = null;
            try {
                // 8/80 = 0.1 depth -> interior loops may dissolve, but every
                // silhouette-hull vertex must survive untouched.
                var result = QEMSimplifier.Simplify(m, new SimplifyOptions { targetTriangleCount=8 });
                output = result.mesh;
                Require(result.resultTriangles < 80, "Interior detail was never released");
                foreach (var p in new[] {
                    new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(118,0,0), new Vector3(118,1,0) })
                    Require(output.vertices.Min(q => (q - p).sqrMagnitude) < 1e-6f,
                        "Outer bound vertex lost: " + p);
            } finally { Object.DestroyImmediate(m); if(output) Object.DestroyImmediate(output); }
        });
        Test("rotated annulus preserves area and both boundary curves", () => {
            var m = Grid(18,true);
            m.vertices = m.vertices.Select(p => Quaternion.Euler(23,37,19) * p + new Vector3(4,7,12)).ToArray();
            m.RecalculateNormals(); m.RecalculateBounds(); Mesh output = null;
            try {
                // 0.8 depth -> stage 0, both loops are strictly protected.
                var result = QEMSimplifier.Simplify(m,new SimplifyOptions
                    { targetTriangleCount=(int)(m.triangles.Length / 3 * 0.8f) });
                output = result.mesh;
                var before = Boundary(m); var after = Boundary(output);
                Require(after.Count > 0, "Lost boundaries");
                foreach (var e in before)
                    Require(after.Min(a => Distance(e.Item1,a.Item1,a.Item2)) < .0001f, "Boundary moved/shrank");
                foreach (var e in after)
                    Require(before.Min(a => Distance(e.Item1,a.Item1,a.Item2)) < .0001f, "Boundary bridged empty space");
                Require(Mathf.Abs(Area(output)-Area(m)) / Area(m) < .00001f, "Hole area or surface coverage changed");
                Require(result.resultTriangles < result.sourceTriangles, "No useful simplification");
            } finally { Object.DestroyImmediate(m); if(output) Object.DestroyImmediate(output); }
        });
        Test("affine UVs still correspond to output positions", () => {
            var m = Grid(18,false); Mesh output = null;
            try {
                var result = QEMSimplifier.Simplify(m,new SimplifyOptions { targetTriangleCount=40 });
                output = result.mesh; var v = output.vertices; var uv = output.uv;
                Require(result.resultTriangles < result.sourceTriangles, "No simplification");
                for(int i=0;i<v.Length;i++)
                    Require((uv[i]-new Vector2(v[i].x,v[i].y)).magnitude < .0001f, "Moved position kept stale UV");
            } finally { Object.DestroyImmediate(m); if(output) Object.DestroyImmediate(output); }
        });
        Test("tetrahedron cannot collapse to duplicate faces", () => {
            var m = Make(new List<Vector3> { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                new List<int> { 0,2,1,0,1,3,0,3,2,1,2,3 }); Mesh output = null;
            try {
                var result = QEMSimplifier.Simplify(m,new SimplifyOptions { targetTriangleCount=1, creaseAngle=180, seamWeight=0 });
                output=result.mesh;
                Require(result.resultTriangles==4, "Closed component collapsed to duplicate/empty surface");
            } finally { Object.DestroyImmediate(m); if(output) Object.DestroyImmediate(output); }
        });
        Test("locked boundary edges remain in the graph", () => {
            var m = Grid(18,false); Mesh output=null;
            try {
                var result=QEMSimplifier.Simplify(m,new SimplifyOptions { targetTriangleCount=24,lockBoundaries=true });
                output=result.mesh;
                Require(result.resultBoundaryComponents==1, "Ineligible edges were retired from topology");
                Require(result.resultTriangles>0, "Locked mesh vanished");
            } finally { Object.DestroyImmediate(m); if(output) Object.DestroyImmediate(output); }
        });
        Test("existing LODs, root transforms, repeated generation and fill", () => {
            var scene=EditorSceneManager.NewPreviewScene();
            var holder=new GameObject("PipelineRegressionHolder");
            SceneManager.MoveGameObjectToScene(holder,scene);
            var root=new GameObject("PipelineRegression");
            root.transform.SetParent(holder.transform,false);
            var m=Grid(12,false);
            var profile=ScriptableObject.CreateInstance<ComplexLODProfile>();
            profile.preset=ComplexLODProfile.QualityPreset.Performance;
            profile.outputFolder="ComplexLODs/Generated/Audit/Regressions";
            try {
                root.transform.position=new Vector3(7,11,13);
                root.transform.rotation=Quaternion.Euler(13,21,9);
                root.transform.localScale=new Vector3(2,3,4);
                root.AddComponent<MeshFilter>().sharedMesh=m;
                var primary=root.AddComponent<MeshRenderer>();
                var lower=new GameObject("ArtistAuthoredLower");
                lower.transform.SetParent(root.transform,false);
                lower.AddComponent<MeshFilter>().sharedMesh=m;
                var lowerRenderer=lower.AddComponent<MeshRenderer>();
                var door=new GameObject("UnassignedChild");
                door.transform.SetParent(root.transform,false);
                door.AddComponent<MeshFilter>().sharedMesh=m;
                var doorRenderer=door.AddComponent<MeshRenderer>();
                var group=root.AddComponent<LODGroup>();
                group.SetLODs(new[] { new LOD(.6f,new Renderer[] { primary }),new LOD(.01f,new Renderer[] { lowerRenderer }) });
                for(int pass=0;pass<3;pass++) {
                    var report=ComplexLODGenerator.Generate(root,profile);
                    var levels=group.GetLODs();
                    Require(report.renderers.Count==2 && levels[0].renderers.Length==2,"Old LOD outputs re-ingested as sources");
                    Require(levels[0].renderers.Contains(primary) && levels[0].renderers.Contains(doorRenderer),"Source renderer lost");
                    Require(!lowerRenderer.enabled,"Unassigned authored lower LOD still renders");
                    foreach(var renderer in levels[1].renderers)
                        Require(renderer.transform.IsChildOf(root.transform),"Root LOD clone escaped hierarchy");
                    var clone=levels[1].renderers[0];
                    for(int k=0;k<16;k++)
                        Require(Mathf.Abs(primary.localToWorldMatrix[k]-clone.localToWorldMatrix[k])<.0001f,"Root clone transform changed");
                }
                var lods=group.GetLODs();
                lods[1]=new LOD(lods[1].screenRelativeTransitionHeight,Array.Empty<Renderer>());
                group.SetLODs(lods);
                var filled=ComplexLODGenerator.FillLODGroup(group,profile);
                Require(group.GetLODs()[1].renderers.Length==2,"Fill lost renderers");
                int target=Mathf.Max(24,Mathf.RoundToInt(m.triangles.Length/3*profile.RatioForLevel(1)));
                var targetField=typeof(ComplexLODGenerator.LevelReport).GetField("targetTriangles");
                Require(targetField!=null && (int)targetField.GetValue(filled.renderers[0].levels[0])==target,
                    "Fill request does not match preset triangle budget");
                // Compare actual simplifier output to the requested preset, independently of report metadata.
                var expected=QEMSimplifier.Simplify(m,profile.CreateOptions(target));
                try {
                    Require(group.GetLODs()[1].renderers[0].GetComponent<MeshFilter>().sharedMesh.triangles.Length/3==
                        expected.resultTriangles,"Fill used screen-height ratio instead of preset ratio");
                } finally { Object.DestroyImmediate(expected.mesh); }
                Require(filled.renderers.Count==2,"Fill report lost renderer");
            } finally {
                Object.DestroyImmediate(holder); Object.DestroyImmediate(m); Object.DestroyImmediate(profile);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        });
        Directory.CreateDirectory("Logs/MegaCityAudit");
        File.WriteAllLines("Logs/MegaCityAudit/regressions.txt", results);
        Debug.Log(string.Join("\n",results));
    }
}
