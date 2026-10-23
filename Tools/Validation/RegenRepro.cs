// Temporary repro driver. Runs only when Logs/RegenRepro.request exists.
// Replays: Quality(6) -> Performance(3) -> Quality again, plus FillLODGroup
// after a preset switch. Counts new GameObjects, mesh assets and LOD slots
// after every step.
using System;
using System.IO;
using System.Linq;
using System.Text;
using ComplexLODs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

[InitializeOnLoad]
internal static class RegenRepro
{
    private const string Request = "Logs/RegenRepro.request";
    private const string Prefab = "Assets/JC_LP_MegaCity/Prefabs/Buildings/SM_Buildings_Airport_01.prefab";
    private static bool busy;

    static RegenRepro() { EditorApplication.update += Tick; }

    // Batch entry point: Unity.exe -batchmode -projectPath <proj> -executeMethod RegenRepro.RunBatch -quit
    // Also runs the regression suite when MegaCityRegressions.cs is copied in
    // alongside (same validation folder) and its request file exists.
    public static void RunBatch() { Run(); MegaCityRegressions.RunBatch(); }

    private static void Tick()
    {
        if (busy || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(Request)) return;
        busy = true;
        try { Run(); if (File.Exists(Request)) File.Delete(Request); }
        catch (Exception e) { File.WriteAllText("Logs/RegenRepro.error.txt", e.ToString()); Debug.LogException(e); }
        finally { busy = false; EditorUtility.ClearProgressBar(); }
    }

    private static ComplexLODProfile Profile(ComplexLODProfile.QualityPreset p)
    {
        var profile = ScriptableObject.CreateInstance<ComplexLODProfile>();
        profile.preset = p;
        profile.textureLODs = false;
        profile.outputFolder = "ComplexLODs/Generated/Repro";
        return profile;
    }

    private static int MeshAssets()
    {
        string dir = "Assets/ComplexLODs/Generated/Repro/Meshes";
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.asset").Length : 0;
    }

    private static void Run()
    {
        var sb = new StringBuilder();
        Scene scene = EditorSceneManager.NewPreviewScene();
        GameObject holder = null;
        try
        {
            holder = new GameObject("RegenRepro");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            var root = Object.Instantiate(prefab, holder.transform);
            root.name = prefab.name;

            foreach (var preset in new[]
            {
                ComplexLODProfile.QualityPreset.Quality,
                ComplexLODProfile.QualityPreset.Performance,
                ComplexLODProfile.QualityPreset.Quality
            })
            {
                var profile = Profile(preset);
                int childrenBefore = root.GetComponentsInChildren<Transform>(true).Length;
                int assetsBefore = MeshAssets();
                var report = ComplexLODGenerator.Generate(root, profile);
                int childrenAfter = root.GetComponentsInChildren<Transform>(true).Length;
                var lods = root.GetComponent<LODGroup>().GetLODs();
                sb.AppendLine($"== Generate({preset}) -> LODGroup slots {lods.Length}, " +
                    $"new children {childrenAfter - childrenBefore}, new mesh assets {MeshAssets() - assetsBefore}, " +
                    $"renderers in report {report.renderers.Count}, cancelled {report.cancelled}");
                for (int l = 0; l < lods.Length; l++)
                    sb.AppendLine($"   slot {l}: {lods[l].renderers.Length} renderers, " +
                        $"{lods[l].renderers.Distinct().Count()} distinct objects");
                foreach (var rr in report.renderers)
                    sb.AppendLine($"   {rr.name}: src {rr.sourceTriangles} tris | " +
                        string.Join(" | ", rr.levels.Select((x, i) =>
                            $"LOD{i + 1}:{x.triangles}t/{x.targetTriangles} " +
                            $"{(x.sourceReused ? "src" : x.sharedWithPrevious ? "shared" : x.reachedTarget ? "ok" : "limited")}")));
                Object.DestroyImmediate(profile);
            }

            // Now replay on a FRESH instance: Generate at Performance (3 slots),
            // then switch profile to Quality and use FillLODGroup (the context
            // menu path "Generate Missing LODs").
            var root2 = Object.Instantiate(prefab, holder.transform);
            root2.name = prefab.name + "_fill";
            var perf = Profile(ComplexLODProfile.QualityPreset.Performance);
            ComplexLODGenerator.Generate(root2, perf);
            Object.DestroyImmediate(perf);
            var group = root2.GetComponent<LODGroup>();
            int slots = group.GetLODs().Length;
            int fillChildrenBefore = root2.GetComponentsInChildren<Transform>(true).Length;
            int fillAssetsBefore = MeshAssets();
            var quality = Profile(ComplexLODProfile.QualityPreset.Quality);
            var fillReport = ComplexLODGenerator.FillLODGroup(group, quality);
            int fillChildrenAfter = root2.GetComponentsInChildren<Transform>(true).Length;
            var lodsAfter = group.GetLODs();
            sb.AppendLine($"== FillLODGroup(Quality) after Generate(Performance): slots {slots} -> {lodsAfter.Length}, " +
                $"new children {fillChildrenAfter - fillChildrenBefore}, new mesh assets {MeshAssets() - fillAssetsBefore}, " +
                $"fill lodCount {fillReport.lodCount}, cancelled {fillReport.cancelled}");
            for (int l = 0; l < lodsAfter.Length; l++)
                sb.AppendLine($"   slot {l}: {lodsAfter[l].renderers.Length} renderers, " +
                    $"{lodsAfter[l].renderers.Distinct().Count()} distinct objects");
            Object.DestroyImmediate(quality);

            File.WriteAllText("Logs/RegenRepro.txt", sb.ToString());
        }
        finally
        {
            if (holder) Object.DestroyImmediate(holder);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
