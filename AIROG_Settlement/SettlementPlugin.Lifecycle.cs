using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

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
            if (index == 4) RefreshResearchTab();
        }

        public bool IsSettlement(Place p)
        {
            return p != null && CurrentSettlement != null && CurrentSettlement.LocationUuid == p.uuid;
        }

        /// <summary>True when the player's current location is this settlement — the gate
        /// for actually surfacing a pending event popup (see TryTriggerEvent).</summary>
        public bool IsPlayerAtSettlement() => IsSettlement(SS.I?.hackyManager?.currentPlace);

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
            CurrentSettlement.Resources["Knowledge"] = 0;
            PendingEvent = null;
            HideEventPopup();
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
                if (GoldText      != null) GoldText.text      = "Gold: —";
                if (WoodText      != null) WoodText.text      = "Wood: —";
                if (StoneText     != null) StoneText.text     = "Stone: —";
                if (KnowledgeText != null) KnowledgeText.text = "Knowledge: —";
            }
            else
            {
                int gold      = CurrentSettlement.Resources.TryGetValue("Gold",      out int g) ? g : 0;
                int wood      = CurrentSettlement.Resources.TryGetValue("Wood",      out int w) ? w : 0;
                int stone     = CurrentSettlement.Resources.TryGetValue("Stone",     out int s) ? s : 0;
                int knowledge = CurrentSettlement.Resources.TryGetValue("Knowledge", out int k) ? k : 0;
                if (GoldText      != null) GoldText.text      = $"Gold: {gold}";
                if (WoodText      != null) WoodText.text      = $"Wood: {wood}";
                if (StoneText     != null) StoneText.text     = $"Stone: {stone}";
                if (KnowledgeText != null) KnowledgeText.text = $"Knowledge: {knowledge}";
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

        // -----------------------------------------------------------------------
        // Event popup: shown independent of whether the Settlement modal itself is open, but
        // gated on the player actually being at the settlement's location — see
        // IsPlayerAtSettlement() and the check in Update() (SettlementPlugin.cs). ShowEventPopup
        // itself doesn't check location; it just renders whatever PendingEvent is handed to it.
        // Backdrop + panel are built once (SettlementMainUIPatch.cs); only the choice
        // buttons are rebuilt per event, same "destroy children, redraw" pattern the tabs use.
        // -----------------------------------------------------------------------

        public void ShowEventPopup(SettlementEventDefinition def)
        {
            if (EventPopupObj == null) { Log.LogError("Event popup UI not built; cannot show event."); return; }

            if (EventPopupTitleText != null) EventPopupTitleText.text = def.Title;
            if (EventPopupFlavorText != null) EventPopupFlavorText.text = def.FlavorText;

            Transform root = EventPopupObj.transform;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                if (child.name.StartsWith("Choice_")) Destroy(child.gameObject);
            }

            const float btnX = 284f, btnW = 456f, btnH = 36f, startY = 272f, step = 46f;
            for (int i = 0; i < def.Choices.Count; i++)
            {
                var choice = def.Choices[i];
                GameObject btnObj = SettlementUIHelper.CreateUIElement($"Choice_{i}", root,
                    btnX, startY + i * step, btnW, btnH, null, new Color(0.16f, 0.16f, 0.24f, 0.95f));
                var btn = btnObj.AddComponent<Button>();
                btn.onClick.AddListener(() => ResolveEvent(choice));

                GameObject txtObj = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
                txtObj.transform.SetParent(btnObj.transform, false);
                var txt = txtObj.GetComponent<TextMeshProUGUI>();
                txt.text = choice.Label;
                txt.fontSize = 14;
                txt.alignment = TextAlignmentOptions.Center;
                txt.color = Color.white;
                var tr = txtObj.GetComponent<RectTransform>();
                tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
                tr.offsetMin = tr.offsetMax = Vector2.zero;
            }

            EventPopupObj.SetActive(true);
            EventPopupObj.transform.SetAsLastSibling();
        }

        public void HideEventPopup()
        {
            if (EventPopupObj != null) EventPopupObj.SetActive(false);
        }

        public void ResolveEvent(SettlementEventChoice choice)
        {
            var def = PendingEvent;
            if (def == null) return;

            string outcome;
            try
            {
                outcome = choice.Resolve(CurrentSettlement) ?? "Nothing happens.";
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Event choice '{choice.Label}' for '{def.ID}' failed: {ex.Message}");
                outcome = "Something went wrong, and the moment passes without incident.";
            }

            CurrentSettlement.UpdateHappiness();
            PendingEvent = null;
            HideEventPopup();
            SaveSettlementData();
            ScheduleUiUpdate();

            Log.LogInfo($"[{def.Title}] {choice.Label}: {outcome}");
            var gameLog = SS.I?.hackyManager?.gameLogView;
            if (gameLog != null)
                _ = gameLog.LogText($"<color=#d8c8a0>[{CurrentSettlement.Name}] {def.Title}: {outcome}</color>");
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
