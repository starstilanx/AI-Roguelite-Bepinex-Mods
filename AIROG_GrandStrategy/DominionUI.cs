using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_WorldExpansion;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_GrandStrategy
{
    // ─── Dominion Panel ──────────────────────────────────────────────────────────
    // A "DOM" button on the world map (beside WorldExpansion's "POL" political lens
    // button) toggles a control panel: founding, status, every order as a button,
    // tax edicts, and petition judgments. Same procedural-UI conventions as
    // StrategicMapUI: cloned frame for the button, layout-group panel on mapViewTrans.
    public static class DominionUI
    {
        private const string BUTTON_NAME = "DominionButton_Mod";
        private const string PANEL_NAME  = "DominionPanel_Mod";

        private static bool _panelOn;
        private static int _targetIdx;
        private static int _impIdx;
        private static int _wonderIdx;
        private static int _holdingIdx;
        private static string _lastResult = "";

        private static readonly Color GOLD    = new Color(1f, 0.85f, 0.3f);
        private static readonly Color MUTED   = new Color(0.78f, 0.78f, 0.83f);
        private static readonly Color BTN_FACE = new Color(0.16f, 0.16f, 0.24f, 0.95f);
        private static readonly Color BTN_WARM = new Color(0.28f, 0.16f, 0.10f, 0.95f);
        private static readonly Color DIVIDER = new Color(0.45f, 0.42f, 0.30f);

        // ─── MapModal hooks (mirrors StrategicMapUI) ──────────────────────────────

        [HarmonyPatch(typeof(MapModal), "ShowWorldView")]
        [HarmonyPostfix]
        public static void Postfix_ShowWorldView(MapModal __instance, VoronoiWorld vw)
        {
            try
            {
                EnsureDominionButton(__instance);
                if (_panelOn) BuildPanel(__instance);
                else ClearPanel(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GrandStrategy] Dominion panel failed: {e}");
            }
        }

        [HarmonyPatch(typeof(MapModal), "ShowDetachedView")]
        [HarmonyPostfix]
        public static void Postfix_ShowDetachedView(MapModal __instance)
        {
            SetButtonVisible(__instance, false);
            ClearPanel(__instance);
        }

        [HarmonyPatch(typeof(MapModal), "ShowUniv")]
        [HarmonyPostfix]
        public static void Postfix_ShowUniv(MapModal __instance)
        {
            SetButtonVisible(__instance, false);
            ClearPanel(__instance);
        }

        [HarmonyPatch(typeof(MapModal), "HideMapModal")]
        [HarmonyPostfix]
        public static void Postfix_HideMapModal(MapModal __instance)
        {
            ClearPanel(__instance);
        }

        // ─── Toggle button ────────────────────────────────────────────────────────

        private static void EnsureDominionButton(MapModal modal)
        {
            if (modal.jumpToCurrentLocationButton == null) return;
            Transform parent = modal.jumpToCurrentLocationButton.transform.parent;
            Transform existing = parent.Find(BUTTON_NAME);
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                UpdateButtonLabel(existing.gameObject);
                return;
            }

            GameObject btnObj = UnityEngine.Object.Instantiate(
                modal.jumpToCurrentLocationButton.gameObject, parent);
            btnObj.name = BUTTON_NAME;

            // ButtonPressEffect caches its child Graphics in Awake (which already ran on
            // Instantiate); destroying the cloned children below would leave it holding
            // dead references and NRE on every press — remove it outright
            var pressFx = btnObj.GetComponent<ButtonPressEffect>();
            if (pressFx != null) UnityEngine.Object.DestroyImmediate(pressFx);

            // POL sits one button-width left of the jump button; we take the next slot out
            RectTransform rt = (RectTransform)btnObj.transform;
            RectTransform srcRt = (RectTransform)modal.jumpToCurrentLocationButton.transform;
            rt.anchoredPosition = srcRt.anchoredPosition + new Vector2(-(srcRt.rect.width + 12f) * 2f, 0f);

            foreach (var img in btnObj.GetComponentsInChildren<Image>(true))
                if (img.gameObject != btnObj) UnityEngine.Object.DestroyImmediate(img.gameObject);
            foreach (var raw in btnObj.GetComponentsInChildren<RawImage>(true))
                if (raw != null && raw.gameObject != btnObj) UnityEngine.Object.DestroyImmediate(raw.gameObject);
            foreach (var t in btnObj.GetComponentsInChildren<TMP_Text>(true))
                if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);

            GameObject txtObj = new GameObject("DomLabel", typeof(RectTransform));
            txtObj.layer = btnObj.layer;
            txtObj.transform.SetParent(btnObj.transform, false);
            var trt = (RectTransform)txtObj.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var lbl = txtObj.AddComponent<TextMeshProUGUI>();
            if (modal.voronoiWorldTitle != null) lbl.font = modal.voronoiWorldTitle.font;
            lbl.text = "DOM";
            lbl.fontSize = 15;
            lbl.fontStyle = FontStyles.Bold;
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.raycastTarget = false;

            Button btn = btnObj.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(() => OnPanelToggled(modal));

            UpdateButtonLabel(btnObj);
        }

        private static void UpdateButtonLabel(GameObject btnObj)
        {
            var txt = btnObj.GetComponentInChildren<TMP_Text>();
            if (txt != null) txt.color = _panelOn ? GOLD : MUTED;
            var frame = btnObj.GetComponent<Image>();
            if (frame != null) frame.color = _panelOn ? new Color(1f, 0.95f, 0.7f) : Color.white;
        }

        private static void OnPanelToggled(MapModal modal)
        {
            _panelOn = !_panelOn;
            if (!_panelOn) _lastResult = ""; // clear stale result text when closing
            Click(modal);

            Transform btn = modal.jumpToCurrentLocationButton?.transform.parent.Find(BUTTON_NAME);
            if (btn != null) UpdateButtonLabel(btn.gameObject);

            if (_panelOn) BuildPanel(modal);
            else ClearPanel(modal);
        }

        private static void SetButtonVisible(MapModal modal, bool visible)
        {
            Transform btn = modal.jumpToCurrentLocationButton?.transform.parent.Find(BUTTON_NAME);
            if (btn != null) btn.gameObject.SetActive(visible);
        }

        private static void Click(MapModal modal)
        {
            modal.manager?.soundManager?.smallClickSoundFxObj?.PlayNextSound();
        }

        // ─── Panel ────────────────────────────────────────────────────────────────

        private static void ClearPanel(MapModal modal)
        {
            Transform old = modal.mapViewTrans?.Find(PANEL_NAME);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
        }

        private static void BuildPanel(MapModal modal)
        {
            ClearPanel(modal);
            if (modal.mapViewTrans == null || modal.manager == null) return;
            TMP_FontAsset font = modal.voronoiWorldTitle != null ? modal.voronoiWorldTitle.font : null;

            GameObject panel = new GameObject(PANEL_NAME, typeof(RectTransform));
            panel.layer = modal.mapViewTrans.gameObject.layer;
            panel.transform.SetParent(modal.mapViewTrans, false);
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = new Vector2(0, 0.5f);   // POL legend owns the right edge; we take the left
            prt.anchorMax = new Vector2(0, 0.5f);
            prt.pivot     = new Vector2(0, 0.5f);
            prt.anchoredPosition = new Vector2(14, 0);
            prt.sizeDelta = new Vector2(370, 100);

            panel.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.09f, 0.94f);
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.spacing = 5;
            vlg.padding = new RectOffset(12, 12, 10, 10);
            panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            try { Populate(modal, panel, font); }
            catch (Exception e) { Debug.LogError($"[GrandStrategy] Dominion panel populate failed: {e}"); }
        }

        private static void Populate(MapModal modal, GameObject panel, TMP_FontAsset font)
        {
            GameplayManager manager = modal.manager;
            var s = GrandStrategyData.State;

            if (!s.Founded)
            {
                AddText(panel, font, "<b>♛ DOMINION</b>", 19, GOLD);
                AddText(panel, font,
                    "You rule no dominion yet. Found one here — your current region becomes the capital (it must be unclaimed).",
                    13, MUTED);
                var frow = AddRow(panel, 30);
                AddButton(frow, font, "FOUND DOMINION HERE", () =>
                {
                    string pname = null;
                    try { pname = SS.I?.hackyManager?.playerCharacter?.name; } catch { }
                    string dname = string.IsNullOrWhiteSpace(pname) ? "New Dominion" : $"Dominion of {pname}";
                    _lastResult = DominionManager.FoundDominion(manager, dname);
                    Click(modal);
                    BuildPanel(modal);
                }, BTN_WARM);
                AddText(panel, font, "<i>(GS_FOUND <name> in the console picks a custom name)</i>", 11, new Color(0.55f, 0.55f, 0.6f));
                AddResult(panel, font);
                return;
            }

            var fac = WorldData.GetFactionData(s.FactionUuid);

            // ── Status ──
            AddText(panel, font, $"<b>♛ {s.DominionName.ToUpperInvariant()}</b>", 19, GOLD);
            AddText(panel, font,
                $"Treasury {s.Treasury}g · Army {s.ArmyStrength} · CP {s.CommandPoints}/{s.MaxCommandPoints} · Pop {fac.Population}",
                14, MUTED);

            int unrestTotal = s.Holdings.Values.Sum(h => h.Unrest);
            AddText(panel, font,
                $"Holdings {s.Holdings.Count} · Vassals {s.VassalNames.Count} · Unrest {unrestTotal}"
                + (string.IsNullOrEmpty(s.ActiveVictory) ? "" : $" · <color=#FFD34D>★ {s.ActiveVictory}</color>"),
                13, MUTED);

            var wars = WorldData.CurrentState.ActiveWars.Values
                .Where(w => w.ActorUuid == s.FactionUuid || w.TargetUuid == s.FactionUuid)
                .Select(w => w.ActorUuid == s.FactionUuid ? w.TargetName : w.ActorName)
                .ToList();
            if (wars.Count > 0)
                AddText(panel, font, $"<color=#FF7766>⚔ At war: {string.Join(", ", wars)}</color>", 13, Color.white);

            if (!string.IsNullOrEmpty(s.WonderInProgress))
            {
                var wip = OrderSystem.WonderDefs.FirstOrDefault(w => w.Key == s.WonderInProgress);
                AddText(panel, font,
                    $"Building: {(wip != null ? wip.Name : s.WonderInProgress)} — {s.WonderTicksLeft} tick(s) left",
                    13, new Color(0.75f, 0.85f, 1f));
            }

            // ── Realm orders ──
            AddText(panel, font, "── REALM ──", 12, DIVIDER);
            var r1 = AddRow(panel);
            AddButton(r1, font, "ANNEX 25g",    () => DoOrder(modal, "ANNEX", ""));
            AddButton(r1, font, "TRADE",         () => DoOrder(modal, "TRADE", ""));
            AddButton(r1, font, "DISBAND",       () => DoOrder(modal, "DISBAND", ""));
            var r2 = AddRow(panel);
            AddButton(r2, font, "LEVY",          () => DoOrder(modal, "LEVY", ""));
            AddButton(r2, font, "FESTIVAL 25g",  () => DoOrder(modal, "FESTIVAL", ""));

            // Holding cycle: determines which holding DEVELOP targets (default: capital)
            var holdingList = s.Holdings.Values.ToList();
            string selectedHolding = holdingList.Count > 0
                ? holdingList[_holdingIdx % holdingList.Count].Name
                : s.CapitalName;
            bool multiHolding = holdingList.Count > 1;

            string imp = OrderSystem.Improvements[_impIdx % OrderSystem.Improvements.Length];
            // Syntax passed to ResolveOrder: "IMP" for capital, "IMP holdingName" for others
            string devArg = selectedHolding == s.CapitalName
                ? imp
                : $"{imp} {selectedHolding}";

            var r3 = AddRow(panel);
            AddButton(r3, font, $"{imp} ▸", () => { _impIdx++; Click(modal); BuildPanel(modal); });
            if (multiHolding)
                AddButton(r3, font, $"@ {selectedHolding} ▸",
                    () => { _holdingIdx++; Click(modal); BuildPanel(modal); },
                    new Color(0.12f, 0.20f, 0.16f, 0.95f));
            AddButton(r3, font, "DEVELOP 30g", () => DoOrder(modal, "DEVELOP", devArg));

            var wd = OrderSystem.WonderDefs[_wonderIdx % OrderSystem.WonderDefs.Count];
            var r4 = AddRow(panel);
            AddButton(r4, font, $"{wd.Key} ▸", () => { _wonderIdx++; Click(modal); BuildPanel(modal); });
            AddButton(r4, font, $"PROJECT {wd.Gold}g", () => DoOrder(modal, "PROJECT", wd.Key));

            var r5 = AddRow(panel);
            AddButton(r5, font, $"TAX: {s.TaxPolicy} ▸", () =>
            {
                s.TaxPolicy = s.TaxPolicy == "LOW" ? "NORMAL" : s.TaxPolicy == "NORMAL" ? "HIGH" : "LOW";
                GrandStrategyData.LogDeed($"{s.DominionName} decreed {s.TaxPolicy.ToLower()} taxation across the realm.");
                GrandStrategyData.SaveToCurrentDir();
                Click(modal);
                BuildPanel(modal);
            });

            // ── Targeted orders (Diplomacy & War) ──
            var targets = EligibleTargets(manager);
            string tName = targets.Count > 0 ? targets[_targetIdx % targets.Count].GetPrettyName() : "";
            AddText(panel, font, "── DIPLOMACY & WAR ──", 12, DIVIDER);
            var tr = AddRow(panel);
            AddButton(tr, font, targets.Count > 0 ? $"TARGET: {tName} ▸" : "TARGET: (no factions)",
                () => { _targetIdx++; Click(modal); BuildPanel(modal); },
                new Color(0.12f, 0.20f, 0.16f, 0.95f));
            // Also show SCOUT inline with target selector
            AddButton(tr, font, "SCOUT 15g", () => DoOrder(modal, "SCOUT", tName));

            var d1 = AddRow(panel);
            AddButton(d1, font, "ENVOY 20g",      () => DoOrder(modal, "ENVOY", tName));
            AddButton(d1, font, "FABRICATE 2CP",  () => DoOrder(modal, "FABRICATE", tName));
            AddButton(d1, font, "PEACE 25g",      () => DoOrder(modal, "PEACE", tName));
            var d2 = AddRow(panel);
            AddButton(d2, font, "WAR 2CP",      () => DoOrder(modal, "WAR", tName), BTN_WARM);
            AddButton(d2, font, "CAMPAIGN 2CP", () => DoOrder(modal, "CAMPAIGN", tName), BTN_WARM);
            AddButton(d2, font, "PILLAGE 2CP",  () => DoOrder(modal, "PILLAGE", tName), BTN_WARM);
            var d3 = AddRow(panel);
            AddButton(d3, font, "INCITE 30g",    () => DoOrder(modal, "INCITE", tName));
            AddButton(d3, font, "SABOTAGE 2CP",  () => DoOrder(modal, "SABOTAGE", tName));
            AddButton(d3, font, "VASSAL 2CP",    () => DoOrder(modal, "VASSAL", tName));

            // ── Petition ──
            if (s.PendingPetition != null)
            {
                AddText(panel, font, "── PETITION ──", 12, DIVIDER);
                AddText(panel, font, s.PendingPetition.Text, 13, new Color(0.92f, 0.88f, 0.75f));
                var pr = AddRow(panel);
                AddButton(pr, font, "ACCEPT", () => DoPetition(modal, true));
                AddButton(pr, font, "REJECT", () => DoPetition(modal, false));
            }

            AddResult(panel, font);
        }

        // ─── Actions ──────────────────────────────────────────────────────────────

        private static void DoOrder(MapModal modal, string type, string arg)
        {
            if (string.IsNullOrEmpty(arg) && RequiresTarget(type))
                _lastResult = "No target faction available.";
            else
                _lastResult = OrderSystem.Issue(modal.manager, type, arg);
            Click(modal);
            BuildPanel(modal);
        }

        private static bool RequiresTarget(string type)
        {
            switch (type)
            {
                case "ENVOY": case "FABRICATE": case "WAR": case "CAMPAIGN":
                case "PILLAGE": case "PEACE": case "VASSAL":
                case "INCITE": case "SABOTAGE": case "SCOUT":
                    return true;
                default:
                    return false;
            }
        }

        private static void DoPetition(MapModal modal, bool accept)
        {
            string r = CourtSystem.Resolve(GrandStrategyData.State, accept);
            _lastResult = r != null && r.StartsWith("!") ? r.Substring(1) : (r ?? "No petition awaits.");
            Click(modal);
            BuildPanel(modal);
        }

        private static List<Faction> EligibleTargets(GameplayManager manager)
        {
            var s = GrandStrategyData.State;
            return (manager.GetCurrentFactions() ?? new List<Faction>())
                .Where(f => f != null && f.uuid != s.FactionUuid && f.GetPrettyName() != "Player"
                            && !WorldData.CurrentState.EliminatedFactions.Contains(f.uuid))
                .ToList();
        }

        // ─── UI primitives ────────────────────────────────────────────────────────

        private static void AddText(GameObject panel, TMP_FontAsset font, string text, float size, Color color)
        {
            GameObject obj = new GameObject("DomText", typeof(RectTransform));
            obj.layer = panel.layer;
            obj.transform.SetParent(panel.transform, false);
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.richText = true;
            tmp.raycastTarget = false;
        }

        private static void AddResult(GameObject panel, TMP_FontAsset font)
        {
            if (string.IsNullOrEmpty(_lastResult)) return;
            AddText(panel, font, "────────────────────", 10, new Color(0.3f, 0.3f, 0.35f));
            AddText(panel, font, $"<i>{_lastResult}</i>", 12, new Color(0.85f, 0.82f, 0.65f));
        }

        private static GameObject AddRow(GameObject panel, float height = 26f)
        {
            GameObject row = new GameObject("DomRow", typeof(RectTransform));
            row.layer = panel.layer;
            row.transform.SetParent(panel.transform, false);
            var h = row.AddComponent<HorizontalLayoutGroup>();
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            h.spacing = 5;
            row.AddComponent<LayoutElement>().preferredHeight = height;
            return row;
        }

        private static void AddButton(GameObject row, TMP_FontAsset font, string label, Action onClick, Color? face = null)
        {
            GameObject obj = new GameObject("DomBtn", typeof(RectTransform));
            obj.layer = row.layer;
            obj.transform.SetParent(row.transform, false);
            var img = obj.AddComponent<Image>();
            img.color = face ?? BTN_FACE;
            var btn = obj.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            GameObject txtObj = new GameObject("Label", typeof(RectTransform));
            txtObj.layer = obj.layer;
            txtObj.transform.SetParent(obj.transform, false);
            var trt = (RectTransform)txtObj.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var lbl = txtObj.AddComponent<TextMeshProUGUI>();
            if (font != null) lbl.font = font;
            lbl.text = label;
            lbl.fontSize = 13;
            lbl.fontStyle = FontStyles.Bold;
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.enableWordWrapping = false;
            lbl.overflowMode = TextOverflowModes.Ellipsis;
            lbl.raycastTarget = false;
            lbl.color = new Color(0.9f, 0.9f, 0.95f);
        }
    }
}
