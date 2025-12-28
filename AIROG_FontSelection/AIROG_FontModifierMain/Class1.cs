using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AIROG_FontModifier
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class FontModifierPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.antigravity.airog.fontmodifier";
        public const string PLUGIN_NAME = "Font Modifier (AssetBundle Support)";
        public const string PLUGIN_VERSION = "1.1.2";

        internal static ManualLogSource Log;

        private static ConfigEntry<string> _fontName;
        private static ConfigEntry<int> _maxCustomFonts;

        // Active Font Assets
        private static TMP_FontAsset _activeTmpFontAsset;

        // Loaded Resources
        private static List<TMP_FontAsset> _loadedCustomFonts = new List<TMP_FontAsset>();
        private static List<AssetBundle> _loadedBundles = new List<AssetBundle>();

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

            Harmony.CreateAndPatchAll(typeof(FontModifierPlugin));
            Log.LogInfo($"Plugin {PLUGIN_GUID} loaded.");
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

                        var assets = bundle.LoadAllAssets<TMP_FontAsset>();
                        if (assets.Length > 0)
                        {
                            _loadedBundles.Add(bundle);
                            _loadedCustomFonts.AddRange(assets);
                            Log.LogInfo($"[FontMod] SUCCESS: Loaded {assets.Length} fonts from bundle: {Path.GetFileName(file)}");
                        }
                        else
                        {
                            // If no fonts found, unload to save memory
                            bundle.Unload(true);
                            Log.LogWarning($"[FontMod] Bundle {Path.GetFileName(file)} loaded but contained no TMP_FontAsset objects.");
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

            // 1. Check Custom Loaded Bundles
            _activeTmpFontAsset = _loadedCustomFonts.FirstOrDefault(f => f.name.Equals(fontName, StringComparison.OrdinalIgnoreCase));

            // Fallback logic
            if (_activeTmpFontAsset == null)
            {
                if (_loadedCustomFonts.Count > 0)
                {
                    Log.LogWarning($"[FontMod] Custom font '{fontName}' not found. Defaulting to first loaded font: {_loadedCustomFonts[0].name}");
                    _activeTmpFontAsset = _loadedCustomFonts[0];
                    _fontName.Value = _activeTmpFontAsset.name;
                    fontName = _activeTmpFontAsset.name;
                }
                else
                {
                    Log.LogError($"[FontMod] Could not apply font: {fontName}. No custom fonts loaded? Please verify AssetBundles in StreamingAssets/Fonts.");
                }
            }

            if (_activeTmpFontAsset != null)
            {
                // Detailed Debugging & Repair
                if (_activeTmpFontAsset.material == null)
                {
                    Log.LogWarning($"[FontMod] Font '{_activeTmpFontAsset.name}' has NULL Material. Attempting to find one in loaded bundles...");
                    // Try to find any matching material in the bundle that loaded this font
                    foreach (var bundle in _loadedBundles)
                    {
                        var materials = bundle.LoadAllAssets<Material>();
                        var matchingMat = materials.FirstOrDefault(m => m.name.Contains(_activeTmpFontAsset.name) || m.name.Contains("SDF"));
                        if (matchingMat != null)
                        {
                            Log.LogInfo($"[FontMod] Found candidate material: {matchingMat.name}. Assigning to font.");
                            _activeTmpFontAsset.material = matchingMat;
                            break;
                        }
                    }
                }

                if (_activeTmpFontAsset.atlasTexture == null)
                {
                    Log.LogWarning($"[FontMod] Font '{_activeTmpFontAsset.name}' has NULL Atlas Texture. Attempting to find one in loaded bundles...");
                    foreach (var bundle in _loadedBundles)
                    {
                        var textures = bundle.LoadAllAssets<Texture2D>();
                        var matchingTex = textures.FirstOrDefault(t => t.name.Contains(_activeTmpFontAsset.name) || t.name.Contains("Atlas"));
                        if (matchingTex != null)
                        {
                            Log.LogInfo($"[FontMod] Found candidate texture: {matchingTex.name}. Assigning to font.");
                            _activeTmpFontAsset.atlasTexture = matchingTex;
                            // Ensure material also uses this texture if possible
                            if (_activeTmpFontAsset.material != null)
                            {
                                _activeTmpFontAsset.material.mainTexture = matchingTex;
                            }
                            break;
                        }
                    }
                }

                // Re-Check after repair
                if (_activeTmpFontAsset.material == null || _activeTmpFontAsset.atlasTexture == null)
                {
                    Log.LogError($"[FontMod] CRITICAL: Font '{_activeTmpFontAsset.name}' is still missing components (Mat: {_activeTmpFontAsset.material != null}, Tex: {_activeTmpFontAsset.atlasTexture != null}). Check AssetBundle build.");
                    // Return to avoid crash
                    return;
                }

                // IMPORTANT: Ensure Fallback list is initialized to prevent errors if glyphs are missing
                if (_activeTmpFontAsset.fallbackFontAssetTable == null)
                {
                    _activeTmpFontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
                }

                // Optional: Add default TMP font as fallback if list is empty to ensure basic chars render
                if (_activeTmpFontAsset.fallbackFontAssetTable.Count == 0)
                {
                    // Try to find a default font in resources
                    var defaultFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name.Contains("Liberation") || f.name.Contains("SDF"));
                    if (defaultFont != null && defaultFont != _activeTmpFontAsset)
                    {
                        _activeTmpFontAsset.fallbackFontAssetTable.Add(defaultFont);
                    }
                }

                UpdateAllText();
            }
        }

        private static void UpdateAllText()
        {
            if (_activeTmpFontAsset == null) return;

            var tmps = Resources.FindObjectsOfTypeAll<TMP_Text>();
            foreach (var t in tmps)
            {
                if (t.gameObject.activeInHierarchy)
                {
                    t.font = _activeTmpFontAsset;
                }
            }
        }

        // Hook for new text objects
        [HarmonyPatch(typeof(TMP_Text), "OnEnable")]
        [HarmonyPatch(typeof(TMP_Text), "Awake")]
        public static class Patch_TMP_Text_OnEnable
        {
            [HarmonyPostfix]
            public static void Postfix(TMP_Text __instance)
            {
                if (_activeTmpFontAsset != null && __instance.font != _activeTmpFontAsset)
                {
                    __instance.font = _activeTmpFontAsset;
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
