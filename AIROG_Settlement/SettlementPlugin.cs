using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using TMPro;
using System;
using Newtonsoft.Json;

namespace AIROG_Settlement
{
    [BepInPlugin("com.airog.settlement", "Settlement Mod", "1.0.0")]
    public class SettlementPlugin : BaseUnityPlugin
    {
        public static SettlementPlugin Instance;
        public static BepInEx.Logging.ManualLogSource Log;
        
        public Sprite SettlementButtonSprite;
        public Sprite SettlementUISprite;
        public Sprite SettlementBkgSprite;
        public Sprite EstablishSettlementSprite;
        public Sprite[] TabSprites = new Sprite[5];
        public Sprite GoldIcon, WoodIcon, StoneIcon, TownPanelSprite;

        public GameObject SettlementButtonObj;
        public GameObject SettlementModalObj;
        public bool IsSettlementOpen = false;

        public SettlementState CurrentSettlement = new SettlementState();
        public int SelectedTab = 0;

        public TextMeshProUGUI OverviewNameText;
        public TextMeshProUGUI GoldText, WoodText, StoneText;
        public RawImage SettlementImageDisplay;

        public GameObject CenterWorkspaceObj;
        public List<GameObject> TabContentObjects = new List<GameObject>();

        private bool _needsUiUpdate = false;

        private void Awake()
        {
            try
            {
                Instance = this;
                Log = Logger;
                Log.LogInfo("Settlement Mod Awake started");
                
                LoadAssets();
                
                var harmony = new Harmony("com.airog.settlement");
                harmony.PatchAll();
                
                Log.LogInfo("Settlement Mod loaded successfully!");
            }
            catch (Exception ex)
            {
                if (Log != null) Log.LogError($"Error in Settlement Mod Awake: {ex}");
                else UnityEngine.Debug.LogError($"CRITICAL: Error in Settlement Mod Awake (Log was null): {ex}");
            }
        }

        private void Update()
        {
            if (_needsUiUpdate)
            {
                _needsUiUpdate = false;
                UpdateOverviewUI();
            }
        }

        private void LoadAssets()
        {
            string assetsPath = Path.Combine(Application.streamingAssetsPath, "Settlement");
            Log.LogInfo($"Loading Settlement assets from: {assetsPath}");
            
            if (!Directory.Exists(assetsPath))
            {
                Log.LogWarning($"Settlement asset directory not found at {assetsPath}. Creating it.");
                Directory.CreateDirectory(assetsPath);
            }

            SettlementButtonSprite = LoadSprite(Path.Combine(assetsPath, "SettlementButton.png"));
            SettlementUISprite = LoadSprite(Path.Combine(assetsPath, "SettlementUI.png"));
            SettlementBkgSprite = LoadSprite(Path.Combine(assetsPath, "Settlement_bkg.png"));
            EstablishSettlementSprite = LoadSprite(Path.Combine(assetsPath, "EstablishSettlement.png"));
            
            TabSprites[0] = LoadSprite(Path.Combine(assetsPath, "OverviewButton.png"));
            TabSprites[1] = LoadSprite(Path.Combine(assetsPath, "BuildingsButton.png"));
            TabSprites[2] = LoadSprite(Path.Combine(assetsPath, "PopulationButton.png"));
            TabSprites[3] = LoadSprite(Path.Combine(assetsPath, "TradeButton.png"));
            TabSprites[4] = LoadSprite(Path.Combine(assetsPath, "ResearchButton.png"));

            GoldIcon = LoadSprite(Path.Combine(assetsPath, "GoldIcon.png"));
            WoodIcon = LoadSprite(Path.Combine(assetsPath, "WoodIcon.png"));
            StoneIcon = LoadSprite(Path.Combine(assetsPath, "StoneIcon.png"));
            TownPanelSprite = LoadSprite(Path.Combine(assetsPath, "TownPanel.png"));
        }

        private Sprite LoadSprite(string path)
        {
            if (File.Exists(path))
            {
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2);
                if (tex.LoadImage(data))
                {
                    return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }
            }
            Log.LogError($"Failed to load sprite at {path}");
            return null;
        }

        public void ToggleSettlementView()
        {
            IsSettlementOpen = !IsSettlementOpen;
            if (SettlementModalObj != null)
            {
                SettlementModalObj.SetActive(IsSettlementOpen);
                if (IsSettlementOpen)
                {
                    SettlementModalObj.transform.SetAsLastSibling();
                    UpdateOverviewUI();
                    SwitchTab(0);
                    Log.LogInfo("Opened Settlement UI.");
                }
                else
                {
                    Log.LogInfo("Closed Settlement UI.");
                }
            }
            else
            {
                Log.LogError("SettlementModalObj is NULL when trying to toggle!");
            }
            // Optional: Pause game or block mouse interactions if needed
            // if (IsSettlementOpen) Utils.DisablePlayerInteractions(...)
        }

        public void SwitchTab(int index)
        {
            SelectedTab = index;
            Log.LogInfo($"Switching to tab {index}");
            
            // Redraw content or toggle objects
            for (int i = 0; i < TabContentObjects.Count; i++)
            {
                TabContentObjects[i].SetActive(i == index);
            }
        }

        public bool IsSettlement(Place p)
        {
            return p != null && CurrentSettlement != null && CurrentSettlement.LocationUuid == p.uuid;
        }

        public void EstablishSettlement(Place p)
        {
            if (p == null) return;
            CurrentSettlement.LocationUuid = p.uuid;
            CurrentSettlement.Name = p.GetPrettyName();
            // Initialize resources
            CurrentSettlement.Resources["Gold"] = 100;
            CurrentSettlement.Resources["Wood"] = 0;
            CurrentSettlement.Resources["Stone"] = 0;
            Log.LogInfo($"Established settlement at {p.GetPrettyName()} ({p.uuid})");
            
            // Save immediately
            SaveSettlementData();

            // Automatically trigger image generation if we have a place
            TriggerImageGeneration();

            // Open the view immediately after establishing
            if (!IsSettlementOpen) ToggleSettlementView();
            else UpdateOverviewUI();
        }

        public void UpdateOverviewUI()
        {
            if (OverviewNameText != null)
            {
                OverviewNameText.text = string.IsNullOrEmpty(CurrentSettlement.Name) ? "No Active Settlement" : CurrentSettlement.Name;
            }
            
            if (GoldText != null) GoldText.text = (CurrentSettlement.Resources.ContainsKey("Gold") ? CurrentSettlement.Resources["Gold"] : 0).ToString();
            if (WoodText != null) WoodText.text = (CurrentSettlement.Resources.ContainsKey("Wood") ? CurrentSettlement.Resources["Wood"] : 0).ToString();
            if (StoneText != null) StoneText.text = (CurrentSettlement.Resources.ContainsKey("Stone") ? CurrentSettlement.Resources["Stone"] : 0).ToString();

            if (SettlementImageDisplay != null)
            {
                if (!string.IsNullOrEmpty(CurrentSettlement.ImageUuid))
                {
                    string path = Path.Combine(SS.I.saveSubDirAsArg, CurrentSettlement.ImageUuid + ".png");
                    if (File.Exists(path))
                    {
                        byte[] data = File.ReadAllBytes(path);
                        Texture2D tex = new Texture2D(2, 2);
                        if (tex.LoadImage(data))
                        {
                            SettlementImageDisplay.texture = tex;
                            SettlementImageDisplay.color = Color.white;
                        }
                    }
                    else
                    {
                        SettlementImageDisplay.texture = null;
                        SettlementImageDisplay.color = new Color(0, 0, 0, 0.5f);
                    }
                }
                else
                {
                    SettlementImageDisplay.texture = null;
                    SettlementImageDisplay.color = new Color(0, 0, 0, 0.5f);
                }
            }
        }

        public void TriggerImageGeneration()
        {
            if (string.IsNullOrEmpty(CurrentSettlement.LocationUuid)) return;
            
            Place p = null;
            if (SS.I.uuidToGameEntityMap.ContainsKey(CurrentSettlement.LocationUuid))
            {
                p = SS.I.uuidToGameEntityMap[CurrentSettlement.LocationUuid] as Place;
            }
            if (p == null) return;
            
            System.Threading.Tasks.Task.Run(async () => {
                try {
                    await GenerateSettlementImage(p);
                } catch (Exception e) {
                    Log.LogError($"Error generating image: {e.Message}");
                }
            });
        }

        public async System.Threading.Tasks.Task GenerateSettlementImage(Place p)
        {
            Log.LogInfo("Starting settlement image generation...");
            
            string prompt = $"A cozy and prospering settlement called {CurrentSettlement.Name}, located in {p.GetPrettyName()}. {p.GetPotentiallyNullDescription()}";
            string uuid = Guid.NewGuid().ToString();
            
            // We use a temporary GameEntity to satisfy the AIAsker's requirement
            SettlementImageEntity entity = new SettlementImageEntity(prompt, uuid, p.manager);
            
            var settings = SS.I.settingsPojo.GetEntImgSettings(SettingsPojo.EntImgType.PLACE);
            // Ensure we use a decent resolution for the overview
            settings.x = 512;
            settings.y = 512;

            await AIAsker.getGeneratedImage(settings, entity, true);
            
            CurrentSettlement.ImageUuid = uuid;
            SaveSettlementData();
            
            // Update UI on main thread via flag
            _needsUiUpdate = true;
        }

        public class SettlementImageEntity : GameEntity
        {
            public string CustomPrompt;
            public SettlementImageEntity(string prompt, string uuid, GameplayManager manager) : base("Settlement", manager)
            {
                this.uuid = uuid;
                this.CustomPrompt = prompt;
                // AIAsker needs these initialized
                this.imgGenInfo = new ImgGenInfo(ImgType.REGULAR);
                this.imgFileNames = new List<string>();
            }
            public override async System.Threading.Tasks.Task<string> GetGenerateImagePrompt() { return CustomPrompt; }
            public override SerializableGameEntity GetSerializable() { return new SerializableGameEntity(this); }
        }

        public string GetSavePath(string subdir) 
        {
            if (string.IsNullOrEmpty(subdir)) return null;
            return Path.Combine(SS.I.saveTopLvlDir, subdir, "settlement_data.json");
        }

        public void SaveSettlementData()
        {
            string subdir = SS.I.saveSubDirAsArg;
            if (string.IsNullOrEmpty(subdir)) return;

            try {
                string path = GetSavePath(subdir);
                string json = JsonConvert.SerializeObject(CurrentSettlement, Formatting.Indented);
                File.WriteAllText(path, json);
                Log.LogInfo($"Settlement data saved to {subdir}");
            } catch (Exception e) {
                Log.LogError($"Failed to save settlement data: {e.Message}");
            }
        }

        public string GetSettlementPromptContext(Place p)
        {
            if (p == null || !IsSettlement(p)) return "";

            string context = $"\n\n[Settlement Context: You are at the settlement of {CurrentSettlement.Name}. ";
            context += $"It is currently thriving with the following resources: ";
            foreach (var res in CurrentSettlement.Resources)
            {
                context += $"{res.Key}: {res.Value}, ";
            }
            context = context.TrimEnd(' ', ',') + ".]";
            return context;
        }

        public void LoadSettlementData(string subdir)
        {
            if (string.IsNullOrEmpty(subdir)) return;
            
            string path = GetSavePath(subdir);
            if (File.Exists(path)) {
                try {
                    string json = File.ReadAllText(path);
                    CurrentSettlement = JsonConvert.DeserializeObject<SettlementState>(json);
                    Log.LogInfo($"Settlement data loaded from {subdir}");
                } catch (Exception e) {
                    Log.LogError($"Failed to load settlement data: {e.Message}");
                    CurrentSettlement = new SettlementState(); // Initialize to default if loading fails
                }
            } else {
                CurrentSettlement = new SettlementState(); // Initialize to default if file doesn't exist
            }
        }

        public void CreateResourceRow(Transform parent, string name, float yAnchor, Sprite icon, out TextMeshProUGUI textComp)
        {
            GameObject row = new GameObject(name + "Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            RectTransform rr = row.GetComponent<RectTransform>();
            // Anchors relative to parent (now TownPanel)
            rr.anchorMin = new Vector2(0.15f, yAnchor);
            rr.anchorMax = new Vector2(0.85f, yAnchor + 0.08f);
            rr.offsetMin = rr.offsetMax = Vector2.zero;

            // Icon
            GameObject iconObj = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconObj.transform.SetParent(row.transform, false);
            var img = iconObj.GetComponent<Image>();
            img.sprite = icon;
            img.preserveAspect = true;
            RectTransform ir = iconObj.GetComponent<RectTransform>();
            ir.anchorMin = Vector2.zero;
            ir.anchorMax = new Vector2(0.2f, 1f);
            ir.offsetMin = ir.offsetMax = Vector2.zero;

            // Text
            GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtObj.transform.SetParent(row.transform, false);
            textComp = txtObj.GetComponent<TextMeshProUGUI>();
            textComp.fontSize = 22;
            textComp.alignment = TextAlignmentOptions.Left;
            textComp.color = Color.white;
            textComp.outlineWidth = 0.1f;
            textComp.outlineColor = Color.black;
            textComp.text = "0";

            RectTransform tr = txtObj.GetComponent<RectTransform>();
            tr.anchorMin = new Vector2(0.25f, 0f);
            tr.anchorMax = Vector2.one;
            tr.offsetMin = tr.offsetMax = Vector2.zero;
        }
    }

    [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
    public static class Patch_SaveIO_WriteSaveFile
    {
        public static void Postfix()
        {
            SettlementPlugin.Instance.SaveSettlementData();
        }
    }

    [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
    public static class Patch_SaveIO_ReadSaveFile
    {
        public static void Postfix(string saveSubDir)
        {
            SettlementPlugin.Instance.LoadSettlementData(saveSubDir);
        }
    }

    [HarmonyPatch(typeof(GameplayManager), "BuildPromptString", new Type[] { typeof(bool), typeof(bool), typeof(InteractionInfo), typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld), typeof(List<Faction>), typeof(List<string>) })]
    public static class Patch_GameplayManager_BuildPromptString
    {
        public static void Postfix(GameplayManager __instance, ref string __result, Place destinationPlace)
        {
            try {
                Place target = destinationPlace ?? __instance.currentPlace;
                if (target != null && SettlementPlugin.Instance != null && SettlementPlugin.Instance.IsSettlement(target))
                {
                    __result += SettlementPlugin.Instance.GetSettlementPromptContext(target);
                }
            } catch (Exception ex) { SettlementPlugin.Log.LogError("Error in BuildPromptString Postfix: " + ex); }
        }
    }

    [HarmonyPatch(typeof(MapLocation), "UpdateGraphicalInfo")]
    public static class Patch_MapLocation_UpdateGraphicalInfo
    {
        public static void Postfix(MapLocation __instance)
        {
            Place p = __instance.GetPlace();
            if (p != null && SettlementPlugin.Instance.IsSettlement(p))
            {
                // Prefix name if not already prefixed
                if (!__instance.entityTitle.text.Contains("[Settlement]"))
                {
                    __instance.entityTitle.text = "[Settlement] " + __instance.entityTitle.text;
                }

                // Distinct highlight color (Greenish)
                if (__instance.highlightedImg != null)
                {
                    // If it's the currently selected/active location, give it a bright green highlight
                    // Otherwise, a more subtle green
                    Color settlementColor = new Color(0.2f, 0.8f, 0.2f, 1.0f);
                    if (__instance.highlightedImg.color == MapLocation.HIGHLIGHTED_COLOR)
                    {
                        __instance.highlightedImg.color = settlementColor;
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
    public static class Patch_MainLayouts_InitCommonAnchs
    {
        public static void Postfix(MainLayouts __instance)
        {
            SettlementPlugin.Log.LogInfo("MainLayouts.InitCommonAnchs Postfix: Injecting Settlement UI");
            CreateSettlementUI(__instance);
        }

        private static void CreateSettlementUI(MainLayouts layout)
        {
            if (SettlementPlugin.Instance.SettlementButtonSprite == null)
            {
                SettlementPlugin.Log.LogError("Sprites not loaded, cannot create UI");
                return;
            }

            // 1. Create the Settlement Button in the bottom HUD bar
            Transform parent = layout.buttonsHolderHolder; 
            if (parent == null)
            {
                SettlementPlugin.Log.LogError("buttonsHolderHolder is NULL, cannot create HUD button");
                return;
            }

            // Check if already exists to avoid duplicates on re-init
            if (parent.Find("SettlementButton") != null) return;

            GameObject btnObj = new GameObject("SettlementButton", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            
            Image img = btnObj.GetComponent<Image>();
            img.sprite = SettlementPlugin.Instance.SettlementButtonSprite;
            img.preserveAspect = true;

            // Add LayoutElement to ensure correct size in HorizontalLayoutGroup
            LayoutElement le = btnObj.AddComponent<LayoutElement>();
            le.preferredWidth = 60;
            le.preferredHeight = 60;
            le.minWidth = 60;
            le.minHeight = 60;

            Button btn = btnObj.GetComponent<Button>();
            btn.onClick.AddListener(() => SettlementPlugin.Instance.ToggleSettlementView());
            
            // Ensure it's among the HUD buttons
            btnObj.transform.SetAsLastSibling();
            
            SettlementPlugin.Instance.SettlementButtonObj = btnObj;
            SettlementPlugin.Log.LogInfo("HUD Settlement Button created with LayoutElement.");

            // 2. Create the Modal Root
            if (layout.mainHolder.Find("SettlementModal") != null)
            {
                SettlementPlugin.Instance.SettlementModalObj = layout.mainHolder.Find("SettlementModal").gameObject;
                return;
            }

            GameObject modalObj = new GameObject("SettlementModal", typeof(RectTransform));
            modalObj.transform.SetParent(layout.mainHolder, false);
            SettlementUIHelper.SetRect(modalObj.GetComponent<RectTransform>(), 0, 0, SettlementUIHelper.CANVAS_WIDTH, SettlementUIHelper.CANVAS_HEIGHT);
            // Center the 1024x559 modally by using the helper or manually anchoring
            // Actually, the user wants it at 0.1, 0.1 to 0.9, 0.9 roughly, but the BG is 1024x559
            // Let's center it.
            RectTransform modalRect = modalObj.GetComponent<RectTransform>();
            modalRect.anchorMin = new Vector2(0.5f, 0.5f);
            modalRect.anchorMax = new Vector2(0.5f, 0.5f);
            modalRect.sizeDelta = new Vector2(SettlementUIHelper.CANVAS_WIDTH, SettlementUIHelper.CANVAS_HEIGHT);

            // 2a. Background Layer
            SettlementUIHelper.CreateUIElement("Background", modalObj.transform, 0, 0, 1024, 559, SettlementPlugin.Instance.SettlementBkgSprite);

            // 2b. Content Layer (Frame)
            SettlementUIHelper.CreateUIElement("Frame", modalObj.transform, 0, 0, 1024, 559, SettlementPlugin.Instance.SettlementUISprite);

            // 3. Populate Tabs
            string[] tabNames = { "Overview", "Buildings", "Population", "Trade", "Research" };
            for (int i = 0; i < 5; i++)
            {
                var tabRect = SettlementUIHelper.Slots.TopTab(i);
                int tabIndex = i;
                
                // Create button with the specific sprite
                GameObject tabBtnObj = SettlementUIHelper.CreateUIElement($"Tab_{i}", modalObj.transform, tabRect.x, tabRect.y, tabRect.width, tabRect.height, SettlementPlugin.Instance.TabSprites[i], Color.white);
                tabBtnObj.AddComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.SwitchTab(tabIndex));
                
                // Ensure image settings are correct for the icons
                Image tabImg = tabBtnObj.GetComponent<Image>();
                if (tabImg != null)
                {
                tabImg.type = Image.Type.Simple;
                    tabImg.preserveAspect = true;
                }
            }

            // 4. Center Content Parent (This is now just a visual guide, not a parent for tab contents)
            var centerRect = SettlementUIHelper.Slots.CenterWorkspace;
            SettlementPlugin.Instance.CenterWorkspaceObj = SettlementUIHelper.CreateUIElement("CenterWorkspace", modalObj.transform, centerRect.x, centerRect.y, centerRect.width, centerRect.height, null, new Color(0, 0, 0, 0f));

            // Create Tab Contents
            SettlementPlugin.Instance.TabContentObjects.Clear();
            for (int i = 0; i < 5; i++)
            {
                // Tab Content spans the whole area (1024x559) to allow sidebar/workspace usage
                GameObject tabContent = new GameObject($"TabContent_{i}", typeof(RectTransform));
                tabContent.transform.SetParent(modalObj.transform, false);
                SettlementUIHelper.SetRect(tabContent.GetComponent<RectTransform>(), 0, 0, 1024, 559);
                tabContent.SetActive(i == SettlementPlugin.Instance.SelectedTab);
                SettlementPlugin.Instance.TabContentObjects.Add(tabContent);
                
                if (i == 0) // Overview
                {
                    // Town Panel Background - Fits within the left sidebar area
                    GameObject tpObj = SettlementUIHelper.CreateUIElement("TownPanel", tabContent.transform, 12, 106, 164, 400, SettlementPlugin.Instance.TownPanelSprite);
                    
                    // Settlement Name (Child of TownPanel)
                    GameObject nameObj = new GameObject("SettlementName", typeof(RectTransform), typeof(TextMeshProUGUI));
                    nameObj.transform.SetParent(tpObj.transform, false);
                    SettlementPlugin.Instance.OverviewNameText = nameObj.GetComponent<TextMeshProUGUI>();
                    SettlementPlugin.Instance.OverviewNameText.fontSize = 20;
                    SettlementPlugin.Instance.OverviewNameText.alignment = TextAlignmentOptions.Center;
                    SettlementPlugin.Instance.OverviewNameText.color = new Color(0.95f, 0.9f, 0.7f);
                    SettlementPlugin.Instance.OverviewNameText.text = "Settlement Name";
                    SettlementPlugin.Instance.OverviewNameText.outlineWidth = 0.15f;
                    SettlementPlugin.Instance.OverviewNameText.outlineColor = Color.black;
                    
                    RectTransform nr = nameObj.GetComponent<RectTransform>();
                    nr.anchorMin = new Vector2(0.1f, 0.65f); // Lowered significantly to avoid "Town" header
                    nr.anchorMax = new Vector2(0.9f, 0.78f);
                    nr.offsetMin = nr.offsetMax = Vector2.zero;

                    // Header for Resources (Child of TownPanel)
                    GameObject headerObj = new GameObject("ResourceHeader", typeof(RectTransform), typeof(TextMeshProUGUI));
                    headerObj.transform.SetParent(tpObj.transform, false);
                    var hTxt = headerObj.GetComponent<TextMeshProUGUI>();
                    hTxt.text = "Resources";
                    hTxt.fontSize = 18;
                    hTxt.alignment = TextAlignmentOptions.Center;
                    hTxt.color = new Color(0.8f, 0.7f, 0.5f);
                    hTxt.fontStyle = FontStyles.Bold;
                    
                    RectTransform hr = headerObj.GetComponent<RectTransform>();
                    hr.anchorMin = new Vector2(0.1f, 0.55f);
                    hr.anchorMax = new Vector2(0.9f, 0.65f);
                    hr.offsetMin = hr.offsetMax = Vector2.zero;

                    // Resources Rows (Children of TownPanel)
                    SettlementPlugin.Instance.CreateResourceRow(tpObj.transform, "Gold", 0.45f, SettlementPlugin.Instance.GoldIcon, out SettlementPlugin.Instance.GoldText);
                    SettlementPlugin.Instance.CreateResourceRow(tpObj.transform, "Wood", 0.35f, SettlementPlugin.Instance.WoodIcon, out SettlementPlugin.Instance.WoodText);
                    SettlementPlugin.Instance.CreateResourceRow(tpObj.transform, "Stone", 0.25f, SettlementPlugin.Instance.StoneIcon, out SettlementPlugin.Instance.StoneText);

                    // Settlement Image (Centered with padding: 204, 134, 616, 396)
                    GameObject imgObj = new GameObject("SettlementImage", typeof(RectTransform), typeof(RawImage));
                    imgObj.transform.SetParent(tabContent.transform, false);
                    SettlementUIHelper.SetRect(imgObj.GetComponent<RectTransform>(), 204, 134, 616, 396);
                    
                    SettlementPlugin.Instance.SettlementImageDisplay = imgObj.GetComponent<RawImage>();
                    SettlementPlugin.Instance.SettlementImageDisplay.color = Color.white; // Default to white so it shows the texture
                    
                    // Regenerate Button (Small overlay at bottom-right of image)
                    // Let's place it a bit inside the margin
                    GameObject regBtnObj = SettlementUIHelper.CreateUIElement("RegenerateButton", tabContent.transform, 640, 475, 170, 45, null, new Color(0.1f, 0.1f, 0.1f, 0.7f));
                    regBtnObj.AddComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.TriggerImageGeneration());
                    
                    GameObject regTxtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                    regTxtObj.transform.SetParent(regBtnObj.transform, false);
                    var rTxt = regTxtObj.GetComponent<TextMeshProUGUI>();
                    rTxt.text = "Regenerate Image";
                    rTxt.fontSize = 14;
                    rTxt.alignment = TextAlignmentOptions.Center;
                    rTxt.color = Color.white;
                    RectTransform rtr = regTxtObj.GetComponent<RectTransform>();
                    rtr.anchorMin = Vector2.zero; rtr.anchorMax = Vector2.one;
                    rtr.offsetMin = rtr.offsetMax = Vector2.zero;
                }
            }

            // 5. Sidebars (Subtle slots)
            for (int i = 0; i < 10; i++) {
                var r = SettlementUIHelper.Slots.LeftSidebarItem(i);
                SettlementUIHelper.CreateUIElement($"SB_L_{i}", modalObj.transform, r.x, r.y, r.width, r.height, null, new Color(1,1,1,0.02f));
            }
            for (int i = 0; i < 5; i++) {
                var r = SettlementUIHelper.Slots.RightSidebarItem(i);
                SettlementUIHelper.CreateUIElement($"SB_R_{i}", modalObj.transform, r.x, r.y, r.width, r.height, null, new Color(1,1,1,0.02f));
            }

            modalObj.SetActive(false);
            SettlementPlugin.Instance.SettlementModalObj = modalObj;

            // 7. Close button (Better placement & icon)
            GameObject closeBtnObj = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
            closeBtnObj.transform.SetParent(modalObj.transform, false);
            closeBtnObj.GetComponent<Image>().color = new Color(0, 0, 0, 0); 
            closeBtnObj.GetComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.ToggleSettlementView());
            SettlementUIHelper.SetRect(closeBtnObj.GetComponent<RectTransform>(), 967, 10, 50, 50);

            GameObject xObj = new GameObject("X", typeof(RectTransform), typeof(TextMeshProUGUI));
            xObj.transform.SetParent(closeBtnObj.transform, false);
            var xTxt = xObj.GetComponent<TextMeshProUGUI>();
            xTxt.text = "X";
            xTxt.fontSize = 32;
            xTxt.alignment = TextAlignmentOptions.Center;
            xTxt.color = new Color(0.9f, 0.2f, 0.2f);
            xTxt.outlineWidth = 0.2f;
            xTxt.outlineColor = Color.black;
            
            RectTransform xr = xObj.GetComponent<RectTransform>();
            xr.anchorMin = Vector2.zero; xr.anchorMax = Vector2.one; xr.offsetMin = xr.offsetMax = Vector2.zero;

            // Final check: Update UI contents
            SettlementPlugin.Instance.UpdateOverviewUI();
        }
    }

    [HarmonyPatch(typeof(MapLocationDetails), "UpdateGraphics")]
    public static class Patch_MapLocationDetails_UpdateGraphics
    {
        public static void Postfix(MapLocationDetails __instance)
        {
            if (__instance == null || __instance.place == null || __instance.enterButtonTrans == null) return;
            if (SettlementPlugin.Instance == null) return;

            // Add a "Found Settlement" button if not already one
            // Use parent.Find for better reliability than global GameObject.Find
            Transform parent = __instance.enterButtonTrans.parent;
            Transform existingBtnTrans = parent.Find("EstablishSettlementButton");
            GameObject foundBtn = existingBtnTrans != null ? existingBtnTrans.gameObject : null;

            if (foundBtn == null)
            {
                // Create a clean button instead of cloning to avoid inheriting redundant game scripts/colors
                foundBtn = new GameObject("EstablishSettlementButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                foundBtn.transform.SetParent(parent, false);
                
                RectTransform rect = foundBtn.GetComponent<RectTransform>();
                // Position to the LEFT of the enter button
                RectTransform enterRect = __instance.enterButtonTrans.GetComponent<RectTransform>();
                if (enterRect != null)
                {
                    rect.sizeDelta = enterRect.sizeDelta;
                    rect.anchorMin = enterRect.anchorMin;
                    rect.anchorMax = enterRect.anchorMax;
                    rect.anchoredPosition = enterRect.anchoredPosition - new Vector2(enterRect.sizeDelta.x + 10f, 0);
                }

                Image img = foundBtn.GetComponent<Image>();
                if (SettlementPlugin.Instance.EstablishSettlementSprite != null)
                {
                    img.sprite = SettlementPlugin.Instance.EstablishSettlementSprite;
                    img.color = Color.white;
                }
                else
                {
                    img.color = new Color(0.5f, 0.2f, 0.2f, 0.8f); // Fallback color
                    GameObject label = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                    label.transform.SetParent(foundBtn.transform, false);
                    var t = label.GetComponent<TextMeshProUGUI>();
                    t.text = "Establish Settlement (Missing Sprite)";
                    t.fontSize = 12;
                    t.alignment = TextAlignmentOptions.Center;
                    SettlementPlugin.Log.LogWarning("EstablishSettlementSprite is NULL! Check if the PNG is in StreamingAssets/Settlement/");
                }

                Button b = foundBtn.GetComponent<Button>();
                b.onClick.AddListener(() => {
                    SettlementPlugin.Instance.EstablishSettlement(__instance.place);
                    __instance.UpdateGraphics();
                });
            }

            if (foundBtn != null)
            {
                bool isSettlement = SettlementPlugin.Instance.IsSettlement(__instance.place);
                foundBtn.SetActive(!isSettlement && __instance.enterButtonTrans.gameObject.activeSelf);
            }
        }
    }
}
