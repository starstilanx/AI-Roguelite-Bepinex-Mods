using System;
using System.Reflection;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AIROG_FontModifier
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class FontModifierPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.antigravity.airog.fontmodifier.v3";
        public const string PLUGIN_NAME = "Font Modifier V3";
        public const string PLUGIN_VERSION = "2.2.1";
        
        internal static ManualLogSource Log;

        private static ConfigEntry<string> _fontName;
        private static ConfigEntry<int> _maxCustomFonts;
        
        // Active Font Assets
        private static TMP_FontAsset _activeTmpFontAsset;
        private static Font _activeLegacyFont;

        // Loaded Resources
        private static List<TMP_FontAsset> _loadedCustomFonts = new List<TMP_FontAsset>();
        private static List<AssetBundle> _loadedBundles = new List<AssetBundle>();
        private static List<Font> _loadedLegacyFonts = new List<Font>();
        
        // P/Invoke for broadcasting changes
        [DllImport("user32.dll")]
        public static extern int PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        
        private const uint WM_FONTCHANGE = 0x001D;
        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

        private bool _uiInjected = false;
        private GameObject _fontDropdownObj;

        private void Awake()
        {
            Log = Logger;
            _fontName = Config.Bind("General", "FontName", "Arial", "The name of the font to use.");
            _maxCustomFonts = Config.Bind("General", "MaxCustomFonts", 20, "Max fonts to load.");

            LoadFonts();
            
            SceneManager.sceneLoaded += OnSceneLoaded;
            
            Harmony.CreateAndPatchAll(typeof(FontModifierPlugin));
            Log.LogInfo($"Plugin {PLUGIN_GUID} loaded.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
             Log.LogInfo($"[FontMod] Scene Loaded: {scene.name}. Refreshing Font...");
             if (_fontName != null && !string.IsNullOrEmpty(_fontName.Value) && _fontName.Value != "Default")
             {
                 ApplyFont(_fontName.Value);
             }
        }

        private void OnDestroy()
        {
            foreach (var bundle in _loadedBundles)
            {
                if (bundle != null) bundle.Unload(true);
            }
            _loadedBundles.Clear();
            _loadedCustomFonts.Clear();
        }

        private void LoadFonts()
        {
            string fontsPath = Path.Combine(Application.streamingAssetsPath, "Fonts");
            if (!Directory.Exists(fontsPath))
            {
                Directory.CreateDirectory(fontsPath);
                return;
            }

            // detailed scan
            var files = Directory.GetFiles(fontsPath);
            Log.LogInfo($"[FontMod] Scanning {files.Length} files in StreamingAssets/Fonts");

            foreach (var file in files)
            {
                string ext = Path.GetExtension(file);
                if (string.Equals(ext, ".meta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".ttf", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".otf", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                if (string.Equals(ext, ".unitypackage", StringComparison.OrdinalIgnoreCase)) 
                {
                     Log.LogWarning($"[FontMod] Found .unitypackage: {Path.GetFileName(file)}. This cannot be loaded at runtime. Please extract/build it into an AssetBundle.");
                     continue;
                }
                
                // Try Load as AssetBundle
                try
                {
                    var bundle = AssetBundle.LoadFromFile(file);
                    if (bundle != null)
                    {
                        // DEBUG: Inspect Bundle Contents
                        var allAssetNames = bundle.GetAllAssetNames();
                        Log.LogInfo($"[FontMod] Bundle {Path.GetFileName(file)} contains {allAssetNames.Length} assets:");
                        foreach (var name in allAssetNames)
                        {
                            Log.LogInfo($"[FontMod] - {name}");
                        }

                        var tmps = bundle.LoadAllAssets<TMP_FontAsset>();
                        var legacys = bundle.LoadAllAssets<Font>();

                        if (tmps.Length > 0 || legacys.Length > 0)
                        {
                            _loadedBundles.Add(bundle);
                            if (tmps.Length > 0) _loadedCustomFonts.AddRange(tmps);
                            if (legacys.Length > 0) _loadedLegacyFonts.AddRange(legacys);
                            
                            Log.LogInfo($"[FontMod] Loaded {tmps.Length} TMP Fonts and {legacys.Length} Legacy Fonts from {Path.GetFileName(file)}");
                        }
                        else
                        {
                            // If no fonts found, unload to save memory
                            bundle.Unload(true);
                        }
                    }
                    else
                    {
                        if (string.Equals(ext, ".asset", StringComparison.OrdinalIgnoreCase))
                        {
                            Log.LogError($"[FontMod] Failed to load .asset file: {Path.GetFileName(file)}. Raw .asset files cannot be loaded. They must be built into an AssetBundle.");
                        }
                        else
                        {
                            Log.LogWarning($"[FontMod] File {Path.GetFileName(file)} is not a valid AssetBundle.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"[FontMod] Exception loading bundle {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            
            // Notify system of font changes (standard practice even if we didn't add OS fonts, to trigger UI updates if necessary)
            PostMessage(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero);
            
            // Initial Apply
            ApplyFont(_fontName.Value);
        }

        private void Update()
        {
            if (_uiInjected && _fontDropdownObj == null) _uiInjected = false;
            if (_uiInjected) return;

            var mainMenu = FindObjectOfType<MainMenu>();
            if (mainMenu != null)
            {
                InjectUI(mainMenu);
            }
        }

        private void ApplyFont(string fontName)
        {
            Log.LogInfo($"[FontMod] Applying font: {fontName}");
            _activeTmpFontAsset = null;
            _activeLegacyFont = null;

            // TMP Search
            _activeTmpFontAsset = _loadedCustomFonts.FirstOrDefault(f => f.name.Equals(fontName, StringComparison.OrdinalIgnoreCase));
            
            // Legacy Search
            string simpler = fontName.Replace(" SDF", "").Replace(" Material", "").Trim();
            _activeLegacyFont = _loadedLegacyFonts.FirstOrDefault(f => f.name.IndexOf(simpler, StringComparison.OrdinalIgnoreCase) >= 0);
             
            // Fallback Legacy: Try to extract from TMP Asset
            if (_activeLegacyFont == null && _activeTmpFontAsset != null && _activeTmpFontAsset.sourceFontFile != null)
            {
                _activeLegacyFont = _activeTmpFontAsset.sourceFontFile;
                Log.LogInfo($"[FontMod] Extracted Legacy Font from TMP Asset: {_activeLegacyFont.name}");
            }

            // Fallback Legacy: Try first loaded
            if (_activeLegacyFont == null && _loadedLegacyFonts.Count > 0) 
            {
                _activeLegacyFont = _loadedLegacyFonts[0];
            }

            if (_activeLegacyFont != null) Log.LogInfo($"[FontMod] Found Legacy Font: {_activeLegacyFont.name}");

            // Fallback TMP
            if (_activeTmpFontAsset == null)
            {
               if (_loadedCustomFonts.Count > 0)
               {
                   Log.LogWarning($"[FontMod] Custom font '{fontName}' not found. Defaulting to first loaded font: {_loadedCustomFonts[0].name}");
                   _activeTmpFontAsset = _loadedCustomFonts[0];
                   _fontName.Value = _activeTmpFontAsset.name;
                   // fontName = _activeTmpFontAsset.name; // Keep intent?
               }
               else
               {
                   Log.LogError($"[FontMod] Could not apply font: {fontName}. No custom fonts loaded.");
               }
            }
            
            // Should prompt update regardless of TMP success if Legacy found
            if (_activeTmpFontAsset != null || _activeLegacyFont != null)
            {
                 if (_activeTmpFontAsset != null)
                 {
                     Log.LogInfo($"[FontMod] STARTING AGGRESSIVE REPAIR for: '{_activeTmpFontAsset.name}'");


                // 1. MATERIAL REPAIR (Unconditional)
                bool matFound = false;
                foreach (var bundle in _loadedBundles)
                {
                    var materials = bundle.LoadAllAssets<Material>();
                    var match = materials.FirstOrDefault(m => m.name.IndexOf(_activeTmpFontAsset.name, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match == null) {
                         string simple = _activeTmpFontAsset.name.Replace(" SDF", "");
                         match = materials.FirstOrDefault(m => m.name.IndexOf(simple, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (match != null)
                    {
                        Log.LogInfo($"[FontMod] Force-Assigning Material: {match.name}");
                        _activeTmpFontAsset.material = match;
                        matFound = true;
                        break; 
                    }
                }
                
                if (!matFound) {
                     // Check if it already has one?
                     if (_activeTmpFontAsset.material != null) matFound = true;
                     else Log.LogWarning($"[FontMod] WARNING: Could not find matching Material for {_activeTmpFontAsset.name} in bundles.");
                }

                // 2. TEXTURE REPAIR (Unconditional)
                bool texFound = false;
                foreach (var bundle in _loadedBundles)
                {
                    var textures = bundle.LoadAllAssets<Texture2D>();
                    var match = textures.FirstOrDefault(t => t.name.IndexOf(_activeTmpFontAsset.name, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match == null) {
                         string simple = _activeTmpFontAsset.name.Replace(" SDF", "");
                         match = textures.FirstOrDefault(t => t.name.IndexOf(simple, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (match != null)
                    {
                        Log.LogInfo($"[FontMod] Force-Assigning Texture: {match.name} via Reflection");
                        
                        // Reflection
                        var singleTex = typeof(TMP_FontAsset).GetField("m_AtlasTexture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (singleTex != null) singleTex.SetValue(_activeTmpFontAsset, match);
                        
                        var arrayTex = typeof(TMP_FontAsset).GetField("m_AtlasTextures", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (arrayTex != null) arrayTex.SetValue(_activeTmpFontAsset, new Texture2D[] { match });

                        // Ensure Material has it too
                        if (_activeTmpFontAsset.material != null)
                        {
                            _activeTmpFontAsset.material.mainTexture = match;
                            // DO NOT set _FaceTex or _OutlineTex to the atlas! That creates the "texture inside text" artifact.
                        }
                        
                        texFound = true;
                        break; 
                    }
                }
                
                if (!texFound) {
                     // If we couldn't find a MATCHING one, we try to trust the existing one?
                     // No, if the user sees invisible text, the existing one is bad.
                     // But let's give it a chance if it's not null, just in case our name search failed.
                     if (_activeTmpFontAsset.atlasTexture != null) 
                     {
                         Log.LogWarning($"[FontMod] WARNING: Could not find matching Texture for {_activeTmpFontAsset.name} in bundles. Keeping existing (potentially broken) texture.");
                         texFound = true; 
                     }
                     else 
                     {
                         Log.LogWarning($"[FontMod] WARNING: Could not find matching Texture for {_activeTmpFontAsset.name} in bundles. Font has no texture.");
                     }
                }

                // Re-Check
                if (!matFound || !texFound)
                {
                     Log.LogError($"[FontMod] CRITICAL: Failed to repair font '{_activeTmpFontAsset.name}'. (MatFound: {matFound}, TexFound: {texFound}). Removing and trying next.");
                     
                    // Remove broken font
                    _loadedCustomFonts.Remove(_activeTmpFontAsset);
                    _activeTmpFontAsset = null;

                    // Try next best
                    if (_loadedCustomFonts.Count > 0)
                    {
                        ApplyFont(_loadedCustomFonts[0].name);
                    }
                    else
                    {
                         Log.LogError("[FontMod] No valid custom fonts left to apply.");
                    }
                    return;
                }
                
                // IMPORTANT: Ensure Fallback list is initialized
                if (_activeTmpFontAsset.fallbackFontAssetTable == null)
                {
                    _activeTmpFontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
                }
                
                // Optional: Add default TMP font as fallback
                if (_activeTmpFontAsset.fallbackFontAssetTable.Count == 0)
                {
                     // Try to find a default font in resources
                     var defaultFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name.Contains("Liberation") || f.name.Contains("SDF"));
                     if (defaultFont != null && defaultFont != _activeTmpFontAsset)
                     {
                         _activeTmpFontAsset.fallbackFontAssetTable.Add(defaultFont);
                     }
                }
                
                // SHADER SWAP
                var validShader = Shader.Find("TextMeshPro/Distance Field");
                if (validShader != null) {
                    _activeTmpFontAsset.material.shader = validShader;
                    Log.LogInfo("[FontMod] Swapped material shader to local 'TextMeshPro/Distance Field'");
                }

                // Force Static
                _activeTmpFontAsset.atlasPopulationMode = AtlasPopulationMode.Static;

                // METADATA REPAIR (Fix invisible text due to zero layout data)
                if (_activeTmpFontAsset.faceInfo.pointSize == 0 || _activeTmpFontAsset.faceInfo.lineHeight == 0)
                {
                    Log.LogWarning($"[FontMod] Broken FaceInfo detected (Size: {_activeTmpFontAsset.faceInfo.pointSize}, LH: {_activeTmpFontAsset.faceInfo.lineHeight}). Repairing...");
                    // FaceInfo is a struct in most TMP versions, so we must copy-modify-assign
                    var face = _activeTmpFontAsset.faceInfo; 
                    // Setting PointSize higher (e.g. 90) makes the text render SMALLER in the UI 
                    // because TMP scales it down (RequestedSize / NativeSize).
                    face.pointSize = 90;
                    face.lineHeight = 110f; // More vertical spacer
                    face.ascentLine = 75f;
                    face.capLine = 70f;
                    face.meanLine = 40f;
                    face.descentLine = -25f; // More room for tails
                    face.baseline = 0f;
                    face.scale = 1f;
                    face.baseline = 0f;
                    face.scale = 1f;
                    
                    // Assign back
                    _activeTmpFontAsset.faceInfo = face; 
                    Log.LogInfo("[FontMod] FaceInfo repaired with default values (Size 36).");
                }

                // DIAGNOSTIC LOGGING
                Log.LogInfo($"[FontMod] DIAGNOSTICS for '{_activeTmpFontAsset.name}':");
                Log.LogInfo($"   - Character Count: {_activeTmpFontAsset.characterTable.Count}");
                Log.LogInfo($"   - Glyph Count: {_activeTmpFontAsset.glyphTable.Count}");
                Log.LogInfo($"   - FaceInfo: Size={_activeTmpFontAsset.faceInfo.pointSize}, Scale={_activeTmpFontAsset.faceInfo.scale}, LH={_activeTmpFontAsset.faceInfo.lineHeight}");
                if (_activeTmpFontAsset.atlasTexture != null)
                   Log.LogInfo($"   - Texture: {_activeTmpFontAsset.atlasTexture.width}x{_activeTmpFontAsset.atlasTexture.height} ({_activeTmpFontAsset.atlasTexture.format})");

                UpdateAllText();
            }
            }
        }

        private static void UpdateAllText()
        {
            // Update TMP
            if (_activeTmpFontAsset != null)
            {
                var tmps = Resources.FindObjectsOfTypeAll<TMP_Text>();
                foreach (var t in tmps)
                {
                    if (t.gameObject.activeInHierarchy) ApplyToText(t);
                }
            }
            
            // Update Legacy
            if (_activeLegacyFont != null)
            {
                var legacys = Resources.FindObjectsOfTypeAll<Text>();
                foreach (var t in legacys)
                {
                    if (t.gameObject.activeInHierarchy) ApplyToLegacy(t);
                }
            }
        }

        private static void ApplyToText(TMP_Text t)
        {
            if (_activeTmpFontAsset == null) return;
            
            t.font = _activeTmpFontAsset;
            if (_activeTmpFontAsset.material != null)
            {
                t.fontSharedMaterial = _activeTmpFontAsset.material; 
            }
        }
        
        private static void ApplyToLegacy(Text t)
        {
             if (_activeLegacyFont == null) return;
             t.font = _activeLegacyFont;
        }

        [HarmonyPatch(typeof(TMP_Text), "OnEnable")]
        [HarmonyPatch(typeof(TMP_Text), "Start")]
        [HarmonyPatch(typeof(TMP_Text), "text", MethodType.Setter)] // Aggressive maintenance
        public static class TMP_Text_Lifecycle_Patch
        {
            public static void Postfix(TMP_Text __instance)
            {
                 if (_activeTmpFontAsset != null && __instance.font != _activeTmpFontAsset)
                 {
                     ApplyToText(__instance);
                 }
            }
        }
        
        [HarmonyPatch(typeof(Text), "OnEnable")]
        [HarmonyPatch(typeof(Text), "Start")]
        [HarmonyPatch(typeof(Text), "text", MethodType.Setter)] // Aggressive maintenance
        public static class LegacyText_Lifecycle_Patch
        {
            public static void Postfix(Text __instance)
            {
                 if (_activeLegacyFont != null && __instance.font != _activeLegacyFont)
                 {
                     ApplyToLegacy(__instance);
                 }
            }
        }


        private void InjectUI(MainMenu mainMenu)
        {
            try
            {
                // Clone Token Dropdown
                var dropdownField = typeof(MainMenu).GetField("textGenerationDropdown", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var sliderField = typeof(MainMenu).GetField("ambienceVolumeSlider", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                
                if (dropdownField == null || sliderField == null) return;

                var refDropdown = dropdownField.GetValue(mainMenu) as TMP_Dropdown;
                var refSlider = sliderField.GetValue(mainMenu) as Slider;

                if (refDropdown == null || refSlider == null) return;

                _fontDropdownObj = Instantiate(refDropdown.gameObject, refSlider.transform.parent);
                _fontDropdownObj.name = "FontSelectorDropdown";

                // Position below
                var rect = _fontDropdownObj.GetComponent<RectTransform>();
                rect.anchoredPosition = refSlider.GetComponent<RectTransform>().anchoredPosition + new Vector2(0, -120f);

                // Populate
                var dropdown = _fontDropdownObj.GetComponent<TMP_Dropdown>();
                dropdown.ClearOptions();

                List<string> options = new List<string>();
                
                // Add custom loaded ONLY
                options.AddRange(_loadedCustomFonts.Select(f => f.name));
                
                if (options.Count == 0)
                {
                    options.Add("No Custom Fonts Found");
                }

                dropdown.AddOptions(options);

                // Select current
                string current = _fontName.Value;
                int idx = options.FindIndex(s => s.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) 
                {
                    dropdown.value = idx;
                }
                else if (options.Count > 0)
                {
                    // Fallback to first if mismatch
                    dropdown.value = 0;
                    if (_loadedCustomFonts.Count > 0)
                    {
                       ApplyFont(_loadedCustomFonts[0].name);
                    }
                }

                dropdown.onValueChanged.RemoveAllListeners();
                dropdown.onValueChanged.AddListener((val) => {
                    if (options.Count > val)
                    {
                        _fontName.Value = options[val];
                        ApplyFont(options[val]);
                    }
                });

                // Add Label
                // (Simplified label creation for brevity, reused from prev code concept if needed)
                
                _uiInjected = true;
                Log.LogInfo("[FontMod] UI Injected");
            }
            catch (Exception ex)
            {
                Log.LogError($"[FontMod] UI Injection Error: {ex}");
                _uiInjected = true; 
            }
        }
    }
}
