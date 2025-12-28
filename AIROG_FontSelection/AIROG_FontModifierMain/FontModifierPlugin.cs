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
        public const string PLUGIN_VERSION = "1.1.0";
        
        internal static ManualLogSource Log;

        private static ConfigEntry<string> _fontName;
        private static ConfigEntry<int> _maxCustomFonts;
        
        // Active Font Assets
        private static TMP_FontAsset _activeTmpFontAsset;
        private static UnityEngine.Font _activeOsFont;

        // Loaded Resources
        private static List<TMP_FontAsset> _loadedCustomFonts = new List<TMP_FontAsset>();
        private static List<AssetBundle> _loadedBundles = new List<AssetBundle>();
        
        // P/Invoke for TTF support
        [DllImport("gdi32.dll", EntryPoint = "AddFontResourceExW", SetLastError = true)]
        public static extern int AddFontResourceEx([In][MarshalAs(UnmanagedType.LPWStr)] string lpFileName, uint fl, IntPtr pdv);
        [DllImport("gdi32.dll", EntryPoint = "RemoveFontResourceExW", SetLastError = true)]
        public static extern int RemoveFontResourceEx([In][MarshalAs(UnmanagedType.LPWStr)] string lpFileName, uint fl, IntPtr pdv);
        [DllImport("user32.dll")]
        public static extern int PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        
        private const uint FR_PRIVATE = 0x10;
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

            var files = Directory.GetFiles(fontsPath);
            foreach (var file in files)
            {
                if (file.EndsWith(".meta")) continue;

                // 1. Try Load as AssetBundle
                try
                {
                    var bundle = AssetBundle.LoadFromFile(file);
                    if (bundle != null)
                    {
                        var assets = bundle.LoadAllAssets<TMP_FontAsset>();
                        if (assets.Length > 0)
                        {
                            _loadedBundles.Add(bundle);
                            _loadedCustomFonts.AddRange(assets);
                            Log.LogInfo($"[FontMod] Loaded {assets.Length} fonts from bundle: {Path.GetFileName(file)}");
                            continue; // Success, move to next file
                        }
                        else
                        {
                            bundle.Unload(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[FontMod] Failed to load {Path.GetFileName(file)} as bundle: {ex.Message}");
                }

                // 2. Fallback: TTF/OTF Loading
                if (file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                {
                    // Register with OS
                    AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero);
                    Log.LogInfo($"[FontMod] Registered OS Font: {Path.GetFileName(file)}");
                }
            }
            
            // Notify system of font changes
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
            _activeOsFont = null;

            // 1. Check Custom Loaded Bundles
            _activeTmpFontAsset = _loadedCustomFonts.FirstOrDefault(f => f.name.Equals(fontName, StringComparison.OrdinalIgnoreCase));
            
            // 2. Check OS Fonts / Resources
            if (_activeTmpFontAsset == null)
            {
                // Try create from OS
                var osFont = UnityEngine.Font.CreateDynamicFontFromOSFont(fontName, 32);
                if (osFont != null)
                {
                    _activeOsFont = osFont;
                    // Create TMP asset
                    _activeTmpFontAsset = TMPro.TMP_FontAsset.CreateFontAsset(osFont);
                    if (_activeTmpFontAsset != null)
                    {
                        _activeTmpFontAsset.name = fontName + " (Dynamic)";
                    }
                }
            }

            if (_activeTmpFontAsset != null)
            {
                UpdateAllText();
            }
            else
            {
                Log.LogError($"[FontMod] Could not find or create font asset for: {fontName}");
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
                
                // Add custom loaded
                options.AddRange(_loadedCustomFonts.Select(f => f.name));
                
                // Add common OS fonts
                options.Add("Arial");
                options.Add("Consolas");
                options.Add("Times New Roman");
                options.Add("Segoe UI");
                // TODO: Add more OS fonts if needed

                dropdown.AddOptions(options);

                // Select current
                string current = _fontName.Value;
                int idx = options.FindIndex(s => s.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) dropdown.value = idx;

                dropdown.onValueChanged.RemoveAllListeners();
                dropdown.onValueChanged.AddListener((val) => {
                    _fontName.Value = options[val];
                    ApplyFont(options[val]);
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
