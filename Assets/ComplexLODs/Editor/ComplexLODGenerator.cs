using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            public int targetTriangles;
            public float maxError, rmsError;
            public bool reachedTarget, cancelled, sourceReused, sharedWithPrevious;
            public int releaseStage;
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
            if (root == null || profile == null) return new Report();
            var report = new Report { lodCount = profile.EffectiveLodCount, outputFolder = profile.outputFolder };

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

            // Retire previous lower-LOD renderers that are not sources and
            // disable nested LODGroups, so the root group is the sole LOD
            // authority. Objects this pipeline generated earlier (their mesh
            // lives under the profile's output folder) are destroyed rather
            // than left as disabled ghosts; artist-authored objects are kept.
            var sourceSet = new HashSet<Renderer>(sources);
            var lodZeroByName = new Dictionary<string, Renderer>();
            foreach (Renderer s in sources) lodZeroByName[s.name] = s;
            foreach (LODGroup previous in root.GetComponentsInChildren<LODGroup>(true))
            {
                foreach (LOD level in previous.GetLODs())
                    foreach (Renderer renderer in level.renderers ?? new Renderer[0])
                        if (renderer != null && !sourceSet.Contains(renderer))
                        {
                            if (IsGeneratedLeaf(renderer, profile, lodZeroByName))
                                Object.DestroyImmediate(renderer.gameObject);
                            else
                            {
                                Undo.RecordObject(renderer, "ComplexLODs retire previous LOD");
                                renderer.enabled = false;
                            }
                        }
                if (previous.gameObject != root)
                {
                    Undo.RecordObject(previous, "ComplexLODs consolidate LOD groups");
                    previous.enabled = false;
                }
            }

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
                if (tiny)
                    rr.sourceBoundaryComponents = MeshGraph.Build(srcMesh, profile.simplify.weldTolerance)
                        .CountBoundaryComponents();
                Mesh previousMesh = srcMesh;
                Renderer previousRenderer = src;
                var notes = new Dictionary<string, int>();
                for (int l = 1; l < profile.EffectiveLodCount; l++)
                {
                    int target = Mathf.Max(24, Mathf.RoundToInt(rr.sourceTriangles * profile.RatioForLevel(l)));
                    Mesh outMesh;
                    SimplifyResult sres = null;
                    bool shared;
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
                            notes[sres.warning] = notes.TryGetValue(sres.warning, out int n) ? n + 1 : 1;

                        outMesh = sres.mesh;
                        shared = MeshesIdentical(previousMesh, outMesh);
                        if (shared)
                        {
                            Object.DestroyImmediate(outMesh);
                            outMesh = previousMesh;
                        }
                        else
                        {
                            outMesh.name = src.name + "_LOD" + l;
                            string assetPath = "Assets/" + profile.outputFolder + "/Meshes/" + outMesh.name + ".asset";
                            AssetDatabase.CreateAsset(outMesh, AssetDatabase.GenerateUniqueAssetPath(assetPath));
                            previousMesh = outMesh;
                        }
                    }
                    else
                    {
                        outMesh = srcMesh;
                        shared = true;
                    }

                    // Identical geometry needs no separate object: a renderer
                    // may be listed in several LOD slots, so a level that
                    // shares the previous level's mesh (or a tiny source that
                    // keeps its own mesh) reuses that renderer - unless a
                    // material variant has to be carried by a distinct clone.
                    // This also avoids crossfade ghosting between identical
                    // duplicates.
                    bool needsVariant = levelMaterials != null && l < levelMaterials.Length &&
                        levelMaterials[l] != null;
                    Renderer lodRenderer;
                    if (shared && !needsVariant)
                        lodRenderer = previousRenderer;
                    else
                    {
                        GameObject clone = CreateLODClone(src, outMesh, l, levelMaterials, root.transform);
                        lodRenderer = clone.GetComponent<Renderer>();
                        previousRenderer = lodRenderer;
                    }
                    levelRenderers[l].Add(lodRenderer);

                    if (l == 1 && sres != null) rr.sourceBoundaryComponents = sres.sourceBoundaryComponents;
                    rr.levels.Add(new LevelReport
                    {
                        triangles = sres != null ? sres.resultTriangles : rr.sourceTriangles,
                        vertices = sres != null ? sres.resultVertices : srcMesh.vertexCount,
                        boundaryComponents = sres != null ? sres.resultBoundaryComponents : rr.sourceBoundaryComponents,
                        maxError = sres != null ? sres.maxError : 0f,
                        rmsError = sres != null ? sres.rmsError : 0f,
                        targetTriangles = target,
                        sourceReused = sres == null,
                        sharedWithPrevious = shared,
                        reachedTarget = sres != null ? sres.reachedTarget : rr.sourceTriangles <= target,
                        cancelled = sres != null && sres.cancelled,
                        releaseStage = sres != null ? sres.releaseStage : 0,
                        ms = sres != null ? sres.elapsedMs : 0.0
                    });
                    if (sres != null && sres.cancelled) { cancelled = true; break; }
                }
                foreach (var note in notes)
                    Debug.LogWarning("[ComplexLODs] " + src.name + ": " + note.Key +
                        (note.Value > 1 ? " (" + note.Value + " levels)" : ""));
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
        /// Fills missing LOD slots of an existing LODGroup by decimating its
        /// LOD0 renderers. Slots holding artist-authored renderers are left
        /// untouched; empty slots and slots containing only earlier generated
        /// output are (re)filled. The group is extended to the profile's level
        /// count when it has fewer slots.
        /// </summary>
        public static Report FillLODGroup(LODGroup group, ComplexLODProfile profile)
        {
            if (group == null || profile == null) return new Report();
            var report = new Report { lodCount = 0, outputFolder = profile.outputFolder };
            LOD[] lods = group.GetLODs();
            if (lods.Length == 0 || lods[0].renderers == null || lods[0].renderers.Length == 0)
            {
                Debug.LogWarning("[ComplexLODs] LODGroup has no LOD0 renderers.");
                return report;
            }

            Undo.RecordObject(group, "ComplexLODs fill missing LODs");
            var sources = new List<Renderer>(lods[0].renderers);
            Directory.CreateDirectory(Path.Combine(Application.dataPath, profile.outputFolder + "/Meshes"));
            var lodZeroByName = new Dictionary<string, Renderer>();
            foreach (Renderer s in sources) lodZeroByName[s.name] = s;
            var previousMeshes = new Dictionary<Renderer, Mesh>();
            var previousRenderers = new Dictionary<Renderer, Renderer>();
            bool cancelled = false;

            // A slot that does not exist yet is by definition missing: grow
            // the LOD array to the profile's level count so preset switches
            // take effect on already-generated groups.
            if (lods.Length < profile.EffectiveLodCount)
            {
                var resized = new LOD[profile.EffectiveLodCount];
                System.Array.Copy(lods, resized, lods.Length);
                lods = resized;
            }

            for (int l = 1; l < lods.Length; l++)
            {
                var existing = lods[l].renderers;
                bool missing = existing == null || existing.Length == 0 ||
                    existing.All(r => r == null || !r.enabled) ||
                    existing.All(r => r == null || IsGeneratedLeaf(r, profile, lodZeroByName));
                if (!missing) continue;
                foreach (var stale in existing ?? new Renderer[0])
                    if (stale != null && IsGeneratedLeaf(stale, profile, lodZeroByName))
                        Object.DestroyImmediate(stale.gameObject);
                var filled = new List<Renderer>();
                float h = lods[l].screenRelativeTransitionHeight;
                if (h <= 0f) h = profile.TransitionHeightForLevel(l, lods.Length);
                if (h >= lods[l - 1].screenRelativeTransitionHeight)
                    h = lods[l - 1].screenRelativeTransitionHeight * 0.99f;
                float ratio = profile.RatioForLevel(l);

                foreach (Renderer src in sources)
                {
                    Mesh srcMesh = GetMesh(src);
                    if (srcMesh == null) continue;
                    var rr = report.renderers.Find(x => x.name == src.name);
                    if (rr == null)
                    {
                        rr = new RendererReport { name = src.name, sourceTriangles = srcMesh.triangles.Length / 3 };
                        if (rr.sourceTriangles <= MinSimplifyTris)
                            rr.sourceBoundaryComponents = MeshGraph.Build(srcMesh, profile.simplify.weldTolerance)
                                .CountBoundaryComponents();
                        report.renderers.Add(rr);
                    }

                    int target = Mathf.Max(24, Mathf.RoundToInt(rr.sourceTriangles * ratio));
                    Mesh outMesh;
                    SimplifyResult sres = null;
                    bool shared;
                    if (rr.sourceTriangles <= MinSimplifyTris)
                    {
                        outMesh = srcMesh;
                        shared = true;
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
                        if (!previousMeshes.TryGetValue(src, out Mesh previous)) previous = srcMesh;
                        shared = MeshesIdentical(previous, outMesh);
                        if (shared)
                        {
                            Object.DestroyImmediate(outMesh);
                            outMesh = previous;
                        }
                        else
                        {
                            outMesh.name = src.name + "_LOD" + l;
                            AssetDatabase.CreateAsset(outMesh,
                                AssetDatabase.GenerateUniqueAssetPath("Assets/" + profile.outputFolder + "/Meshes/" + outMesh.name + ".asset"));
                            previousMeshes[src] = outMesh;
                        }
                    }

                    // Same sharing rule as Generate: identical geometry reuses
                    // the previous level's renderer instead of a new object.
                    if (!previousRenderers.TryGetValue(src, out Renderer previousRenderer))
                        previousRenderer = src;
                    Renderer lodRenderer;
                    if (shared)
                        lodRenderer = previousRenderer;
                    else
                    {
                        GameObject clone = CreateLODClone(src, outMesh, l, null, group.transform);
                        lodRenderer = clone.GetComponent<Renderer>();
                        previousRenderers[src] = lodRenderer;
                    }
                    filled.Add(lodRenderer);
                    if (sres != null) rr.sourceBoundaryComponents = sres.sourceBoundaryComponents;
                    rr.levels.Add(new LevelReport
                    {
                        triangles = sres != null ? sres.resultTriangles : rr.sourceTriangles,
                        vertices = sres != null ? sres.resultVertices : srcMesh.vertexCount,
                        boundaryComponents = sres != null ? sres.resultBoundaryComponents : rr.sourceBoundaryComponents,
                        maxError = sres != null ? sres.maxError : 0f,
                        rmsError = sres != null ? sres.rmsError : 0f,
                        targetTriangles = target,
                        sourceReused = sres == null,
                        sharedWithPrevious = shared,
                        reachedTarget = sres != null ? sres.reachedTarget : rr.sourceTriangles <= target,
                        cancelled = sres != null && sres.cancelled,
                        releaseStage = sres != null ? sres.releaseStage : 0,
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
            // LOD-group membership decides what is source: renderers in any
            // LOD level below 0 are previous/generated LOD output, and
            // disabled renderers outside an explicit LOD0 slot are not active
            // source geometry.
            var excluded = new HashSet<Renderer>();
            var lodZero = new HashSet<Renderer>();
            foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] levels = group.GetLODs();
                if (levels.Length == 0) continue;
                var primary = new HashSet<Renderer>(levels[0].renderers ?? new Renderer[0]);
                lodZero.UnionWith(primary);
                for (int l = 1; l < levels.Length; l++)
                    foreach (Renderer renderer in levels[l].renderers ?? new Renderer[0])
                        if (renderer != null && !primary.Contains(renderer)) excluded.Add(renderer);
            }
            var list = new List<Renderer>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (excluded.Contains(renderer)) continue;
                if (!renderer.enabled && !lodZero.Contains(renderer)) continue;
                if ((renderer is MeshRenderer || renderer is SkinnedMeshRenderer) && GetMesh(renderer) != null)
                    list.Add(renderer);
            }
            return list;
        }

        // A deeper level can land on the same preservation floor as the level
        // before it (feature-protected meshes stop at an identical fixed
        // point). In that case the level reuses the previous mesh asset
        // instead of writing a duplicate.
        private static bool MeshesIdentical(Mesh a, Mesh b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.vertexCount != b.vertexCount || a.subMeshCount != b.subMeshCount) return false;
            for (int s = 0; s < a.subMeshCount; s++)
                if (!a.GetTriangles(s).SequenceEqual(b.GetTriangles(s))) return false;
            return a.vertices.SequenceEqual(b.vertices)
                && a.normals.SequenceEqual(b.normals)
                && a.tangents.SequenceEqual(b.tangents)
                && a.colors32.SequenceEqual(b.colors32)
                && a.boneWeights.SequenceEqual(b.boneWeights)
                && a.uv.SequenceEqual(b.uv)
                && a.uv2.SequenceEqual(b.uv2)
                && a.uv3.SequenceEqual(b.uv3)
                && a.uv4.SequenceEqual(b.uv4);
        }

        // True only for leaf objects this pipeline produced: a single renderer
        // (plus MeshFilter) with no children whose mesh asset lives under the
        // profile's output folder - or, for older runs that cloned source-reuse
        // children, whose name is "<source>_LOD<n>" with the very same mesh
        // instance as that LOD0 source. Authored LOD content (like the
        // stadium's authored LOD1) fails both checks: it sits under a
        // different name base or references a different mesh - so it is
        // retired, never deleted.
        private static bool IsGeneratedLeaf(Renderer renderer, ComplexLODProfile profile,
            Dictionary<string, Renderer> lodZeroByName)
        {
            if (renderer.transform.childCount != 0) return false;
            foreach (var c in renderer.GetComponents<Component>())
                if (!(c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer))
                    return false;
            Mesh mesh = GetMesh(renderer);
            string path = mesh != null ? AssetDatabase.GetAssetPath(mesh) : null;
            if (!string.IsNullOrEmpty(path) &&
                path.Replace('\\', '/').StartsWith("Assets/" + profile.outputFolder + "/"))
                return true;
            if (mesh != null && lodZeroByName != null)
            {
                string name = renderer.name;
                int cut = name.LastIndexOf("_LOD");
                if (cut > 0 && int.TryParse(name.Substring(cut + 4), out int n) && n >= 1 &&
                    lodZeroByName.TryGetValue(name.Substring(0, cut), out Renderer owner) &&
                    GetMesh(owner) == mesh)
                    return true;
            }
            return false;
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

        private static GameObject CreateLODClone(Renderer src, Mesh mesh, int level, Material[][] levelMaterials, Transform root)
        {
            var clone = new GameObject(src.name + "_LOD" + level);
            Undo.RegisterCreatedObjectUndo(clone, "ComplexLODs LOD" + level);
            if (src.transform == root)
            {
                clone.transform.SetParent(root, false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;
            }
            else
            {
                clone.transform.SetParent(src.transform.parent, false);
                clone.transform.localPosition = src.transform.localPosition;
                clone.transform.localRotation = src.transform.localRotation;
                clone.transform.localScale = src.transform.localScale;
            }
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
