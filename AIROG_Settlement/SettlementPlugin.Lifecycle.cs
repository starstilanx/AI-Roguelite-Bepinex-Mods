using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIROG_Settlement
{
    // Establish/toggle/overview-refresh/image-generation — the settlement's core lifecycle.
    public partial class SettlementPlugin
    {
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
            if (index == 2) RefreshPopulationTab();
            if (index == 3) RefreshTradeTab();
        }

        public bool IsSettlement(Place p)
        {
            return p != null && CurrentSettlement != null && CurrentSettlement.LocationUuid == p.uuid;
        }

        public void EstablishSettlement(Place p)
        {
            if (p == null) return;

            // Establishing is a full fresh start — a previous settlement elsewhere is abandoned
            // entirely (previously this reset resources but silently kept old buildings/residents).
            if (!string.IsNullOrEmpty(CurrentSettlement.LocationUuid))
                Log.LogWarning($"Abandoning previous settlement '{CurrentSettlement.Name}' to establish a new one.");

            CurrentSettlement = new SettlementState
            {
                LocationUuid = p.uuid,
                Name = p.GetPrettyName()
            };
            // 150 gold: enough for two producers (40+60) with 50 left toward a Farm,
            // so the opening build order can't dead-end the economy.
            CurrentSettlement.Resources["Gold"] = 150;
            CurrentSettlement.Resources["Wood"] = 0;
            CurrentSettlement.Resources["Stone"] = 0;
            Log.LogInfo($"Established settlement at {p.GetPrettyName()} ({p.uuid})");

            SaveSettlementData();
            TriggerImageGeneration();

            if (!IsSettlementOpen) ToggleSettlementView();
            else UpdateOverviewUI();
        }

        /// <summary>True once the player has established the settlement at a map location.</summary>
        public bool HasActiveSettlement => !string.IsNullOrEmpty(CurrentSettlement?.LocationUuid);

        public void UpdateOverviewUI()
        {
            // Settlement name (persistent, always visible).
            // Check LocationUuid, not Name — the default state has a placeholder name.
            if (OverviewNameText != null)
            {
                OverviewNameText.text = HasActiveSettlement
                    ? CurrentSettlement.Name
                    : "No Active Settlement";
            }

            // Resources in right sidebar (persistent, always visible)
            if (!HasActiveSettlement)
            {
                if (GoldText  != null) GoldText.text  = "Gold: —";
                if (WoodText  != null) WoodText.text  = "Wood: —";
                if (StoneText != null) StoneText.text = "Stone: —";
            }
            else
            {
                int gold  = CurrentSettlement.Resources.TryGetValue("Gold",  out int g) ? g : 0;
                int wood  = CurrentSettlement.Resources.TryGetValue("Wood",  out int w) ? w : 0;
                int stone = CurrentSettlement.Resources.TryGetValue("Stone", out int s) ? s : 0;
                if (GoldText  != null) GoldText.text  = $"Gold: {gold}";
                if (WoodText  != null) WoodText.text  = $"Wood: {wood}";
                if (StoneText != null) StoneText.text = $"Stone: {stone}";
            }

            // Population in right sidebar (persistent)
            if (PopulationText != null)
            {
                PopulationText.text = string.IsNullOrEmpty(CurrentSettlement.LocationUuid)
                    ? "Pop: —"
                    : $"Pop: {CurrentSettlement.Residents.Count}/{CurrentSettlement.GetPopulationCap()}";
            }

            // Settlement image (Overview tab only). Textures are cached by ImageUuid so we
            // don't re-read the PNG and leak a new Texture2D on every refresh.
            if (SettlementImageDisplay != null)
            {
                bool shown = false;
                if (!string.IsNullOrEmpty(CurrentSettlement.ImageUuid) && SS.I != null)
                {
                    if (CurrentSettlement.ImageUuid == _loadedImageUuid && _loadedImageTex != null)
                    {
                        shown = true; // Already displaying this image
                    }
                    else
                    {
                        string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg,
                                                   CurrentSettlement.ImageUuid + ".png");
                        if (File.Exists(path))
                        {
                            byte[] data = File.ReadAllBytes(path);
                            Texture2D tex = new Texture2D(2, 2);
                            if (tex.LoadImage(data))
                            {
                                if (_loadedImageTex != null) Destroy(_loadedImageTex);
                                _loadedImageTex = tex;
                                _loadedImageUuid = CurrentSettlement.ImageUuid;
                                SettlementImageDisplay.texture = tex;
                                SettlementImageDisplay.color = Color.white;
                                shown = true;
                            }
                            else Destroy(tex);
                        }
                    }
                }

                if (!shown)
                {
                    SettlementImageDisplay.texture = null;
                    SettlementImageDisplay.color = new Color(0, 0, 0, 0.4f);
                }
            }
        }

        private Texture2D _loadedImageTex;
        private string _loadedImageUuid;

        public void TriggerImageGeneration()
        {
            if (string.IsNullOrEmpty(CurrentSettlement.LocationUuid)) return;

            Place p = null;
            if (SS.I.uuidToGameEntityMap.ContainsKey(CurrentSettlement.LocationUuid))
                p = SS.I.uuidToGameEntityMap[CurrentSettlement.LocationUuid] as Place;
            if (p == null) return;

            // Fire-and-forget on the MAIN thread. Task.Run would move game/Unity API
            // calls onto a pool thread, which Unity does not allow.
            _ = GenerateSettlementImage(p);
        }

        public async System.Threading.Tasks.Task GenerateSettlementImage(Place p)
        {
            try
            {
                Log.LogInfo("Starting settlement image generation...");

                string prompt = $"A cozy and prospering settlement called {CurrentSettlement.Name}, located in {p.GetPrettyName()}. {p.GetPotentiallyNullDescription()}";
                string uuid = Guid.NewGuid().ToString();

                SettlementImageEntity entity = new SettlementImageEntity(prompt, uuid, p.manager as GameplayManager);

                // NOTE: GetEntImgSettings returns a reference to the shared settings object.
                // Do NOT mutate .x/.y — it would permanently corrupt game image settings.
                var settings = SS.I.settingsPojo.GetEntImgSettings(SettingsPojo.EntImgType.PLACE);
                await AIAsker.getGeneratedImage(settings, entity, true);

                CurrentSettlement.ImageUuid = uuid;
                SaveSettlementData();
                _needsUiUpdate = true;
            }
            catch (Exception e)
            {
                Log.LogError($"Error generating settlement image: {e}");
            }
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
    }
}
