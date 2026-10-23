using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ComplexLODs
{
    /// <summary>
    /// Generates downscaled texture assets and per-LOD material variants.
    /// Works with non-readable textures by blitting through a RenderTexture.
    /// </summary>
    internal static class TextureLODUtility
    {
        private static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        /// <summary>
        /// Returns a version of src whose largest dimension is maxSize, or src
        /// itself when it already fits. The resized texture is saved as a PNG
        /// asset under &lt;outputFolder&gt;/Textures and reused on repeat runs.
        /// </summary>
        public static Texture2D GetOrCreateResized(Texture2D src, int maxSize, string outputFolder, bool isNormalMap)
        {
            if (src == null || maxSize <= 0) return src;
            int w = src.width, h = src.height;
            int max = Mathf.Max(w, h);
            if (max <= maxSize) return src;

            string key = AssetDatabase.GetAssetPath(src) + "|" + maxSize;
            if (cache.TryGetValue(key, out Texture2D cached) && cached != null) return cached;

            float scale = (float)maxSize / max;
            int nw = Mathf.Max(4, Mathf.RoundToInt(w * scale));
            int nh = Mathf.Max(4, Mathf.RoundToInt(h * scale));

            string dir = Path.Combine(Application.dataPath, outputFolder + "/Textures");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(src));
            if (string.IsNullOrEmpty(name)) name = src.name;
            string relPath = "Assets/" + outputFolder + "/Textures/" + name + "_" + maxSize + ".png";
            string absPath = Path.Combine(Application.dataPath, outputFolder + "/Textures/" + name + "_" + maxSize + ".png");

            Texture2D result = AssetDatabase.LoadAssetAtPath<Texture2D>(relPath);
            if (result == null)
            {
                RenderTextureReadWrite rw = src.isDataSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
                RenderTexture rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, rw);
                RenderTexture prev = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var resized = new Texture2D(nw, nh, TextureFormat.RGBA32, true, !src.isDataSRGB);
                resized.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                resized.Apply(true, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                File.WriteAllBytes(absPath, resized.EncodeToPNG());
                Object.DestroyImmediate(resized);
                AssetDatabase.ImportAsset(relPath, ImportAssetOptions.ForceUpdate);

                // Mirror the source import settings.
                string srcPath = AssetDatabase.GetAssetPath(src);
                TextureImporter srcImp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
                TextureImporter imp = AssetImporter.GetAtPath(relPath) as TextureImporter;
                if (imp != null)
                {
                    if (srcImp != null)
                    {
                        imp.textureType = isNormalMap ? TextureImporterType.NormalMap : srcImp.textureType;
                        imp.sRGBTexture = srcImp.sRGBTexture;
                        imp.alphaIsTransparency = srcImp.alphaIsTransparency;
                        imp.mipmapEnabled = true;
                        imp.wrapMode = srcImp.wrapMode;
                        imp.filterMode = srcImp.filterMode;
                    }
                    else
                    {
                        imp.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                        imp.mipmapEnabled = true;
                    }
                    imp.SaveAndReimport();
                }
                result = AssetDatabase.LoadAssetAtPath<Texture2D>(relPath);
            }

            if (result != null) cache[key] = result;
            return result != null ? result : src;
        }

        public static bool IsNormalMap(Material mat, string property)
        {
            TextureImporter imp = ImporterFor(mat.GetTexture(property));
            if (imp != null) return imp.textureType == TextureImporterType.NormalMap;
            string n = property.ToLowerInvariant();
            return n.Contains("normal") || n.Contains("bump");
        }

        private static TextureImporter ImporterFor(Texture t)
        {
            if (t == null) return null;
            string path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetImporter.GetAtPath(path) as TextureImporter;
        }

        /// <summary>
        /// Creates (or reuses) a material variant whose 2D textures are clamped
        /// to maxSize. Returns the source material when nothing needed resizing.
        /// </summary>
        public static Material CreateMaterialVariant(Material src, int maxSize, string outputFolder, int lodLevel, bool resizeNormals)
        {
            if (src == null || maxSize <= 0) return src;

            string dir = Path.Combine(Application.dataPath, outputFolder + "/Materials");
            Directory.CreateDirectory(dir);
            string relPath = "Assets/" + outputFolder + "/Materials/" + src.name + "_LOD" + lodLevel + ".mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(relPath);
            if (existing != null) return existing;

            var variant = new Material(src) { name = src.name + "_LOD" + lodLevel };
            bool any = false;
            Shader shader = src.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                string prop = shader.GetPropertyName(i);
                var tex = variant.GetTexture(prop) as Texture2D;
                if (tex == null) continue;
                bool nrm = IsNormalMap(src, prop);
                if (nrm && !resizeNormals) continue;
                Texture2D resized = GetOrCreateResized(tex, maxSize, outputFolder, nrm);
                if (resized != tex) { variant.SetTexture(prop, resized); any = true; }
            }

            if (!any)
            {
                Object.DestroyImmediate(variant);
                return src;
            }
            AssetDatabase.CreateAsset(variant, AssetDatabase.GenerateUniqueAssetPath(relPath));
            return variant;
        }

        public static void ClearCache() => cache.Clear();
    }
}
