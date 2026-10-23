using System.Collections.Generic;
using System.IO;
using ComplexLODs.Core;
using UnityEditor;
using UnityEngine;

namespace ComplexLODs
{
    /// <summary>
    /// LOD generation pipeline: simplifies each source renderer's mesh once per
    /// LOD level (always from the LOD0 source, never chained - no error
    /// accumulation), writes mesh/texture/material assets and fills a LODGroup.
    /// </summary>
    public static class ComplexLODGenerator
    {
        // Meshes at or below this size are already minimal: decimating them
        // only erodes rims and leaves gaps, so every LOD level reuses the
        // source mesh (still costs nothing at distance).
        private const int MinSimplifyTris = 256;

        public class LevelReport
        {
            public int triangles, vertices;
            public int boundaryComponents;
            public float maxError, rmsError;
            public bool reachedTarget, cancelled;
            public double ms;
        }

        public class RendererReport
        {
            public string name;
            public int sourceTriangles;
            public int sourceBoundaryComponents;
            public readonly List<LevelReport> levels = new List<LevelReport>();
        }

        public class Report
        {
            public readonly List<RendererReport> renderers = new List<RendererReport>();
            public int lodCount;
            public string outputFolder;
            public bool cancelled;
        }

        /// <summary>
        /// Generates a full LOD chain for every renderer under root, creates a
        /// LODGroup on root and returns a per-level quality report. Works on
        /// scene objects AND on prefab assets (the prefab is edited in place).
        /// </summary>
        public static Report Generate(GameObject root, ComplexLODProfile profile)
        {
            var report = new Report { lodCount = profile.EffectiveLodCount, outputFolder = profile.outputFolder };
            if (root == null || profile == null) return report;

            if (EditorUtility.IsPersistent(root))
            {
                string assetPath = AssetDatabase.GetAssetPath(root);
                if (string.IsNullOrEmpty(assetPath))
                {
                    Debug.LogWarning("[ComplexLODs] '" + root.name + "' is not a scene object or prefab asset.");
                    return report;
                }
                using (var scope = new PrefabUtility.EditPrefabContentsScope(assetPath))
                    GenerateHierarchy(scope.prefabContentsRoot, profile, report);
                return report;
            }

            GenerateHierarchy(root, profile, report);
            return report;
        }

        private static void GenerateHierarchy(GameObject root, ComplexLODProfile profile, Report report)
        {
            var sources = CollectSourceRenderers(root);
            if (sources.Count == 0)
            {
                Debug.LogWarning("[ComplexLODs] No MeshRenderer/SkinnedMeshRenderer found under '" + root.name + "'.");
                return;
            }

            LODGroup group = root.GetComponent<LODGroup>();
            if (group == null) group = Undo.AddComponent<LODGroup>(root);
            else Undo.RecordObject(group, "ComplexLODs");

            bool cancelled = false;
            var lods = new LOD[profile.EffectiveLodCount];
            lods[0] = new LOD(profile.TransitionHeightForLevel(0, profile.EffectiveLodCount), sources.ToArray());

            var meshDir = Path.Combine(Application.dataPath, profile.outputFolder + "/Meshes");
            Directory.CreateDirectory(meshDir);

            var levelRenderers = new List<Renderer>[profile.EffectiveLodCount];
            levelRenderers[0] = new List<Renderer>(sources);
            for (int l = 1; l < profile.EffectiveLodCount; l++) levelRenderers[l] = new List<Renderer>();

            for (int r = 0; r < sources.Count && !cancelled; r++)
            {
                Renderer src = sources[r];
                Mesh srcMesh = GetMesh(src);
                if (srcMesh == null)
                {
                    Debug.LogWarning("[ComplexLODs] '" + src.name + "' has no mesh, skipped.");
                    continue;
                }

                var rr = new RendererReport { name = src.name, sourceTriangles = srcMesh.triangles.Length / 3 };
                report.renderers.Add(rr);

                Material[][] levelMaterials = null;
                if (profile.textureLODs && profile.materialVariants)
                    levelMaterials = BuildMaterialLadder(src, profile);

                bool tiny = rr.sourceTriangles <= MinSimplifyTris;
                for (int l = 1; l < profile.EffectiveLodCount; l++)
                {
                    int target = Mathf.Max(24, Mathf.RoundToInt(rr.sourceTriangles * profile.RatioForLevel(l)));
                    Mesh outMesh;
                    SimplifyResult sres = null;
                    if (!tiny)
                    {
                        SimplifyOptions opt = profile.CreateOptions(target);
                        int capturedL = l;
                        opt.progress = (col, srcTris, cur, tgt) =>
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("ComplexLODs",
                                src.name + " LOD" + capturedL + "  (" + cur + " tris)", 1f - (float)cur / srcTris))
                                cancelled = true;
                        };
                        opt.cancelRequested = () => cancelled;

                        sres = QEMSimplifier.Simplify(srcMesh, opt);
                        if (sres.mesh == null) continue;
                        if (!string.IsNullOrEmpty(sres.warning))
                            Debug.LogWarning("[ComplexLODs] " + src.name + ": " + sres.warning);

                        outMesh = sres.mesh;
                        outMesh.name = src.name + "_LOD" + l;
                        string assetPath = "Assets/" + profile.outputFolder + "/Meshes/" + outMesh.name + ".asset";
                        AssetDatabase.CreateAsset(outMesh, AssetDatabase.GenerateUniqueAssetPath(assetPath));
                    }
                    else
                    {
                        outMesh = srcMesh;
                    }

                    GameObject clone = CreateLODClone(src, outMesh, l, levelMaterials);
                    levelRenderers[l].Add(clone.GetComponent<Renderer>());

                    if (l == 1 && sres != null) rr.sourceBoundaryComponents = sres.sourceBoundaryComponents;
                    rr.levels.Add(new LevelReport
                    {
                        triangles = sres != null ? sres.resultTriangles : rr.sourceTriangles,
                        vertices = sres != null ? sres.resultVertices : srcMesh.vertexCount,
                        boundaryComponents = sres != null ? sres.resultBoundaryComponents : 0,
                        maxError = sres != null ? sres.maxError : 0f,
                        rmsError = sres != null ? sres.rmsError : 0f,
                        reachedTarget = true,
                        cancelled = false,
                        ms = sres != null ? sres.elapsedMs : 0.0
                    });
                    if (sres != null && sres.cancelled) { cancelled = true; break; }
                }
            }

            for (int l = 1; l < profile.EffectiveLodCount; l++)
            {
                float h = profile.TransitionHeightForLevel(l, profile.EffectiveLodCount);
                if (h >= lods[l - 1].screenRelativeTransitionHeight)
                    h = lods[l - 1].screenRelativeTransitionHeight * 0.99f;
                lods[l] = new LOD(h, levelRenderers[l].ToArray());
            }

            group.SetLODs(lods);
            if (profile.crossFade)
            {
                group.animateCrossFading = true;
                group.fadeMode = LODFadeMode.CrossFade;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            report.cancelled = cancelled;
        }

        /// <summary>
        /// Fills empty LOD slots of an existing LODGroup by decimating its LOD0
        /// renderers without touching the ones already filled.
        /// </summary>
        public static Report FillLODGroup(LODGroup group, ComplexLODProfile profile)
        {
            var report = new Report { lodCount = 0, outputFolder = profile.outputFolder };
            if (group == null) return report;
            LOD[] lods = group.GetLODs();
            if (lods.Length == 0 || lods[0].renderers == null || lods[0].renderers.Length == 0)
            {
                Debug.LogWarning("[ComplexLODs] LODGroup has no LOD0 renderers.");
                return report;
            }

            Undo.RecordObject(group, "ComplexLODs fill missing LODs");
            var sources = new List<Renderer>(lods[0].renderers);
            Directory.CreateDirectory(Path.Combine(Application.dataPath, profile.outputFolder + "/Meshes"));
            bool cancelled = false;

            for (int l = 1; l < lods.Length; l++)
            {
                if (lods[l].renderers != null && lods[l].renderers.Length > 0) continue;
                var filled = new List<Renderer>();
                float h = lods[l].screenRelativeTransitionHeight;
                if (h <= 0f) h = profile.TransitionHeightForLevel(l, lods.Length);
                float ratio = Mathf.Clamp01(h / Mathf.Max(0.001f, lods[0].screenRelativeTransitionHeight));

                foreach (Renderer src in sources)
                {
                    Mesh srcMesh = GetMesh(src);
                    if (srcMesh == null) continue;
                    var rr = report.renderers.Find(x => x.name == src.name);
                    if (rr == null)
                    {
                        rr = new RendererReport { name = src.name, sourceTriangles = srcMesh.triangles.Length / 3 };
                        report.renderers.Add(rr);
                    }

                    int target = Mathf.Max(24, Mathf.RoundToInt(rr.sourceTriangles * ratio));
                    Mesh outMesh;
                    SimplifyResult sres = null;
                    if (rr.sourceTriangles <= MinSimplifyTris)
                    {
                        outMesh = srcMesh;
                    }
                    else
                    {
                        SimplifyOptions opt = profile.CreateOptions(target);
                        int capturedL = l;
                        opt.progress = (col, srcTris, cur, tgt) =>
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("ComplexLODs",
                                src.name + " LOD" + capturedL, 1f - (float)cur / srcTris))
                                cancelled = true;
                        };
                        opt.cancelRequested = () => cancelled;

                        sres = QEMSimplifier.Simplify(srcMesh, opt);
                        if (sres.mesh == null) continue;
                        outMesh = sres.mesh;
                        outMesh.name = src.name + "_LOD" + l;
                        AssetDatabase.CreateAsset(outMesh,
                            AssetDatabase.GenerateUniqueAssetPath("Assets/" + profile.outputFolder + "/Meshes/" + outMesh.name + ".asset"));
                    }

                    GameObject clone = CreateLODClone(src, outMesh, l, null);
                    filled.Add(clone.GetComponent<Renderer>());
                    if (sres != null) rr.sourceBoundaryComponents = sres.sourceBoundaryComponents;
                    rr.levels.Add(new LevelReport
                    {
                        triangles = sres != null ? sres.resultTriangles : rr.sourceTriangles,
                        vertices = sres != null ? sres.resultVertices : srcMesh.vertexCount,
                        boundaryComponents = sres != null ? sres.resultBoundaryComponents : 0,
                        maxError = sres != null ? sres.maxError : 0f,
                        rmsError = sres != null ? sres.rmsError : 0f,
                        reachedTarget = true,
                        ms = sres != null ? sres.elapsedMs : 0.0
                    });
                    if (sres != null && sres.cancelled) { cancelled = true; break; }
                }
                lods[l] = new LOD(h, filled.ToArray());
                if (cancelled) break;
            }

            group.SetLODs(lods);
            AssetDatabase.SaveAssets();
            EditorUtility.ClearProgressBar();
            report.lodCount = lods.Length;
            report.cancelled = cancelled;
            return report;
        }

        // ------------------------------------------------------------------

        private static List<Renderer> CollectSourceRenderers(GameObject root)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is MeshRenderer)
                {
                    if (r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null)
                        list.Add(r);
                }
                else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    list.Add(r);
                }
            }
            return list;
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static Material[][] BuildMaterialLadder(Renderer src, ComplexLODProfile profile)
        {
            var mats = src.sharedMaterials;
            var ladder = new Material[profile.EffectiveLodCount][];
            for (int l = 1; l < profile.EffectiveLodCount; l++)
            {
                int size = profile.MaxTextureSizeForLevel(l);
                if (size <= 0) continue;
                var arr = new Material[mats.Length];
                for (int m = 0; m < mats.Length; m++)
                    arr[m] = TextureLODUtility.CreateMaterialVariant(mats[m], size, profile.outputFolder, l, profile.resizeNormalMaps);
                ladder[l] = arr;
            }
            return ladder;
        }

        private static GameObject CreateLODClone(Renderer src, Mesh mesh, int level, Material[][] levelMaterials)
        {
            var clone = new GameObject(src.name + "_LOD" + level);
            Undo.RegisterCreatedObjectUndo(clone, "ComplexLODs LOD" + level);
            clone.transform.SetParent(src.transform.parent, false);
            clone.transform.localPosition = src.transform.localPosition;
            clone.transform.localRotation = src.transform.localRotation;
            clone.transform.localScale = src.transform.localScale;
            clone.layer = src.gameObject.layer;
            clone.isStatic = src.gameObject.isStatic;
            GameObjectUtility.SetStaticEditorFlags(clone, GameObjectUtility.GetStaticEditorFlags(src.gameObject));

            Material[] mats = src.sharedMaterials;
            if (levelMaterials != null && level < levelMaterials.Length && levelMaterials[level] != null)
                mats = levelMaterials[level];

            if (src is SkinnedMeshRenderer ssrc)
            {
                var smr = clone.AddComponent<SkinnedMeshRenderer>();
                smr.bones = ssrc.bones;
                smr.rootBone = ssrc.rootBone;
                smr.sharedMesh = mesh;
                smr.sharedMaterials = mats;
                smr.localBounds = ssrc.localBounds;
            }
            else
            {
                clone.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = clone.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
                var ssrcMR = (MeshRenderer)src;
                mr.shadowCastingMode = ssrcMR.shadowCastingMode;
                mr.receiveShadows = ssrcMR.receiveShadows;
            }
            return clone;
        }
    }
}
