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

        // Persistent UI refs (live on modal, always visible regardless of active tab)
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

        public void ScheduleUiUpdate() { _needsUiUpdate = true; }

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
        }

        public void SwitchTab(int index)
        {
            SelectedTab = index;
            Log.LogInfo($"Switching to tab {index}");

            for (int i = 0; i < TabContentObjects.Count; i++)
                TabContentObjects[i].SetActive(i == index);

            if (index == 1) RefreshBuildingsTab();
            if (index == 3) RefreshTradeTab();
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
            CurrentSettlement.Resources["Gold"] = 100;
            CurrentSettlement.Resources["Wood"] = 0;
            CurrentSettlement.Resources["Stone"] = 0;
            Log.LogInfo($"Established settlement at {p.GetPrettyName()} ({p.uuid})");

            SaveSettlementData();
            TriggerImageGeneration();

            if (!IsSettlementOpen) ToggleSettlementView();
            else UpdateOverviewUI();
        }

        public void UpdateOverviewUI()
        {
            // Settlement name (persistent, always visible)
            if (OverviewNameText != null)
            {
                OverviewNameText.text = string.IsNullOrEmpty(CurrentSettlement.Name)
                    ? "No Active Settlement"
                    : CurrentSettlement.Name;
            }

            // Resources in right sidebar (persistent, always visible)
            int gold  = CurrentSettlement.Resources.TryGetValue("Gold",  out int g) ? g : 0;
            int wood  = CurrentSettlement.Resources.TryGetValue("Wood",  out int w) ? w : 0;
            int stone = CurrentSettlement.Resources.TryGetValue("Stone", out int s) ? s : 0;
            if (GoldText  != null) GoldText.text  = $"Gold: {gold}";
            if (WoodText  != null) WoodText.text  = $"Wood: {wood}";
            if (StoneText != null) StoneText.text = $"Stone: {stone}";

            // Settlement image (Overview tab only)
            if (SettlementImageDisplay != null)
            {
                if (!string.IsNullOrEmpty(CurrentSettlement.ImageUuid) && SS.I != null)
                {
                    string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg,
                                               CurrentSettlement.ImageUuid + ".png");
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
                        SettlementImageDisplay.color = new Color(0, 0, 0, 0.4f);
                    }
                }
                else
                {
                    SettlementImageDisplay.texture = null;
                    SettlementImageDisplay.color = new Color(0, 0, 0, 0.4f);
                }
            }
        }

        public void TriggerImageGeneration()
        {
            if (string.IsNullOrEmpty(CurrentSettlement.LocationUuid)) return;

            Place p = null;
            if (SS.I.uuidToGameEntityMap.ContainsKey(CurrentSettlement.LocationUuid))
                p = SS.I.uuidToGameEntityMap[CurrentSettlement.LocationUuid] as Place;
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

            SettlementImageEntity entity = new SettlementImageEntity(prompt, uuid, p.manager);

            // FIX: GetEntImgSettings returns a reference to the shared settings object.
            // Do NOT mutate .x/.y — it would permanently corrupt game image settings.
            var settings = SS.I.settingsPojo.GetEntImgSettings(SettingsPojo.EntImgType.PLACE);
            await AIAsker.getGeneratedImage(settings, entity, true);

            CurrentSettlement.ImageUuid = uuid;
            SaveSettlementData();
            _needsUiUpdate = true;
        }

        public class SettlementImageEntity : GameEntity
        {
            public string CustomPrompt;
            public SettlementImageEntity(string prompt, string uuid, GameplayManager manager) : base("Settlement", manager)
            {
                this.uuid = uuid;
                this.CustomPrompt = prompt;
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
            string subdir = SS.I?.saveSubDirAsArg;
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
            context += "Resources: ";
            foreach (var res in CurrentSettlement.Resources)
                context += $"{res.Key}: {res.Value}, ";
            context = context.TrimEnd(' ', ',') + ".";

            if (CurrentSettlement.Buildings.Count > 0)
            {
                context += " Constructed buildings: ";
                foreach (var b in CurrentSettlement.Buildings)
                    context += $"{b.Name}, ";
                context = context.TrimEnd(' ', ',') + ".";
            }

            context += "]";
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
                    CurrentSettlement = new SettlementState();
                }
            } else {
                CurrentSettlement = new SettlementState();
            }
        }

        public void BuildBuilding(string buildingId)
        {
            var def = BuildingCatalog.Get(buildingId);
            if (def == null) { Log.LogError($"Unknown building ID: {buildingId}"); return; }
            if (CurrentSettlement.HasBuilding(buildingId)) { Log.LogWarning($"{buildingId} already built."); return; }
            if (!def.CanAfford(CurrentSettlement)) { Log.LogWarning($"Cannot afford {buildingId}."); return; }

            foreach (var kv in def.Cost)
                CurrentSettlement.Resources[kv.Key] -= kv.Value;

            CurrentSettlement.Buildings.Add(new BuildingInstance
            {
                BuildingID = def.ID,
                Name = def.Name,
                Level = 1,
                IsComplete = true,
                ConstructionProgress = 100f
            });

            Log.LogInfo($"Built {def.Name} at {CurrentSettlement.Name}.");
            SaveSettlementData();
        }

        // -----------------------------------------------------------------------
        // Center frame interior pixel bounds (measured from SettlementUI.png):
        //   Frame outer: x=305, y=124, w=405, h=323
        //   Frame inner: x=315, y=133, w=387, h=306
        // Right sidebar slots (RightSidebarItem helper): x=826, y=106+(i*45), w=164, h=42
        public void RefreshTradeTab()
        {
            if (TabContentObjects.Count < 4 || TabContentObjects[3] == null) return;
            Transform content = TabContentObjects[3].transform;

            for (int i = content.childCount - 1; i >= 0; i--)
                Destroy(content.GetChild(i).gameObject);

            SettlementUIHelper.CreateUIElement("TradeBg", content, 305, 124, 405, 323, null,
                new Color(0.06f, 0.06f, 0.10f, 0.88f));

            GameObject headerObj = new GameObject("Header", typeof(RectTransform), typeof(TextMeshProUGUI));
            headerObj.transform.SetParent(content, false);
            var hTxt = headerObj.GetComponent<TextMeshProUGUI>();
            hTxt.text = "Trade & Logistics";
            hTxt.fontSize = 18;
            hTxt.fontStyle = FontStyles.Bold;
            hTxt.alignment = TextAlignmentOptions.Center;
            hTxt.color = new Color(0.95f, 0.85f, 0.5f);
            hTxt.outlineWidth = 0.15f;
            hTxt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(headerObj.GetComponent<RectTransform>(), 305, 127, 405, 22);

            var actions = new List<(string name, string desc, Func<bool> canAfford, Action execute)>
            {
                ("Deposit Gold", "Give 50 personal gold", new Func<bool>(() => SS.I.hackyManager.playerCharacter.pcGameEntity.numGold >= 50), new Action(() => {
                    SS.I.hackyManager.playerCharacter.IncrGold(-50);
                    CurrentSettlement.AddResource("Gold", 50);
                })),
                ("Withdraw Gold", "Take 50 gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Gold", out int g) && g >= 50), new Action(() => {
                    CurrentSettlement.Resources["Gold"] -= 50;
                    SS.I.hackyManager.playerCharacter.IncrGold(50);
                })),
                ("Import Wood", "Buy 10 Wood for 20 Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Gold", out int g) && g >= 20), new Action(() => {
                    CurrentSettlement.Resources["Gold"] -= 20;
                    CurrentSettlement.AddResource("Wood", 10);
                })),
                ("Export Wood", "Sell 10 Wood for 10 Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Wood", out int w) && w >= 10), new Action(() => {
                    CurrentSettlement.Resources["Wood"] -= 10;
                    CurrentSettlement.AddResource("Gold", 10);
                })),
                ("Import Stone", "Buy 10 Stone for 30 Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Gold", out int g) && g >= 30), new Action(() => {
                    CurrentSettlement.Resources["Gold"] -= 30;
                    CurrentSettlement.AddResource("Stone", 10);
                })),
                ("Export Stone", "Sell 10 Stone for 15 Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Stone", out int s) && s >= 10), new Action(() => {
                    CurrentSettlement.Resources["Stone"] -= 10;
                    CurrentSettlement.AddResource("Gold", 15);
                }))
            };

            for (int i = 0; i < actions.Count; i++)
            {
                var act = actions[i];
                float rowY = 152f + i * 48f;
                bool canAfford = act.canAfford();

                Color bgColor = new Color(0.08f, 0.08f, 0.14f, 0.75f);
                SettlementUIHelper.CreateUIElement($"Row_{i}", content, 309, rowY, 397, 44, null, bgColor);

                GameObject nameObj = new GameObject($"Name_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                nameObj.transform.SetParent(content, false);
                var nameTxt = nameObj.GetComponent<TextMeshProUGUI>();
                nameTxt.text = act.name;
                nameTxt.fontSize = 14;
                nameTxt.fontStyle = FontStyles.Bold;
                nameTxt.alignment = TextAlignmentOptions.Left;
                nameTxt.color = Color.white;
                SettlementUIHelper.SetRect(nameObj.GetComponent<RectTransform>(), 314, rowY + 4, 188, 20);

                GameObject descObj = new GameObject($"Desc_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                descObj.transform.SetParent(content, false);
                var descTxt = descObj.GetComponent<TextMeshProUGUI>();
                descTxt.text = act.desc;
                descTxt.fontSize = 11;
                descTxt.alignment = TextAlignmentOptions.Left;
                descTxt.color = new Color(0.72f, 0.72f, 0.72f);
                SettlementUIHelper.SetRect(descObj.GetComponent<RectTransform>(), 314, rowY + 26, 188, 16);

                Color btnColor = canAfford
                    ? new Color(0.12f, 0.38f, 0.12f, 0.95f)
                    : new Color(0.22f, 0.22f, 0.22f, 0.80f);
                GameObject btnObj = SettlementUIHelper.CreateUIElement($"Btn_{i}", content,
                    617, rowY + 8, 84, 28, null, btnColor);
                var btn = btnObj.AddComponent<Button>();
                btn.interactable = canAfford;

                GameObject btnTxt = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
                btnTxt.transform.SetParent(btnObj.transform, false);
                var bTxt = btnTxt.GetComponent<TextMeshProUGUI>();
                bTxt.text = act.name.Split(' ')[0]; // Deposit, Withdraw, Import...
                bTxt.fontSize = 11;
                bTxt.alignment = TextAlignmentOptions.Center;
                bTxt.color = canAfford ? Color.white : new Color(0.55f, 0.55f, 0.55f);
                var btr = btnTxt.GetComponent<RectTransform>();
                btr.anchorMin = Vector2.zero; btr.anchorMax = Vector2.one;
                btr.offsetMin = btr.offsetMax = Vector2.zero;

                btn.onClick.AddListener(() =>
                {
                    act.execute();
                    SaveSettlementData();
                    RefreshTradeTab();
                    UpdateOverviewUI();
                });
            }
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Clears and rebuilds the Buildings tab. The building rows are contained entirely
        /// within the center frame interior so the meadow background is masked.
        /// </summary>
        public void RefreshBuildingsTab()
        {
            if (TabContentObjects.Count < 2 || TabContentObjects[1] == null) return;
            Transform content = TabContentObjects[1].transform;

            for (int i = content.childCount - 1; i >= 0; i--)
                Destroy(content.GetChild(i).gameObject);

            // Solid dark background covering the full center frame area (masks the transparent interior)
            SettlementUIHelper.CreateUIElement("BldgBg", content, 305, 124, 405, 323, null,
                new Color(0.06f, 0.06f, 0.10f, 0.88f));

            // Header
            GameObject headerObj = new GameObject("Header", typeof(RectTransform), typeof(TextMeshProUGUI));
            headerObj.transform.SetParent(content, false);
            var hTxt = headerObj.GetComponent<TextMeshProUGUI>();
            hTxt.text = "Buildings";
            hTxt.fontSize = 18;
            hTxt.fontStyle = FontStyles.Bold;
            hTxt.alignment = TextAlignmentOptions.Center;
            hTxt.color = new Color(0.95f, 0.85f, 0.5f);
            hTxt.outlineWidth = 0.15f;
            hTxt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(headerObj.GetComponent<RectTransform>(), 305, 127, 405, 22);

            // Building rows — 6 rows × 48px = 288px; start at y=152, end y=440 (frame ends ~y=447)
            var catalog = BuildingCatalog.All;
            for (int i = 0; i < catalog.Length; i++)
            {
                var def = catalog[i];
                float rowY = 152f + i * 48f;
                bool isBuilt = CurrentSettlement.HasBuilding(def.ID);
                bool canAfford = !isBuilt && def.CanAfford(CurrentSettlement);

                // Row background
                Color bgColor = isBuilt
                    ? new Color(0.08f, 0.25f, 0.08f, 0.75f)
                    : new Color(0.08f, 0.08f, 0.14f, 0.75f);
                SettlementUIHelper.CreateUIElement($"Row_{i}", content, 309, rowY, 397, 44, null, bgColor);

                // Building name
                GameObject nameObj = new GameObject($"Name_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                nameObj.transform.SetParent(content, false);
                var nameTxt = nameObj.GetComponent<TextMeshProUGUI>();
                nameTxt.text = def.Name;
                nameTxt.fontSize = 14;
                nameTxt.fontStyle = FontStyles.Bold;
                nameTxt.alignment = TextAlignmentOptions.Left;
                nameTxt.color = isBuilt ? new Color(0.65f, 1f, 0.65f) : Color.white;
                SettlementUIHelper.SetRect(nameObj.GetComponent<RectTransform>(), 314, rowY + 4, 188, 20);

                // Description
                GameObject descObj = new GameObject($"Desc_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                descObj.transform.SetParent(content, false);
                var descTxt = descObj.GetComponent<TextMeshProUGUI>();
                descTxt.text = def.Description;
                descTxt.fontSize = 11;
                descTxt.alignment = TextAlignmentOptions.Left;
                descTxt.color = new Color(0.72f, 0.72f, 0.72f);
                SettlementUIHelper.SetRect(descObj.GetComponent<RectTransform>(), 314, rowY + 26, 188, 16);

                // Cost / status
                GameObject costObj = new GameObject($"Cost_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                costObj.transform.SetParent(content, false);
                var costTxt = costObj.GetComponent<TextMeshProUGUI>();
                if (isBuilt)
                {
                    costTxt.text = "Constructed";
                    costTxt.color = new Color(0.45f, 0.9f, 0.45f);
                }
                else
                {
                    var parts = new System.Text.StringBuilder();
                    foreach (var kv in def.Cost)
                        parts.Append($"{kv.Key}: {kv.Value}  ");
                    costTxt.text = parts.ToString().TrimEnd();
                    costTxt.color = canAfford ? new Color(0.95f, 0.85f, 0.45f) : new Color(0.9f, 0.35f, 0.35f);
                }
                costTxt.fontSize = 12;
                costTxt.alignment = TextAlignmentOptions.Center;
                SettlementUIHelper.SetRect(costObj.GetComponent<RectTransform>(), 507, rowY + 4, 104, 36);

                // Build button
                if (!isBuilt)
                {
                    Color btnColor = canAfford
                        ? new Color(0.12f, 0.38f, 0.12f, 0.95f)
                        : new Color(0.22f, 0.22f, 0.22f, 0.80f);
                    GameObject btnObj = SettlementUIHelper.CreateUIElement($"Btn_{i}", content,
                        617, rowY + 8, 84, 28, null, btnColor);
                    var btn = btnObj.AddComponent<Button>();
                    btn.interactable = canAfford;

                    GameObject btnTxt = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
                    btnTxt.transform.SetParent(btnObj.transform, false);
                    var bTxt = btnTxt.GetComponent<TextMeshProUGUI>();
                    bTxt.text = canAfford ? "Build" : "Can't Afford";
                    bTxt.fontSize = 11;
                    bTxt.alignment = TextAlignmentOptions.Center;
                    bTxt.color = canAfford ? Color.white : new Color(0.55f, 0.55f, 0.55f);
                    var btr = btnTxt.GetComponent<RectTransform>();
                    btr.anchorMin = Vector2.zero; btr.anchorMax = Vector2.one;
                    btr.offsetMin = btr.offsetMax = Vector2.zero;

                    string defId = def.ID;
                    btn.onClick.AddListener(() =>
                    {
                        BuildBuilding(defId);
                        RefreshBuildingsTab();
                        UpdateOverviewUI();
                    });
                }
            }
        }
    }

    [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
    public static class Patch_SaveIO_WriteSaveFile
    {
        public static void Postfix()
        {
            if (SettlementPlugin.Instance == null) return;
            SettlementPlugin.Instance.CurrentSettlement.ProduceResources();
            SettlementPlugin.Instance.SaveSettlementData();
            SettlementPlugin.Instance.ScheduleUiUpdate();
        }
    }

    [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
    public static class Patch_SaveIO_ReadSaveFile
    {
        public static void Postfix(string saveSubDir)
        {
            if (SettlementPlugin.Instance == null) return;
            SettlementPlugin.Instance.LoadSettlementData(saveSubDir);
        }
    }


    [HarmonyPatch(typeof(MapLocation), "UpdateGraphicalInfo")]
    public static class Patch_MapLocation_UpdateGraphicalInfo
    {
        public static void Postfix(MapLocation __instance)
        {
            Place p = __instance.GetPlace();
            if (p != null && SettlementPlugin.Instance != null && SettlementPlugin.Instance.IsSettlement(p))
            {
                if (!__instance.entityTitle.text.Contains("[Settlement]"))
                    __instance.entityTitle.text = "[Settlement] " + __instance.entityTitle.text;

                if (__instance.highlightedImg != null &&
                    __instance.highlightedImg.color == MapLocation.HIGHLIGHTED_COLOR)
                {
                    __instance.highlightedImg.color = new Color(0.2f, 0.8f, 0.2f, 1.0f);
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

            // ---- HUD Button ----
            Transform parent = layout.buttonsHolderHolder;
            if (parent == null)
            {
                SettlementPlugin.Log.LogError("buttonsHolderHolder is NULL");
                return;
            }
            if (parent.Find("SettlementButton") != null) return;

            GameObject btnObj = new GameObject("SettlementButton", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            btnObj.GetComponent<Image>().sprite = SettlementPlugin.Instance.SettlementButtonSprite;
            btnObj.GetComponent<Image>().preserveAspect = true;
            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 60;
            le.preferredHeight = le.minHeight = 60;
            btnObj.GetComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.ToggleSettlementView());
            btnObj.transform.SetAsLastSibling();
            SettlementPlugin.Instance.SettlementButtonObj = btnObj;

            // ---- Modal Root ----
            if (layout.mainHolder.Find("SettlementModal") != null)
            {
                SettlementPlugin.Instance.SettlementModalObj = layout.mainHolder.Find("SettlementModal").gameObject;
                return;
            }

            GameObject modalObj = new GameObject("SettlementModal", typeof(RectTransform));
            modalObj.transform.SetParent(layout.mainHolder, false);
            var modalRect = modalObj.GetComponent<RectTransform>();
            modalRect.anchorMin = modalRect.anchorMax = new Vector2(0.5f, 0.5f);
            modalRect.sizeDelta = new Vector2(SettlementUIHelper.CANVAS_WIDTH, SettlementUIHelper.CANVAS_HEIGHT);

            // Layer 1: opaque meadow background
            SettlementUIHelper.CreateUIElement("Background", modalObj.transform,
                0, 0, 1024, 559, SettlementPlugin.Instance.SettlementBkgSprite);

            // Layer 2: UI chrome (tabs, center frame border, right drawer panel)
            SettlementUIHelper.CreateUIElement("Frame", modalObj.transform,
                0, 0, 1024, 559, SettlementPlugin.Instance.SettlementUISprite);

            // ---- Tab buttons ----
            for (int i = 0; i < 5; i++)
            {
                var tr = SettlementUIHelper.Slots.TopTab(i);
                int idx = i;
                GameObject tabBtn = SettlementUIHelper.CreateUIElement($"Tab_{i}", modalObj.transform,
                    tr.x, tr.y, tr.width, tr.height, SettlementPlugin.Instance.TabSprites[i], Color.white);
                var tabImg = tabBtn.GetComponent<Image>();
                tabImg.type = Image.Type.Simple;
                tabImg.preserveAspect = true;
                tabBtn.AddComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.SwitchTab(idx));
            }

            // ---- Sidebar slot overlays (nearly transparent) ----
            for (int i = 0; i < 10; i++)
            {
                var r = SettlementUIHelper.Slots.LeftSidebarItem(i);
                SettlementUIHelper.CreateUIElement($"SB_L_{i}", modalObj.transform,
                    r.x, r.y, r.width, r.height, null, new Color(1, 1, 1, 0.02f));
            }
            for (int i = 0; i < 5; i++)
            {
                var r = SettlementUIHelper.Slots.RightSidebarItem(i);
                SettlementUIHelper.CreateUIElement($"SB_R_{i}", modalObj.transform,
                    r.x, r.y, r.width, r.height, null, new Color(1, 1, 1, 0.02f));
            }

            // ---- Persistent: settlement name above the center frame ----
            // Center frame starts at y=124; name sits in the 27px gap below the tabs (tabs end ~y=103)
            GameObject nameObj = new GameObject("SettlementName", typeof(RectTransform), typeof(TextMeshProUGUI));
            nameObj.transform.SetParent(modalObj.transform, false);
            SettlementPlugin.Instance.OverviewNameText = nameObj.GetComponent<TextMeshProUGUI>();
            SettlementPlugin.Instance.OverviewNameText.text = "Settlement";
            SettlementPlugin.Instance.OverviewNameText.fontSize = 18;
            SettlementPlugin.Instance.OverviewNameText.fontStyle = FontStyles.Bold;
            SettlementPlugin.Instance.OverviewNameText.alignment = TextAlignmentOptions.Center;
            SettlementPlugin.Instance.OverviewNameText.color = new Color(0.98f, 0.92f, 0.72f);
            SettlementPlugin.Instance.OverviewNameText.outlineWidth = 0.2f;
            SettlementPlugin.Instance.OverviewNameText.outlineColor = Color.black;
            // x=316, y=97, w=390, h=25 — sits in the gap between tabs and center frame
            SettlementUIHelper.SetRect(nameObj.GetComponent<RectTransform>(), 316, 97, 390, 25);

            // ---- Persistent: resources in right sidebar slots 0-2 ----
            // Slot dimensions: x=826, y=106+(slot*45), w=164, h=42
            SettlementPlugin.Instance.GoldText  = CreateSidebarText(modalObj.transform, "SidebarGold",
                826, 128, 164, 42, GoldIcon: SettlementPlugin.Instance.GoldIcon);
            SettlementPlugin.Instance.WoodText  = CreateSidebarText(modalObj.transform, "SidebarWood",
                826, 173, 164, 42, GoldIcon: SettlementPlugin.Instance.WoodIcon);
            SettlementPlugin.Instance.StoneText = CreateSidebarText(modalObj.transform, "SidebarStone",
                826, 218, 164, 42, GoldIcon: SettlementPlugin.Instance.StoneIcon);

            // ---- Tab Content objects ----
            SettlementPlugin.Instance.TabContentObjects.Clear();
            for (int i = 0; i < 5; i++)
            {
                GameObject tabContent = new GameObject($"TabContent_{i}", typeof(RectTransform));
                tabContent.transform.SetParent(modalObj.transform, false);
                SettlementUIHelper.SetRect(tabContent.GetComponent<RectTransform>(), 0, 0, 1024, 559);
                tabContent.SetActive(i == SettlementPlugin.Instance.SelectedTab);
                SettlementPlugin.Instance.TabContentObjects.Add(tabContent);

                if (i == 0) BuildOverviewTabContent(tabContent.transform);
                // Tab 1 (Buildings): populated on demand by RefreshBuildingsTab()
                // Tabs 2-4: reserved
            }

            // ---- Close button ----
            GameObject closeBtn = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
            closeBtn.transform.SetParent(modalObj.transform, false);
            closeBtn.GetComponent<Image>().color = Color.clear;
            closeBtn.GetComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.ToggleSettlementView());
            SettlementUIHelper.SetRect(closeBtn.GetComponent<RectTransform>(), 967, 10, 50, 50);

            GameObject xObj = new GameObject("X", typeof(RectTransform), typeof(TextMeshProUGUI));
            xObj.transform.SetParent(closeBtn.transform, false);
            var xTxt = xObj.GetComponent<TextMeshProUGUI>();
            xTxt.text = "X"; xTxt.fontSize = 30;
            xTxt.alignment = TextAlignmentOptions.Center;
            xTxt.color = new Color(0.9f, 0.2f, 0.2f);
            xTxt.outlineWidth = 0.2f; xTxt.outlineColor = Color.black;
            var xr = xObj.GetComponent<RectTransform>();
            xr.anchorMin = Vector2.zero; xr.anchorMax = Vector2.one;
            xr.offsetMin = xr.offsetMax = Vector2.zero;

            modalObj.SetActive(false);
            SettlementPlugin.Instance.SettlementModalObj = modalObj;
            SettlementPlugin.Instance.UpdateOverviewUI();
        }

        // -----------------------------------------------------------------------
        // Overview tab — settlement image fills the center frame interior exactly.
        // Frame inner bounds (measured from SettlementUI.png): x=315, y=133, w=387, h=306
        // -----------------------------------------------------------------------
        private static void BuildOverviewTabContent(Transform parent)
        {
            // Settlement image — fills center frame interior
            GameObject imgObj = new GameObject("SettlementImage", typeof(RectTransform), typeof(RawImage));
            imgObj.transform.SetParent(parent, false);
            SettlementUIHelper.SetRect(imgObj.GetComponent<RectTransform>(), 315, 133, 387, 306);
            SettlementPlugin.Instance.SettlementImageDisplay = imgObj.GetComponent<RawImage>();
            SettlementPlugin.Instance.SettlementImageDisplay.color = new Color(0, 0, 0, 0.4f);

            // Regenerate button — bottom-right corner just below the frame
            GameObject regBtn = SettlementUIHelper.CreateUIElement("RegenerateButton", parent,
                578, 447, 124, 30, null, new Color(0.08f, 0.08f, 0.08f, 0.75f));
            regBtn.AddComponent<Button>().onClick.AddListener(
                () => SettlementPlugin.Instance.TriggerImageGeneration());

            GameObject regTxt = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
            regTxt.transform.SetParent(regBtn.transform, false);
            var rTxt = regTxt.GetComponent<TextMeshProUGUI>();
            rTxt.text = "Regenerate Image"; rTxt.fontSize = 12;
            rTxt.alignment = TextAlignmentOptions.Center; rTxt.color = Color.white;
            var rtr = regTxt.GetComponent<RectTransform>();
            rtr.anchorMin = Vector2.zero; rtr.anchorMax = Vector2.one;
            rtr.offsetMin = rtr.offsetMax = Vector2.zero;
        }

        // Creates a text element sized to fit inside a right-sidebar slot.
        // The slot's wooden drawer texture (from SettlementUI.png) provides the visual frame.
        // Optional icon is placed on the left if the sprite is not null.
        private static TextMeshProUGUI CreateSidebarText(Transform parent, string name,
            float slotX, float slotY, float slotW, float slotH, Sprite GoldIcon = null)
        {
            // Optional icon (left edge of slot)
            if (GoldIcon != null)
            {
                GameObject iconObj = new GameObject(name + "_Icon", typeof(RectTransform), typeof(Image));
                iconObj.transform.SetParent(parent, false);
                iconObj.GetComponent<Image>().sprite = GoldIcon;
                iconObj.GetComponent<Image>().preserveAspect = true;
                // 24×24 icon, vertically centered in slot, 4px from left
                SettlementUIHelper.SetRect(iconObj.GetComponent<RectTransform>(),
                    slotX + 4, slotY + (slotH - 24f) * 0.5f, 24, 24);
            }

            float textOffsetX = GoldIcon != null ? 32f : 4f;

            GameObject txtObj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            txtObj.transform.SetParent(parent, false);
            var txt = txtObj.GetComponent<TextMeshProUGUI>();
            txt.text = "—";
            txt.fontSize = 15;
            txt.fontStyle = FontStyles.Bold;
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            txt.color = Color.white;
            txt.outlineWidth = 0.15f;
            txt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(txtObj.GetComponent<RectTransform>(),
                slotX + textOffsetX, slotY, slotW - textOffsetX - 4f, slotH);
            return txt;
        }
    }

    [HarmonyPatch(typeof(MapLocationDetails), "UpdateGraphics")]
    public static class Patch_MapLocationDetails_UpdateGraphics
    {
        public static void Postfix(MapLocationDetails __instance)
        {
            if (__instance == null || __instance.place == null || __instance.enterButtonTrans == null) return;
            if (SettlementPlugin.Instance == null) return;

            Transform parent = __instance.enterButtonTrans.parent;
            bool isSettlement = SettlementPlugin.Instance.IsSettlement(__instance.place);
            bool enterActive = __instance.enterButtonTrans.gameObject.activeSelf;

            // ---- Establish Settlement button ----
            var existingEstablish = parent.Find("EstablishSettlementButton");
            GameObject foundBtn = existingEstablish != null ? existingEstablish.gameObject : null;
            if (foundBtn == null)
            {
                foundBtn = new GameObject("EstablishSettlementButton",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                foundBtn.transform.SetParent(parent, false);

                var enterRect = __instance.enterButtonTrans.GetComponent<RectTransform>();
                var rect = foundBtn.GetComponent<RectTransform>();
                if (enterRect != null)
                {
                    rect.sizeDelta = enterRect.sizeDelta;
                    rect.anchorMin = enterRect.anchorMin;
                    rect.anchorMax = enterRect.anchorMax;
                    rect.anchoredPosition = enterRect.anchoredPosition - new Vector2(enterRect.sizeDelta.x + 10f, 0);
                }

                var img = foundBtn.GetComponent<Image>();
                if (SettlementPlugin.Instance.EstablishSettlementSprite != null)
                {
                    img.sprite = SettlementPlugin.Instance.EstablishSettlementSprite;
                    img.color = Color.white;
                }
                else
                {
                    img.color = new Color(0.5f, 0.2f, 0.2f, 0.8f);
                    var lbl = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                    lbl.transform.SetParent(foundBtn.transform, false);
                    var t = lbl.GetComponent<TextMeshProUGUI>();
                    t.text = "Establish Settlement"; t.fontSize = 12;
                    t.alignment = TextAlignmentOptions.Center;
                    SettlementPlugin.Log.LogWarning("EstablishSettlementSprite is NULL!");
                }

                foundBtn.GetComponent<Button>().onClick.AddListener(() =>
                {
                    SettlementPlugin.Instance.EstablishSettlement(__instance.place);
                    __instance.UpdateGraphics();
                });
            }

            // ---- View Settlement button ----
            var existingView = parent.Find("ViewSettlementButton");
            GameObject viewBtn = existingView != null ? existingView.gameObject : null;
            if (viewBtn == null)
            {
                viewBtn = new GameObject("ViewSettlementButton",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                viewBtn.transform.SetParent(parent, false);

                var enterRect = __instance.enterButtonTrans.GetComponent<RectTransform>();
                var vRect = viewBtn.GetComponent<RectTransform>();
                if (enterRect != null)
                {
                    vRect.sizeDelta = enterRect.sizeDelta;
                    vRect.anchorMin = enterRect.anchorMin;
                    vRect.anchorMax = enterRect.anchorMax;
                    // Mutually exclusive with establish button — same position
                    vRect.anchoredPosition = enterRect.anchoredPosition - new Vector2(enterRect.sizeDelta.x + 10f, 0);
                }

                viewBtn.GetComponent<Image>().color = new Color(0.1f, 0.35f, 0.1f, 0.85f);

                var vLabel = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                vLabel.transform.SetParent(viewBtn.transform, false);
                var vt = vLabel.GetComponent<TextMeshProUGUI>();
                vt.text = "View Settlement"; vt.fontSize = 11;
                vt.alignment = TextAlignmentOptions.Center; vt.color = Color.white;
                var vtr = vLabel.GetComponent<RectTransform>();
                vtr.anchorMin = Vector2.zero; vtr.anchorMax = Vector2.one;
                vtr.offsetMin = vtr.offsetMax = Vector2.zero;

                viewBtn.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (!SettlementPlugin.Instance.IsSettlementOpen)
                        SettlementPlugin.Instance.ToggleSettlementView();
                });
            }

            foundBtn.SetActive(!isSettlement && enterActive);
            viewBtn.SetActive(isSettlement && enterActive);
        }
    }
}
