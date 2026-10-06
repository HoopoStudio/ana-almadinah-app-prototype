// Fix Pink Materials
// Converts Built-in pipeline materials (Standard, Legacy, Mobile) to the Lit shader of the
// active render pipeline (HDRP or URP), keeping textures, colors, cutout and emission.
//
// Usage: put this file in any "Editor" folder under Assets (e.g. Assets/Editor/), then
//   Tools > Fix Pink Materials > Convert All Materials In Project
// or right-click materials/folders in the Project window > Fix Pink Materials (Selected).
// Back up your project (or commit in Unity VCS) first: the conversion edits .mat files in place.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AnaAlMadinah.EditorTools
{
    public static class FixPinkMaterials
    {
        const string Title = "Fix Pink Materials";

        static readonly HashSet<string> BuiltInLitShaders = new HashSet<string>
        {
            "Standard",
            "Standard (Specular setup)",
            "Autodesk Interactive",
        };

        enum Pipeline { BuiltIn, URP, HDRP }
        enum Surface { Opaque, Cutout, Fade, Transparent }

        [MenuItem("Tools/Fix Pink Materials/Convert All Materials In Project")]
        static void ConvertAll()
        {
            var materials = AssetDatabase.FindAssets("t:Material", new[] { "Assets" })
                .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                .Where(IsMaterialFile)
                .Select(path => AssetDatabase.LoadAssetAtPath<Material>(path));
            Run(materials);
        }

        [MenuItem("Tools/Fix Pink Materials/Convert Selected Materials Or Folders")]
        [MenuItem("Assets/Fix Pink Materials (Selected)")]
        static void ConvertSelected()
        {
            var materials = Selection.GetFiltered<Material>(SelectionMode.DeepAssets)
                .Where(m => IsMaterialFile(AssetDatabase.GetAssetPath(m)));
            Run(materials);
        }

        static bool IsMaterialFile(string path) =>
            path.StartsWith("Assets/", StringComparison.Ordinal) &&
            path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);

        static void Run(IEnumerable<Material> materials)
        {
            var pipeline = DetectPipeline();
            if (pipeline == Pipeline.BuiltIn)
            {
                EditorUtility.DisplayDialog(Title,
                    "No URP/HDRP pipeline asset is active (Project Settings > Graphics / Quality).\n" +
                    "Built-in materials should already render, nothing to convert.", "OK");
                return;
            }

            var lit = Shader.Find(pipeline == Pipeline.HDRP ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
            if (lit == null)
            {
                EditorUtility.DisplayDialog(Title, $"Could not find the {pipeline} Lit shader.", "OK");
                return;
            }

            var todo = materials.Where(m => m != null && NeedsConversion(m)).Distinct().ToList();
            if (todo.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "No Built-in materials found to convert.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(Title,
                    $"Convert {todo.Count} material(s) to \"{lit.name}\"?\n\n" +
                    "Make sure you have a backup or a Unity VCS commit first.", "Convert", "Cancel"))
                return;

            Undo.RecordObjects(todo.ToArray(), Title);
            int converted = 0;
            try
            {
                for (int i = 0; i < todo.Count; i++)
                {
                    var m = todo[i];
                    if (EditorUtility.DisplayCancelableProgressBar(Title, m.name, (float)i / todo.Count))
                        break;
                    try
                    {
                        var props = SavedProps.Read(m);
                        if (pipeline == Pipeline.HDRP) ToHdrp(m, lit, props);
                        else ToUrp(m, lit, props);
                        EditorUtility.SetDirty(m);
                        converted++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[{Title}] {AssetDatabase.GetAssetPath(m)}: {e}", m);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"[{Title}] Converted {converted}/{todo.Count} material(s) to {lit.name}.");
        }

        static Pipeline DetectPipeline()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return Pipeline.BuiltIn;
            var typeName = rp.GetType().Name;
            if (typeName.Contains("HDRenderPipeline")) return Pipeline.HDRP;
            if (typeName.Contains("Universal")) return Pipeline.URP;
            return Pipeline.BuiltIn;
        }

        static bool NeedsConversion(Material m)
        {
            var shader = m.shader;
            if (shader == null || shader.name == "Hidden/InternalErrorShader") return true;
            var n = shader.name;
            if (n.Contains("Particles")) return false;
            return BuiltInLitShaders.Contains(n) ||
                   n.StartsWith("Legacy Shaders/", StringComparison.Ordinal) ||
                   n.StartsWith("Mobile/", StringComparison.Ordinal);
        }

        // Values common to both pipelines, read from the original Built-in material.
        class Source
        {
            public Surface Surface;
            public bool Specular;
            public Texture Albedo;
            public Vector2 Tiling = Vector2.one, Offset = Vector2.zero;
            public Color Color;
            public Texture Normal;
            public float NormalScale;
            public Texture GlossMap; // _MetallicGlossMap, or _SpecGlossMap in specular setup
            public float Metallic, Smoothness, GlossMapScale;
            public bool SmoothnessFromAlbedo;
            public Color SpecColor;
            public Texture Occlusion;
            public float OcclusionStrength;
            public Texture EmissionMap;
            public Color EmissionColor;
            public float Cutoff;

            public bool Emissive => EmissionMap != null || EmissionColor.maxColorComponent > 0.0001f;

            public Source(SavedProps p)
            {
                var name = p.ShaderName;
                Specular = name == "Standard (Specular setup)";
                if (name.Contains("Cutout")) Surface = Surface.Cutout;
                else if (name.StartsWith("Legacy Shaders/Transparent", StringComparison.Ordinal)) Surface = Surface.Fade;
                else Surface = (Surface)Mathf.Clamp(Mathf.RoundToInt(p.F("_Mode", 0)), 0, 3);

                if (p.Tex.TryGetValue("_MainTex", out var main))
                {
                    Albedo = main.tex;
                    Tiling = main.scale;
                    Offset = main.offset;
                }
                Color = p.C("_Color", Color.white);
                Normal = p.T("_BumpMap");
                NormalScale = p.F("_BumpScale", 1);
                GlossMap = p.T(Specular ? "_SpecGlossMap" : "_MetallicGlossMap");
                Metallic = Specular ? 0 : p.F("_Metallic", 0);
                Smoothness = p.F("_Glossiness", 0.2f);
                GlossMapScale = p.F("_GlossMapScale", 1);
                SmoothnessFromAlbedo = p.F("_SmoothnessTextureChannel", 0) >= 0.5f;
                SpecColor = p.C("_SpecColor", new Color(0.2f, 0.2f, 0.2f));
                Occlusion = p.T("_OcclusionMap");
                OcclusionStrength = p.F("_OcclusionStrength", 1);
                EmissionMap = p.T("_EmissionMap");
                EmissionColor = p.C("_EmissionColor", Color.black);
                Cutoff = p.F("_Cutoff", 0.5f);
            }
        }

        // ---------------- URP ----------------

        static void ToUrp(Material m, Shader lit, SavedProps p)
        {
            var s = new Source(p);
            m.shader = lit;

            m.SetTexture("_BaseMap", s.Albedo);
            m.SetTextureScale("_BaseMap", s.Tiling);
            m.SetTextureOffset("_BaseMap", s.Offset);
            m.SetColor("_BaseColor", s.Color);

            m.SetTexture("_BumpMap", s.Normal);
            m.SetFloat("_BumpScale", s.NormalScale);

            m.SetFloat("_WorkflowMode", s.Specular ? 0 : 1);
            if (s.Specular)
            {
                m.SetTexture("_SpecGlossMap", s.GlossMap);
                m.SetColor("_SpecColor", s.SpecColor);
            }
            else
            {
                m.SetTexture("_MetallicGlossMap", s.GlossMap);
            }
            m.SetFloat("_Metallic", s.Metallic);
            // URP multiplies the map/albedo alpha by _Smoothness, like Standard's _GlossMapScale.
            m.SetFloat("_Smoothness", s.GlossMap != null || s.SmoothnessFromAlbedo ? s.GlossMapScale : s.Smoothness);
            m.SetFloat("_SmoothnessTextureChannel", s.SmoothnessFromAlbedo ? 1 : 0);

            m.SetTexture("_OcclusionMap", s.Occlusion);
            m.SetFloat("_OcclusionStrength", s.OcclusionStrength);

            m.SetTexture("_EmissionMap", s.EmissionMap);
            m.SetColor("_EmissionColor", s.Emissive ? s.EmissionColor : Color.black);
            m.globalIlluminationFlags = s.Emissive
                ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                : MaterialGlobalIlluminationFlags.EmissiveIsBlack;

            bool transparent = s.Surface == Surface.Fade || s.Surface == Surface.Transparent;
            m.SetFloat("_Surface", transparent ? 1 : 0);
            m.SetFloat("_Blend", s.Surface == Surface.Transparent ? 1 : 0); // 0 Alpha, 1 Premultiply
            m.SetFloat("_AlphaClip", s.Surface == Surface.Cutout ? 1 : 0);
            m.SetFloat("_Cutoff", s.Cutoff);

            if (!TryUrpSetKeywords(m))
                UrpManualKeywords(m, s, transparent);
        }

        static bool TryUrpSetKeywords(Material m)
        {
            try
            {
                var baseGui = FindType("UnityEditor.BaseShaderGUI");
                var litGui = FindType("UnityEditor.Rendering.Universal.ShaderGUI.LitGUI");
                var setKeywords = baseGui?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(mi => mi.Name == "SetMaterialKeywords" && mi.GetParameters().Length == 3);
                var litKeywords = litGui?.GetMethod("SetMaterialKeywords",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null);
                if (setKeywords == null) return false;
                var litFunc = litKeywords != null
                    ? (Action<Material>)Delegate.CreateDelegate(typeof(Action<Material>), litKeywords)
                    : null;
                setKeywords.Invoke(null, new object[] { m, litFunc, null });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Title}] URP keyword setup failed, using fallback: {e.Message}");
                return false;
            }
        }

        static void UrpManualKeywords(Material m, Source s, bool transparent)
        {
            SetKeyword(m, "_NORMALMAP", s.Normal != null);
            SetKeyword(m, "_METALLICSPECGLOSSMAP", s.GlossMap != null);
            SetKeyword(m, "_SPECULAR_SETUP", s.Specular);
            SetKeyword(m, "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", s.SmoothnessFromAlbedo);
            SetKeyword(m, "_OCCLUSIONMAP", s.Occlusion != null);
            SetKeyword(m, "_EMISSION", s.Emissive);
            SetKeyword(m, "_ALPHATEST_ON", s.Surface == Surface.Cutout);
            SetKeyword(m, "_SURFACE_TYPE_TRANSPARENT", transparent);
            SetKeyword(m, "_ALPHAPREMULTIPLY_ON", s.Surface == Surface.Transparent);

            m.SetFloat("_SrcBlend", transparent ? (s.Surface == Surface.Transparent ? 1 : 5) : 1); // One / SrcAlpha
            m.SetFloat("_DstBlend", transparent ? 10 : 0); // OneMinusSrcAlpha / Zero
            m.SetFloat("_ZWrite", transparent ? 0 : 1);
            m.renderQueue = transparent ? (int)RenderQueue.Transparent
                : s.Surface == Surface.Cutout ? (int)RenderQueue.AlphaTest
                : (int)RenderQueue.Geometry;
        }

        // ---------------- HDRP ----------------

        static void ToHdrp(Material m, Shader lit, SavedProps p)
        {
            var s = new Source(p);
            // Build the mask map before switching shaders so a failure leaves the material untouched.
            Texture2D mask = NeedsMaskMap(s) ? BuildMaskMap(m, s) : null;

            m.shader = lit;

            m.SetTexture("_BaseColorMap", s.Albedo);
            m.SetTextureScale("_BaseColorMap", s.Tiling);
            m.SetTextureOffset("_BaseColorMap", s.Offset);
            m.SetColor("_BaseColor", s.Color);

            m.SetTexture("_NormalMap", s.Normal);
            m.SetFloat("_NormalScale", s.NormalScale);

            m.SetFloat("_Metallic", s.Metallic);
            m.SetFloat("_Smoothness", s.Smoothness);
            m.SetTexture("_MaskMap", mask);
            if (mask != null)
            {
                m.SetFloat("_MetallicRemapMin", 0);
                m.SetFloat("_MetallicRemapMax", 1);
                m.SetFloat("_SmoothnessRemapMin", 0);
                m.SetFloat("_SmoothnessRemapMax", 1);
                m.SetFloat("_AORemapMin", 0);
                m.SetFloat("_AORemapMax", 1);
            }

            m.SetTexture("_EmissiveColorMap", s.EmissionMap);
            m.SetFloat("_UseEmissiveIntensity", 0);
            m.SetColor("_EmissiveColorLDR", s.Emissive ? s.EmissionColor : Color.black);
            m.SetColor("_EmissiveColor", s.Emissive ? s.EmissionColor.linear : Color.black);
            m.globalIlluminationFlags = s.Emissive
                ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                : MaterialGlobalIlluminationFlags.EmissiveIsBlack;

            bool transparent = s.Surface == Surface.Fade || s.Surface == Surface.Transparent;
            m.SetFloat("_SurfaceType", transparent ? 1 : 0);
            m.SetFloat("_BlendMode", s.Surface == Surface.Transparent ? 4 : 0); // 4 Premultiply, 0 Alpha
            m.SetFloat("_AlphaCutoffEnable", s.Surface == Surface.Cutout ? 1 : 0);
            m.SetFloat("_AlphaCutoff", s.Cutoff);
            m.SetFloat("_AlphaCutoffShadow", s.Cutoff);

            bool validated =
                TryInvokeStatic("UnityEngine.Rendering.HighDefinition.HDMaterial", "ValidateMaterial", m) ||
                TryInvokeStatic("UnityEditor.Rendering.HighDefinition.HDShaderUtils", "ResetMaterialKeywords", m);
            if (!validated)
            {
                SetKeyword(m, "_NORMALMAP", s.Normal != null);
                SetKeyword(m, "_MASKMAP", mask != null);
                SetKeyword(m, "_EMISSIVE_COLOR_MAP", s.EmissionMap != null);
                SetKeyword(m, "_ALPHATEST_ON", s.Surface == Surface.Cutout);
                SetKeyword(m, "_SURFACE_TYPE_TRANSPARENT", transparent);
                m.renderQueue = transparent ? (int)RenderQueue.Transparent
                    : s.Surface == Surface.Cutout ? (int)RenderQueue.AlphaTest
                    : (int)RenderQueue.Geometry;
                Debug.LogWarning($"[{Title}] Could not run HDRP material validation on {m.name}; " +
                                 "select it once in the Inspector if it looks wrong.", m);
            }
        }

        // HDRP ignores occlusion and per-pixel metallic/smoothness unless they are packed into a mask map
        // (R metallic, G occlusion, B detail mask, A smoothness).
        static bool NeedsMaskMap(Source s) =>
            s.Occlusion != null || s.GlossMap != null || (s.SmoothnessFromAlbedo && s.Albedo != null);

        static Texture2D BuildMaskMap(Material m, Source s)
        {
            var albedo = s.SmoothnessFromAlbedo ? s.Albedo : null;
            int w = 0, h = 0;
            foreach (var t in new[] { s.GlossMap, s.Occlusion, albedo })
            {
                if (t == null) continue;
                w = Mathf.Max(w, t.width);
                h = Mathf.Max(h, t.height);
            }
            w = Mathf.Clamp(w, 1, 4096);
            h = Mathf.Clamp(h, 1, 4096);

            var gloss = s.GlossMap != null ? ReadPixels(s.GlossMap, w, h) : null;
            var ao = s.Occlusion != null ? ReadPixels(s.Occlusion, w, h) : null;
            var alb = albedo != null ? ReadPixels(albedo, w, h) : null;

            byte metallicConst = ToByte(s.Metallic);
            byte smoothConst = ToByte(s.Smoothness);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                byte r = gloss != null && !s.Specular ? gloss[i].r : metallicConst;
                byte g = ao != null ? ToByte(Mathf.Lerp(1f, ao[i].g / 255f, s.OcclusionStrength)) : (byte)255;
                byte a;
                if (alb != null) a = ToByte(alb[i].a / 255f * s.GlossMapScale);
                else if (gloss != null) a = ToByte(gloss[i].a / 255f * s.GlossMapScale);
                else a = smoothConst;
                px[i] = new Color32(r, g, 255, a);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(px);
            tex.Apply();
            var png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            var dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(m)).Replace('\\', '/');
            var path = $"{dir}/{m.name}_MaskMap.png";
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.sRGBTexture = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Reads any texture (readable or not, compressed or not) through the GPU.
        static Color32[] ReadPixels(Texture src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                var px = tex.GetPixels32();
                UnityEngine.Object.DestroyImmediate(tex);
                return px;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ---------------- helpers ----------------

        static byte ToByte(float v) => (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);

        static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword);
            else m.DisableKeyword(keyword);
        }

        static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);

        static bool TryInvokeStatic(string typeName, string methodName, Material m)
        {
            var method = FindType(typeName)?.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(Material) }, null);
            if (method == null) return false;
            try
            {
                method.Invoke(null, new object[] { m });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Title}] {typeName}.{methodName} failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        // Reads the serialized property values of a material regardless of its current shader,
        // so materials whose shader is missing still keep their textures.
        class SavedProps
        {
            public readonly Dictionary<string, (Texture tex, Vector2 scale, Vector2 offset)> Tex =
                new Dictionary<string, (Texture, Vector2, Vector2)>();
            public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
            public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();
            public string ShaderName = "";

            public Texture T(string name) => Tex.TryGetValue(name, out var t) ? t.tex : null;
            public float F(string name, float fallback) => Floats.TryGetValue(name, out var v) ? v : fallback;
            public Color C(string name, Color fallback) => Colors.TryGetValue(name, out var c) ? c : fallback;

            public static SavedProps Read(Material m)
            {
                var p = new SavedProps();
                if (m.shader != null) p.ShaderName = m.shader.name;

                var so = new SerializedObject(m);
                var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
                for (int i = 0; texEnvs != null && i < texEnvs.arraySize; i++)
                {
                    var e = texEnvs.GetArrayElementAtIndex(i);
                    p.Tex[e.FindPropertyRelative("first").stringValue] = (
                        e.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture,
                        e.FindPropertyRelative("second.m_Scale").vector2Value,
                        e.FindPropertyRelative("second.m_Offset").vector2Value);
                }
                var floats = so.FindProperty("m_SavedProperties.m_Floats");
                for (int i = 0; floats != null && i < floats.arraySize; i++)
                {
                    var e = floats.GetArrayElementAtIndex(i);
                    p.Floats[e.FindPropertyRelative("first").stringValue] = e.FindPropertyRelative("second").floatValue;
                }
                var colors = so.FindProperty("m_SavedProperties.m_Colors");
                for (int i = 0; colors != null && i < colors.arraySize; i++)
                {
                    var e = colors.GetArrayElementAtIndex(i);
                    p.Colors[e.FindPropertyRelative("first").stringValue] = e.FindPropertyRelative("second").colorValue;
                }
                return p;
            }
        }
    }
}
