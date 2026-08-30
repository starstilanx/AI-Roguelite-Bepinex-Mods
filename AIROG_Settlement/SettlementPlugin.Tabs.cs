using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace AIROG_Settlement
{
    // Tab content builders (Trade, Buildings, Population) plus the shared
    // "no settlement yet" notice they all fall back to.
    public partial class SettlementPlugin
    {
        /// <summary>
        /// Draws the shared "no settlement yet" notice into a tab. All gameplay tabs are
        /// inert until the player establishes a settlement at a map location — previously
        /// you could build into a phantom settlement that could never produce anything.
        /// </summary>
        private void DrawNoSettlementNotice(Transform content, string tabTitle)
        {
            SettlementUIHelper.CreateUIElement("NoticeBg", content, 305, 124, 405, 323, null,
                new Color(0.06f, 0.06f, 0.10f, 0.88f));

            GameObject headerObj = new GameObject("Header", typeof(RectTransform), typeof(TextMeshProUGUI));
            headerObj.transform.SetParent(content, false);
            var hTxt = headerObj.GetComponent<TextMeshProUGUI>();
            hTxt.text = tabTitle;
            hTxt.fontSize = 18;
            hTxt.fontStyle = FontStyles.Bold;
            hTxt.alignment = TextAlignmentOptions.Center;
            hTxt.color = new Color(0.95f, 0.85f, 0.5f);
            hTxt.outlineWidth = 0.15f;
            hTxt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(headerObj.GetComponent<RectTransform>(), 305, 127, 405, 22);

            GameObject msgObj = new GameObject("Notice", typeof(RectTransform), typeof(TextMeshProUGUI));
            msgObj.transform.SetParent(content, false);
            var mTxt = msgObj.GetComponent<TextMeshProUGUI>();
            mTxt.text = "No settlement has been established yet.\n\n" +
                        "Travel to a location on the world map and press\n" +
                        "<b>Establish Settlement</b> to found one there.";
            mTxt.fontSize = 14;
            mTxt.alignment = TextAlignmentOptions.Center;
            mTxt.color = new Color(0.8f, 0.8f, 0.8f);
            SettlementUIHelper.SetRect(msgObj.GetComponent<RectTransform>(), 315, 230, 385, 90);
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

            if (!HasActiveSettlement) { DrawNoSettlementNotice(content, "Trade & Logistics"); return; }

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

            // Market conditions line — makes the fluctuating prices visible before clicking
            float woodPct = CurrentSettlement.TradePrices.TryGetValue("Wood", out float wp) ? wp * 100f : 100f;
            float stonePct = CurrentSettlement.TradePrices.TryGetValue("Stone", out float sp) ? sp * 100f : 100f;
            GameObject marketObj = new GameObject("Market", typeof(RectTransform), typeof(TextMeshProUGUI));
            marketObj.transform.SetParent(content, false);
            var marketTxt = marketObj.GetComponent<TextMeshProUGUI>();
            marketTxt.text = $"Market: Wood {woodPct:F0}%   Stone {stonePct:F0}%";
            marketTxt.fontSize = 11;
            marketTxt.alignment = TextAlignmentOptions.Center;
            marketTxt.color = new Color(0.7f, 0.7f, 0.65f);
            SettlementUIHelper.SetRect(marketObj.GetComponent<RectTransform>(), 309, 151, 397, 14);

            bool tradeAgreements = CurrentSettlement.Researched.Contains("trade_agreements");
            int woodBuy = Mathf.Max(1, Mathf.RoundToInt(20 * woodPct / 100f * (tradeAgreements ? 0.9f : 1f)));
            int woodSell = Mathf.Max(1, Mathf.RoundToInt(10 * woodPct / 100f * (tradeAgreements ? 1.1f : 1f)));
            int stoneBuy = Mathf.Max(1, Mathf.RoundToInt(30 * stonePct / 100f * (tradeAgreements ? 0.9f : 1f)));
            int stoneSell = Mathf.Max(1, Mathf.RoundToInt(15 * stonePct / 100f * (tradeAgreements ? 1.1f : 1f)));

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
                ("Import Wood", $"Buy 10 Wood for {woodBuy} Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Gold", out int g) && g >= woodBuy), new Action(() => {
                    CurrentSettlement.Resources["Gold"] -= woodBuy;
                    CurrentSettlement.AddResource("Wood", 10);
                })),
                ("Export Wood", $"Sell 10 Wood for {woodSell} Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Wood", out int w) && w >= 10), new Action(() => {
                    CurrentSettlement.Resources["Wood"] -= 10;
                    CurrentSettlement.AddResource("Gold", woodSell);
                })),
                ("Import Stone", $"Buy 10 Stone for {stoneBuy} Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Gold", out int g) && g >= stoneBuy), new Action(() => {
                    CurrentSettlement.Resources["Gold"] -= stoneBuy;
                    CurrentSettlement.AddResource("Stone", 10);
                })),
                ("Export Stone", $"Sell 10 Stone for {stoneSell} Gold", new Func<bool>(() => CurrentSettlement.Resources.TryGetValue("Stone", out int s) && s >= 10), new Action(() => {
                    CurrentSettlement.Resources["Stone"] -= 10;
                    CurrentSettlement.AddResource("Gold", stoneSell);
                }))
            };

            for (int i = 0; i < actions.Count; i++)
            {
                var act = actions[i];
                float rowY = 168f + i * 46f;
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

            if (!HasActiveSettlement) { DrawNoSettlementNotice(content, "Buildings"); return; }

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
                var instance = CurrentSettlement.GetBuilding(def.ID);
                bool isBuilt = instance != null;
                bool isMaxed = isBuilt && instance.Level >= BuildingDefinition.MAX_LEVEL;
                var upgradeCost = (isBuilt && !isMaxed) ? def.GetUpgradeCost(instance.Level) : null;
                bool canAfford = !isBuilt
                    ? def.CanAfford(CurrentSettlement)
                    : (!isMaxed && BuildingDefinition.CanAffordCost(CurrentSettlement, upgradeCost));

                // Row background
                Color bgColor = isBuilt
                    ? new Color(0.08f, 0.25f, 0.08f, 0.75f)
                    : new Color(0.08f, 0.08f, 0.14f, 0.75f);
                SettlementUIHelper.CreateUIElement($"Row_{i}", content, 309, rowY, 397, 44, null, bgColor);

                // Building name
                GameObject nameObj = new GameObject($"Name_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                nameObj.transform.SetParent(content, false);
                var nameTxt = nameObj.GetComponent<TextMeshProUGUI>();
                nameTxt.text = isBuilt ? $"{def.Name} (Lv {instance.Level})" : def.Name;
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
                if (isMaxed)
                {
                    costTxt.text = "Max Level";
                    costTxt.color = new Color(0.45f, 0.9f, 0.45f);
                }
                else
                {
                    var displayCost = isBuilt ? upgradeCost : def.Cost;
                    var parts = new System.Text.StringBuilder();
                    foreach (var kv in displayCost)
                        parts.Append($"{kv.Key}: {kv.Value}  ");
                    costTxt.text = parts.ToString().TrimEnd();
                    costTxt.color = canAfford ? new Color(0.95f, 0.85f, 0.45f) : new Color(0.9f, 0.35f, 0.35f);
                }
                costTxt.fontSize = 12;
                costTxt.alignment = TextAlignmentOptions.Center;
                SettlementUIHelper.SetRect(costObj.GetComponent<RectTransform>(), 507, rowY + 4, 104, 36);

                // Build / Upgrade button (maxed buildings get no button)
                if (!isMaxed)
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
                    string actionLabel = isBuilt ? "Upgrade" : "Build";
                    bTxt.text = canAfford ? actionLabel : "Can't Afford";
                    bTxt.fontSize = 11;
                    bTxt.alignment = TextAlignmentOptions.Center;
                    bTxt.color = canAfford ? Color.white : new Color(0.55f, 0.55f, 0.55f);
                    var btr = btnTxt.GetComponent<RectTransform>();
                    btr.anchorMin = Vector2.zero; btr.anchorMax = Vector2.one;
                    btr.offsetMin = btr.offsetMax = Vector2.zero;

                    string defId = def.ID;
                    bool doUpgrade = isBuilt;
                    btn.onClick.AddListener(() =>
                    {
                        if (doUpgrade) UpgradeBuilding(defId);
                        else BuildBuilding(defId);
                        RefreshBuildingsTab();
                        UpdateOverviewUI();
                    });
                }
            }
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Population tab: lists residents with their jobs and happiness.
        /// Residents arrive automatically over time (requires a Farm and free capacity).
        /// </summary>
        public void RefreshPopulationTab()
        {
            if (TabContentObjects.Count < 3 || TabContentObjects[2] == null) return;
            Transform content = TabContentObjects[2].transform;

            for (int i = content.childCount - 1; i >= 0; i--)
                Destroy(content.GetChild(i).gameObject);

            if (!HasActiveSettlement) { DrawNoSettlementNotice(content, "Population"); return; }

            SettlementUIHelper.CreateUIElement("PopBg", content, 305, 124, 405, 323, null,
                new Color(0.06f, 0.06f, 0.10f, 0.88f));

            GameObject headerObj = new GameObject("Header", typeof(RectTransform), typeof(TextMeshProUGUI));
            headerObj.transform.SetParent(content, false);
            var hTxt = headerObj.GetComponent<TextMeshProUGUI>();
            hTxt.text = $"Population  ({CurrentSettlement.Residents.Count}/{CurrentSettlement.GetPopulationCap()})";
            hTxt.fontSize = 18;
            hTxt.fontStyle = FontStyles.Bold;
            hTxt.alignment = TextAlignmentOptions.Center;
            hTxt.color = new Color(0.95f, 0.85f, 0.5f);
            hTxt.outlineWidth = 0.15f;
            hTxt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(headerObj.GetComponent<RectTransform>(), 305, 127, 405, 22);

            if (CurrentSettlement.Residents.Count == 0)
            {
                GameObject emptyObj = new GameObject("Empty", typeof(RectTransform), typeof(TextMeshProUGUI));
                emptyObj.transform.SetParent(content, false);
                var eTxt = emptyObj.GetComponent<TextMeshProUGUI>();
                eTxt.text = CurrentSettlement.HasBuilding("farm")
                    ? "No residents yet.\nWith a farm providing food, settlers will arrive over time."
                    : "No residents yet.\nBuild a Farm — nobody settles where there is no food.";
                eTxt.fontSize = 13;
                eTxt.alignment = TextAlignmentOptions.Center;
                eTxt.color = new Color(0.75f, 0.75f, 0.75f);
                SettlementUIHelper.SetRect(emptyObj.GetComponent<RectTransform>(), 315, 240, 385, 60);
                return;
            }

            for (int i = 0; i < CurrentSettlement.Residents.Count && i < 6; i++)
            {
                var resident = CurrentSettlement.Residents[i];
                float rowY = 152f + i * 48f;

                SettlementUIHelper.CreateUIElement($"Row_{i}", content, 309, rowY, 397, 44, null,
                    new Color(0.08f, 0.08f, 0.14f, 0.75f));

                GameObject nameObj = new GameObject($"Name_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                nameObj.transform.SetParent(content, false);
                var nameTxt = nameObj.GetComponent<TextMeshProUGUI>();
                nameTxt.text = resident.Name;
                nameTxt.fontSize = 14;
                nameTxt.fontStyle = FontStyles.Bold;
                nameTxt.alignment = TextAlignmentOptions.Left;
                nameTxt.color = Color.white;
                SettlementUIHelper.SetRect(nameObj.GetComponent<RectTransform>(), 314, rowY + 4, 220, 20);

                GameObject jobObj = new GameObject($"Job_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                jobObj.transform.SetParent(content, false);
                var jobTxt = jobObj.GetComponent<TextMeshProUGUI>();
                jobTxt.text = string.IsNullOrEmpty(resident.Trait) ? resident.Job : $"{resident.Job} ({resident.Trait})";
                jobTxt.fontSize = 11;
                jobTxt.alignment = TextAlignmentOptions.Left;
                jobTxt.color = new Color(0.72f, 0.72f, 0.72f);
                SettlementUIHelper.SetRect(jobObj.GetComponent<RectTransform>(), 314, rowY + 26, 220, 16);

                // Happiness readout
                GameObject hapObj = new GameObject($"Hap_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                hapObj.transform.SetParent(content, false);
                var hapTxt = hapObj.GetComponent<TextMeshProUGUI>();
                // Plain ASCII markers — the game's TMP font atlas has no emoji glyphs
                string face = resident.Happiness >= 70 ? "Content" : resident.Happiness >= 45 ? "Fine" : "Unhappy";
                hapTxt.text = $"{face} ({resident.Happiness})";
                hapTxt.fontSize = 14;
                hapTxt.alignment = TextAlignmentOptions.Right;
                hapTxt.color = resident.Happiness >= 70 ? new Color(0.55f, 0.95f, 0.55f)
                             : resident.Happiness >= 45 ? new Color(0.95f, 0.9f, 0.6f)
                             : new Color(0.95f, 0.5f, 0.5f);
                SettlementUIHelper.SetRect(hapObj.GetComponent<RectTransform>(), 560, rowY + 10, 140, 24);
            }
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Research tab: a flat list of tech nodes (Knowledge + a secondary resource each),
        /// same row-list pattern as RefreshBuildingsTab rather than a branching tree UI.
        /// </summary>
        public void RefreshResearchTab()
        {
            if (TabContentObjects.Count < 5 || TabContentObjects[4] == null) return;
            Transform content = TabContentObjects[4].transform;

            for (int i = content.childCount - 1; i >= 0; i--)
                Destroy(content.GetChild(i).gameObject);

            if (!HasActiveSettlement) { DrawNoSettlementNotice(content, "Research"); return; }

            SettlementUIHelper.CreateUIElement("ResearchBg", content, 305, 124, 405, 323, null,
                new Color(0.06f, 0.06f, 0.10f, 0.88f));

            GameObject headerObj = new GameObject("Header", typeof(RectTransform), typeof(TextMeshProUGUI));
            headerObj.transform.SetParent(content, false);
            var hTxt = headerObj.GetComponent<TextMeshProUGUI>();
            hTxt.text = "Research";
            hTxt.fontSize = 18;
            hTxt.fontStyle = FontStyles.Bold;
            hTxt.alignment = TextAlignmentOptions.Center;
            hTxt.color = new Color(0.95f, 0.85f, 0.5f);
            hTxt.outlineWidth = 0.15f;
            hTxt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(headerObj.GetComponent<RectTransform>(), 305, 127, 405, 22);

            var catalog = ResearchCatalog.All;
            for (int i = 0; i < catalog.Length; i++)
            {
                var def = catalog[i];
                float rowY = 152f + i * 48f;
                bool researched = CurrentSettlement.Researched.Contains(def.ID);
                bool available = def.IsAvailable(CurrentSettlement);
                bool canAfford = !researched && available && def.CanAfford(CurrentSettlement);

                Color bgColor = researched
                    ? new Color(0.08f, 0.25f, 0.08f, 0.75f)
                    : new Color(0.08f, 0.08f, 0.14f, 0.75f);
                SettlementUIHelper.CreateUIElement($"Row_{i}", content, 309, rowY, 397, 44, null, bgColor);

                GameObject nameObj = new GameObject($"Name_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                nameObj.transform.SetParent(content, false);
                var nameTxt = nameObj.GetComponent<TextMeshProUGUI>();
                nameTxt.text = def.Name;
                nameTxt.fontSize = 14;
                nameTxt.fontStyle = FontStyles.Bold;
                nameTxt.alignment = TextAlignmentOptions.Left;
                nameTxt.color = researched ? new Color(0.65f, 1f, 0.65f) : Color.white;
                SettlementUIHelper.SetRect(nameObj.GetComponent<RectTransform>(), 314, rowY + 4, 188, 20);

                GameObject descObj = new GameObject($"Desc_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                descObj.transform.SetParent(content, false);
                var descTxt = descObj.GetComponent<TextMeshProUGUI>();
                descTxt.text = def.Description;
                descTxt.fontSize = 11;
                descTxt.alignment = TextAlignmentOptions.Left;
                descTxt.color = new Color(0.72f, 0.72f, 0.72f);
                SettlementUIHelper.SetRect(descObj.GetComponent<RectTransform>(), 314, rowY + 26, 188, 16);

                GameObject costObj = new GameObject($"Cost_{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                costObj.transform.SetParent(content, false);
                var costTxt = costObj.GetComponent<TextMeshProUGUI>();
                if (researched)
                {
                    costTxt.text = "Researched";
                    costTxt.color = new Color(0.45f, 0.9f, 0.45f);
                }
                else if (!available)
                {
                    costTxt.text = "Locked";
                    costTxt.color = new Color(0.6f, 0.6f, 0.6f);
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

                if (!researched && available)
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
                    bTxt.text = canAfford ? "Research" : "Can't Afford";
                    bTxt.fontSize = 11;
                    bTxt.alignment = TextAlignmentOptions.Center;
                    bTxt.color = canAfford ? Color.white : new Color(0.55f, 0.55f, 0.55f);
                    var btr = btnTxt.GetComponent<RectTransform>();
                    btr.anchorMin = Vector2.zero; btr.anchorMax = Vector2.one;
                    btr.offsetMin = btr.offsetMax = Vector2.zero;

                    string defId = def.ID;
                    btn.onClick.AddListener(() =>
                    {
                        ResearchTech(defId);
                        RefreshResearchTab();
                        UpdateOverviewUI();
                    });
                }
            }
        }
    }
}
