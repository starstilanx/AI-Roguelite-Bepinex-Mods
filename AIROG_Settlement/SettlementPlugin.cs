using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using System.IO;
using System.Collections.Generic;
using TMPro;
using System;

namespace AIROG_Settlement
{
    // Split via `partial` across SettlementPlugin.*.cs (no external file references this
    // plugin's internals — only the Harmony patch classes at the bottom of the original file
    // touched SettlementPlugin.Instance/.Log, and those got their own files too). See
    // SettlementPlugin.Simulation.cs (per-turn tick), SettlementPlugin.Lifecycle.cs
    // (establish/toggle/overview/image gen/event popup show-hide), SettlementPlugin.Persistence.cs
    // (save/load), SettlementPlugin.Buildings.cs (build/upgrade/research), SettlementPlugin.Tabs.cs
    // (tab UI), SettlementPlugin.Events.cs (event catalog + per-turn trigger roll),
    // SettlementHarmonyPatches.cs / SettlementMainUIPatch.cs / SettlementMapButtonsPatch.cs
    // (the previously-bundled standalone Harmony patch classes). SettlementResearch.cs holds
    // the (non-partial) research catalog, mirroring BuildingCatalog in SettlementData.cs.
    [BepInPlugin("com.airog.settlement", "Settlement Mod", "1.2.0")]
    public partial class SettlementPlugin : BaseUnityPlugin
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
        public TextMeshProUGUI GoldText, WoodText, StoneText, PopulationText, KnowledgeText;
        public RawImage SettlementImageDisplay;

        public GameObject CenterWorkspaceObj;
        public List<GameObject> TabContentObjects = new List<GameObject>();

        // Event popup (built alongside the Settlement modal; independent of whether the
        // modal itself is open, but only ever shown while the player is at the settlement's
        // own location — see IsPlayerAtSettlement() and the check in Update())
        public GameObject EventPopupObj;
        public TextMeshProUGUI EventPopupTitleText;
        public TextMeshProUGUI EventPopupFlavorText;

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

            // A queued event only surfaces once the player is actually standing at the
            // settlement — not the moment it rolls, wherever the player happens to be in the
            // story. Polling here catches both "event rolled while already there" (shows next
            // frame) and "player travels to a settlement with an event already waiting".
            if (PendingEvent != null && EventPopupObj != null && !EventPopupObj.activeSelf && IsPlayerAtSettlement())
                ShowEventPopup(PendingEvent);
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
    }
}
