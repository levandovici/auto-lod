// Temporary validation driver. Runs only when Logs/MegaCityAudit.request exists.
// Uses the real Unity importer, profile, generator and prefab hierarchy.
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
internal static class MegaCityAudit
{
    private const string Request = "Logs/MegaCityAudit.request";
    private const string Output = "Logs/MegaCityAudit";
    private static readonly string[] Models = {
        "Airport_01", "Skyscraper_02", "Church_01", "CityHall_01", "Football_01"
    };
    private static bool busy;

    [Serializable] public class Submesh { public int[] indices; }
    [Serializable] public class Part
    {
        public string name, meshPath;
        public Vector3[] positions, normals;
        public Vector2[] uv;
        public Submesh[] submeshes;
        public int vertices, triangles, graphVertices, boundaryComponents;
        public int requestedTriangles;
        public bool sourceReused, reportedReached;
        public double milliseconds;
    }
    [Serializable] public class Level
    {
        public int index;
        public float transition;
        public Part[] parts;
    }
    [Serializable] public class Run
    {
        public string model, preset, unityVersion, phase;
        public double generationMs;
        public bool cancelled;
        public Level[] levels;
    }
    [Serializable] public class InventoryItem { public string path; public int triangles, vertices; }
    [Serializable] public class Inventory { public InventoryItem[] models; }

    static MegaCityAudit() { EditorApplication.update += Tick; }

    private static Mesh MeshOf(Renderer r) =>
        r is SkinnedMeshRenderer skinned ? skinned.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;

    private static void Tick()
    {
        if (busy || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(Request)) return;
        busy = true;
        try
        {
            string phase = File.ReadAllText(Request).Trim();
            if (phase != "baseline" && phase != "fixed") throw new Exception("Unknown audit phase");
            string dir = Output + "/" + phase;
            Directory.CreateDirectory(dir);
            if (!File.Exists(Output + "/inventory.json"))
            {
                var inventory = new List<InventoryItem>();
                foreach (string file in Directory.GetFiles("Assets/JC_LP_MegaCity/Models/Buildings", "*.fbx"))
                {
                    var meshes = AssetDatabase.LoadAllAssetsAtPath(file.Replace('\\', '/')).OfType<Mesh>().ToArray();
                    inventory.Add(new InventoryItem {
                        path = file, triangles = meshes.Sum(m => m.triangles.Length / 3),
                        vertices = meshes.Sum(m => m.vertexCount)
                    });
                }
                File.WriteAllText(Output + "/inventory.json", JsonUtility.ToJson(
                    new Inventory { models = inventory.OrderByDescending(m => m.triangles).ToArray() }, true));
            }
            foreach (string model in Models)
                foreach (ComplexLODProfile.QualityPreset preset in Enum.GetValues(typeof(ComplexLODProfile.QualityPreset)))
                {
                    string file = dir + "/" + model + "_" + preset + ".json";
                    if (File.Exists(file)) continue;
                    File.WriteAllText(Output + "/status.txt", phase + ": " + model + " " + preset);
                    RunOne(model, preset, phase, file);
                    return; // One complete prefab/preset per editor update; results survive reloads.
                }
            File.Delete(Request);
            File.WriteAllText(Output + "/status.txt", phase + ": COMPLETE");
        }
        catch (Exception e)
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "/error.txt", e.ToString());
            if (File.Exists(Request)) File.Delete(Request);
            Debug.LogException(e);
        }
        finally { busy = false; EditorUtility.ClearProgressBar(); }
    }

    private static void RunOne(string model, ComplexLODProfile.QualityPreset preset, string phase, string file)
    {
        Scene scene = EditorSceneManager.NewPreviewScene();
        ComplexLODProfile profile = null;
        GameObject holder = null;
        try
        {
            holder = new GameObject("MegaCityAudit");
            SceneManager.MoveGameObjectToScene(holder, scene);
            string path = "Assets/JC_LP_MegaCity/Prefabs/Buildings/SM_Buildings_" + model + ".prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab) throw new Exception("Prefab not found: " + path);
            var root = Object.Instantiate(prefab, holder.transform);
            root.name = prefab.name;
            profile = ScriptableObject.CreateInstance<ComplexLODProfile>();
            profile.preset = preset;
            profile.textureLODs = false;
            profile.outputFolder = "ComplexLODs/Generated/Audit/" + phase + "/" + model + "/" + preset;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var report = ComplexLODGenerator.Generate(root, profile);
            watch.Stop();
            if (report.cancelled) throw new Exception("Audit generation cancelled");
            LOD[] lods = root.GetComponent<LODGroup>().GetLODs();
            if (lods.Length != profile.EffectiveLodCount) throw new Exception("Wrong LOD count");
            if (phase == "fixed" && model == "Football_01" &&
                (lods[0].renderers.Length != 9 ||
                 lods[0].renderers.Any(r => r.name == "SM_Buildings_Football_01_LOD1") ||
                 lods[0].renderers.Sum(r => MeshOf(r).triangles.Length / 3) != 84463))
                throw new Exception("Authored stadium LOD1 was included as source, or source children were lost");
            var run = new Run {
                model = model, preset = preset.ToString(), phase = phase,
                unityVersion = Application.unityVersion, generationMs = watch.Elapsed.TotalMilliseconds,
                cancelled = report.cancelled, levels = new Level[lods.Length]
            };
            var assigned = new HashSet<Renderer>();
            for (int l = 0; l < lods.Length; l++)
            {
                Renderer[] renderers = lods[l].renderers;
                if (renderers.Length != lods[0].renderers.Length) throw new Exception("Missing LOD renderer");
                var level = new Level { index = l, transition = lods[l].screenRelativeTransitionHeight,
                    parts = new Part[renderers.Length] };
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    bool mayShare = l > 0 && r < report.renderers.Count &&
                        report.renderers[r].levels.Count > l - 1 &&
                        (report.renderers[r].levels[l - 1].sharedWithPrevious ||
                         report.renderers[r].levels[l - 1].sourceReused);
                    if (!renderer || (!assigned.Add(renderer) && !mayShare))
                        throw new Exception("Null/duplicate LOD assignment");
                    Mesh mesh = MeshOf(renderer);
                    Mesh source = MeshOf(lods[0].renderers[r]);
                    var graph = MeshGraph.Build(mesh, 0);
                    var matrix = holder.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                    var sourceMatrix = holder.transform.worldToLocalMatrix * lods[0].renderers[r].localToWorldMatrix;
                    for (int k = 0; k < 16; k++)
                        if (Mathf.Abs(matrix[k] - sourceMatrix[k]) > 0.00001f)
                            throw new Exception("LOD transform differs from source");
                    var part = new Part {
                        name = renderer.name, meshPath = AssetDatabase.GetAssetPath(mesh),
                        positions = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray(),
                        normals = mesh.normals.Select(matrix.inverse.transpose.MultiplyVector).ToArray(),
                        uv = mesh.uv, vertices = mesh.vertexCount, triangles = mesh.triangles.Length / 3,
                        graphVertices = graph.pos.Length, boundaryComponents = graph.CountBoundaryComponents(),
                        submeshes = new Submesh[mesh.subMeshCount], sourceReused = mesh == source,
                        requestedTriangles = l == 0 ? source.triangles.Length / 3 :
                            Mathf.Max(24, Mathf.RoundToInt(source.triangles.Length / 3 * profile.RatioForLevel(l))),
                        reportedReached = l == 0 || report.renderers[r].levels[l - 1].reachedTarget,
                        milliseconds = l == 0 ? 0 : report.renderers[r].levels[l - 1].ms
                    };
                    if (mesh.subMeshCount != source.subMeshCount) throw new Exception("Lost material slot");
                    if (!renderer.sharedMaterials.SequenceEqual(lods[0].renderers[r].sharedMaterials))
                        throw new Exception("Material assignment differs");
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        part.submeshes[s] = new Submesh { indices = mesh.GetTriangles(s) };
                    if (l > 0 && source.triangles.Length / 3 <= 256 && mesh != source)
                        throw new Exception("Small child was simplified");
                    level.parts[r] = part;
                }
                run.levels[l] = level;
            }
            if (phase == "fixed")
            {
                holder.name = model + "_" + preset;
                string previewPath = "Assets/" + profile.outputFolder + "/" + holder.name + ".prefab";
                if (!PrefabUtility.SaveAsPrefabAsset(holder, previewPath))
                    throw new Exception("Could not save review prefab: " + previewPath);
            }
            File.WriteAllText(file, JsonUtility.ToJson(run));
        }
        finally
        {
            if (holder) Object.DestroyImmediate(holder);
            if (profile) Object.DestroyImmediate(profile);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
