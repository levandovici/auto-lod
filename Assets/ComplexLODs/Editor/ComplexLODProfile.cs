using ComplexLODs.Core;
using UnityEngine;

namespace ComplexLODs
{
    /// <summary>
    /// Serializable generation profile. Create via
    /// Assets/Create/ComplexLODs/LOD Profile to store presets per asset type
    /// (buildings, props, vegetation, characters...).
    /// </summary>
    [CreateAssetMenu(menuName = "ComplexLODs/LOD Profile", fileName = "ComplexLODProfile")]
    public class ComplexLODProfile : ScriptableObject
    {
        public enum QualityPreset { Performance, Balanced, Quality }

        [Header("Main")]
        [Tooltip("Performance = few aggressive LODs, Quality = many gentle LODs. Everything else is automatic.")]
        public QualityPreset preset = QualityPreset.Balanced;

        [Tooltip("Total number of LOD levels including LOD0 (the source mesh).")]
        [Range(1, 8)] public int lodCount = 4;

        [Tooltip("Each level gets sourceTriangles / reductionRate^level triangles.")]
        [Range(1.1f, 6f)] public float reductionRate = 2f;

        [Tooltip("Override the per-level triangle ratios manually (index = LOD level).")]
        public bool customRatios = false;
        public float[] triangleRatios = { 1f, 0.5f, 0.25f, 0.125f, 0.0625f, 0.03f, 0.015f, 0.008f };

        [Tooltip("screenRelativeTransitionHeight of LOD l = transitionBase^(l+1).")]
        [Range(0.1f, 0.9f)] public float transitionBase = 0.6f;
        [Tooltip("Screen-relative height below which the object is culled entirely.")]
        [Range(0.001f, 0.2f)] public float cullHeight = 0.01f;
        public bool crossFade = true;

        [Header("Simplification")]
        public SimplifyOptions simplify = new SimplifyOptions();

        [Header("Texture LODs")]
        [Tooltip("Generate downscaled texture variants per LOD level and assign material variants.")]
        public bool textureLODs = false;
        [Tooltip("Max texture size per LOD level (0 = keep original textures).")]
        public int[] maxTextureSize = { 0, 2048, 1024, 512, 256, 128, 64, 64 };
        [Tooltip("Downscale normal maps too.")]
        public bool resizeNormalMaps = true;
        [Tooltip("Create LOD material variants instead of only generating textures.")]
        public bool materialVariants = true;

        [Header("Output")]
        [Tooltip("Folder under Assets/ where meshes, textures and materials are written.")]
        public string outputFolder = "ComplexLODs/Generated";

        // Effective values driven by the preset (manual fields used only for Custom).
        public int EffectiveLodCount =>
            preset == QualityPreset.Performance ? 3 :
            preset == QualityPreset.Quality ? 6 :
            preset == QualityPreset.Balanced ? 4 : Mathf.Max(1, lodCount);

        public float EffectiveReductionRate =>
            preset == QualityPreset.Performance ? 2.5f :
            preset == QualityPreset.Quality ? 1.6f :
            preset == QualityPreset.Balanced ? 2f : Mathf.Max(1.01f, reductionRate);

        public float EffectiveTransitionBase =>
            preset == QualityPreset.Performance ? 0.5f :
            preset == QualityPreset.Quality ? 0.7f :
            preset == QualityPreset.Balanced ? 0.6f : transitionBase;

        public float RatioForLevel(int level)
        {
            if (customRatios && level < triangleRatios.Length && triangleRatios[level] > 0f)
                return Mathf.Clamp01(triangleRatios[level]);
            return Mathf.Pow(1f / EffectiveReductionRate, level);
        }

        public float TransitionHeightForLevel(int level, int total)
        {
            if (level >= total - 1) return cullHeight;
            return Mathf.Max(cullHeight * 2f, Mathf.Pow(EffectiveTransitionBase, level + 1));
        }

        public int MaxTextureSizeForLevel(int level)
        {
            if (level < maxTextureSize.Length) return maxTextureSize[level];
            int last = maxTextureSize[maxTextureSize.Length - 1];
            return Mathf.Max(32, last >> (level - maxTextureSize.Length + 1));
        }

        public SimplifyOptions CreateOptions(int targetTriangles)
        {
            var o = new SimplifyOptions
            {
                targetTriangleCount = targetTriangles,
                weldTolerance = simplify.weldTolerance,
                boundaryWeight = simplify.boundaryWeight,
                lockBoundaries = simplify.lockBoundaries,
                creaseAngle = simplify.creaseAngle,
                creaseWeight = simplify.creaseWeight,
                seamWeight = simplify.seamWeight,
                extremesWeight = simplify.extremesWeight,
                extremesPower = simplify.extremesPower,
                lockTopVertices = simplify.lockTopVertices,
                lockBoundsExtremes = simplify.lockBoundsExtremes,
                useVertexColorImportance = simplify.useVertexColorImportance,
                vertexColorChannel = simplify.vertexColorChannel,
                importanceMax = simplify.importanceMax,
                interiorRemoval = simplify.interiorRemoval,
                stripInterior = simplify.stripInterior,
                interiorCostBias = simplify.interiorCostBias,
                preventFlips = simplify.preventFlips,
                neverCollapseLocked = simplify.neverCollapseLocked,
                preserveTopology = simplify.preserveTopology,
                preserveBoundaryLoops = simplify.preserveBoundaryLoops,
                dissolveOpeningBelow = simplify.dissolveOpeningBelow,
                componentMinTris = simplify.componentMinTris,
                recalculateNormals = simplify.recalculateNormals
            };
            return o;
        }
    }
}
