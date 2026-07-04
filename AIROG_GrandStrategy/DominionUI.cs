using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_WorldExpansion;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
        private const string MARKER_NAME = "DominionMarker_Mod";

        private static bool _panelOn;
        private static int _targetIdx;
        private static int _impIdx;
        private static int _wonderIdx;
        private static int _holdingIdx;
        private static int _advisorRoleIdx;
        private static string _lastResult = "";

        // Map-click targeting: a panel toggle arms pick mode, then the next left-click on a
        // MapLocation (intercepted via Harmony below) supplies a specific place instead of
        // ANNEX/CAMPAIGN's automatic nearest/adjacent pick.
        private enum PickMode { None, Annex, Campaign }
        private static PickMode _pickMode = PickMode.None;
        private static string _pickedAnnexUuid;
        private static string _pickedCampaignUuid;

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
                EnsureCapitalMarker(__instance);
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
            ClearCapitalMarker(__instance);
            _pickMode = PickMode.None;
        }

        [HarmonyPatch(typeof(MapModal), "ShowUniv")]
        [HarmonyPostfix]
        public static void Postfix_ShowUniv(MapModal __instance)
        {
            SetButtonVisible(__instance, false);
            ClearPanel(__instance);
            ClearCapitalMarker(__instance);
            _pickMode = PickMode.None;
        }

        [HarmonyPatch(typeof(MapModal), "HideMapModal")]
        [HarmonyPostfix]
        public static void Postfix_HideMapModal(MapModal __instance)
        {
            ClearPanel(__instance);
            ClearCapitalMarker(__instance);
            _pickMode = PickMode.None;
        }

        // A left-click on the world map, while a pick mode is armed from the panel, supplies
        // a specific place to ANNEX/CAMPAIGN instead of triggering the native travel/select
        // behavior. Consumed (returns false) only while a pick is actually pending.
        [HarmonyPatch(typeof(MapLocation), "OnPointerUp")]
        [HarmonyPrefix]
        public static bool Prefix_MapLocationPointerUp(MapLocation __instance, PointerEventData eventData)
        {
            if (_pickMode == PickMode.None) return true;
            if (eventData.button != PointerEventData.InputButton.Left) return true;

            Place place = __instance.GetPlace();
            if (place == null) return true;

            if (_pickMode == PickMode.Annex)     _pickedAnnexUuid = place.uuid;
            else if (_pickMode == PickMode.Campaign) _pickedCampaignUuid = place.uuid;

            var modal = __instance.mapModal;
            _lastResult = $"Target set: {place.GetPrettyName()}.";
            _pickMode = PickMode.None;
            if (modal != null) { Click(modal); BuildPanel(modal); }
            return false;
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

        // ─── Capital marker ───────────────────────────────────────────────────────
        // A small label pinned to the capital's map icon (gold dominion name + army
        // strength) so the dominion reads at a glance on the world map, independent of
        // whether WorldExpansion's own political lens is toggled on.

        private static void ClearCapitalMarker(MapModal modal)
        {
            Transform old = modal.mapLocationsParent?.Find(MARKER_NAME);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
        }

        private static void EnsureCapitalMarker(MapModal modal)
        {
            if (modal.mapLocationsParent == null) return;
            var s = GrandStrategyData.State;
            Transform old = modal.mapLocationsParent.Find(MARKER_NAME);

            if (!s.Founded || string.IsNullOrEmpty(s.CapitalPlaceUuid))
            {
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                return;
            }

            Vector2 pos;
            try
            {
                if (SS.I?.uuidToGameEntityMap == null
                    || !SS.I.uuidToGameEntityMap.TryGetValue(s.CapitalPlaceUuid, out var e) || !(e is Place capPlace))
                {
                    if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                    return;
                }
                pos = capPlace.worldCoords;
            }
            catch { return; }

            GameObject markerObj;
            TMP_Text label;
            if (old != null)
            {
                markerObj = old.gameObject;
                label = markerObj.GetComponentInChildren<TMP_Text>();
            }
            else
            {
                markerObj = new GameObject(MARKER_NAME, typeof(RectTransform));
                markerObj.layer = modal.mapLocationsParent.gameObject.layer;
                var rt = (RectTransform)markerObj.transform;
                rt.SetParent(modal.mapLocationsParent, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(240, 24);
                rt.localScale = Vector3.one;

                GameObject lblObj = new GameObject("Label", typeof(RectTransform));
                lblObj.layer = markerObj.layer;
                lblObj.transform.SetParent(markerObj.transform, false);
                var lrt = (RectTransform)lblObj.transform;
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = Vector2.zero;
                lrt.offsetMax = Vector2.zero;
                label = lblObj.AddComponent<TextMeshProUGUI>();
                if (modal.voronoiWorldTitle != null) label.font = modal.voronoiWorldTitle.font;
                label.fontSize = 13;
                label.fontStyle = FontStyles.Bold;
                label.alignment = TextAlignmentOptions.Bottom;
                label.raycastTarget = false;
                label.richText = true;
                label.enableWordWrapping = false;
            }

            var mrt = (RectTransform)markerObj.transform;
            mrt.localPosition = new Vector3(pos.x, pos.y + 34f, 0f); // sits just above the capital's icon
            if (label != null)
                label.text = $"<color=#FFD34D>♛ {s.DominionName}</color> <color=#FF9E9E>⚔{s.ArmyStrength}</color>";
            markerObj.transform.SetAsLastSibling();
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

            var worstHolding = s.Holdings.Values.OrderByDescending(h => h.Unrest).FirstOrDefault();
            if (worstHolding != null && worstHolding.Unrest >= 20)
                AddText(panel, font, $"<color=#FFAA55>⚠ Unrest brewing in {worstHolding.Name} ({worstHolding.Unrest})</color>", 13, Color.white);

            if (s.Advisors.Count > 0)
                AddText(panel, font,
                    $"Council: {string.Join(", ", s.Advisors.Select(a => $"{a.Name} ({a.Role.ToLower()})"))}",
                    12, MUTED);

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
            AddButton(r1, font, string.IsNullOrEmpty(_pickedAnnexUuid) ? "ANNEX 25g" : "ANNEX ▸ picked",
                () => DoOrder(modal, "ANNEX", "", _pickedAnnexUuid));
            AddButton(r1, font, _pickMode == PickMode.Annex ? "🎯 CLICK MAP…" : "🎯 pick",
                () =>
                {
                    _pickMode = _pickMode == PickMode.Annex ? PickMode.None : PickMode.Annex;
                    _lastResult = _pickMode == PickMode.Annex
                        ? "Click an unclaimed place on the map to target ANNEX." : "";
                    Click(modal);
                    BuildPanel(modal);
                }, new Color(0.20f, 0.16f, 0.28f, 0.95f));
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

            // ── Council ──
            var role = OrderSystem.AdvisorRoles[_advisorRoleIdx % OrderSystem.AdvisorRoles.Length];
            var r6 = AddRow(panel);
            AddButton(r6, font, $"{role} ▸", () => { _advisorRoleIdx++; Click(modal); BuildPanel(modal); },
                new Color(0.12f, 0.20f, 0.16f, 0.95f));
            AddButton(r6, font, "COUNCIL 40g", () => DoOrder(modal, "COUNCIL", role));

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
            var d1b = AddRow(panel);
            AddButton(d1b, font, "PACT 15g",       () => DoOrder(modal, "PACT", tName));
            AddButton(d1b, font, "TRADE_DEAL 20g", () => DoOrder(modal, "TRADE_DEAL", tName));
            var d2 = AddRow(panel);
            AddButton(d2, font, "WAR 2CP",      () => DoOrder(modal, "WAR", tName), BTN_WARM);
            AddButton(d2, font, string.IsNullOrEmpty(_pickedCampaignUuid) ? "CAMPAIGN 2CP" : "CAMPAIGN ▸ picked",
                () => DoOrder(modal, "CAMPAIGN", tName, _pickedCampaignUuid), BTN_WARM);
            AddButton(d2, font, _pickMode == PickMode.Campaign ? "🎯 CLICK MAP…" : "🎯 pick",
                () =>
                {
                    _pickMode = _pickMode == PickMode.Campaign ? PickMode.None : PickMode.Campaign;
                    _lastResult = _pickMode == PickMode.Campaign
                        ? "Click an enemy-held place on the map to target CAMPAIGN." : "";
                    Click(modal);
                    BuildPanel(modal);
                }, new Color(0.20f, 0.16f, 0.28f, 0.95f));
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

        private static void DoOrder(MapModal modal, string type, string arg, string placeUuid = null)
        {
            if (string.IsNullOrEmpty(arg) && RequiresTarget(type))
                _lastResult = "No target faction available.";
            else
                _lastResult = OrderSystem.Issue(modal.manager, type, arg, placeUuid);

            // Map-click picks are single-use regardless of outcome — a failed order just
            // means the player re-picks (or lets ANNEX/CAMPAIGN fall back to automatic).
            if (type == "ANNEX")    _pickedAnnexUuid = null;
            if (type == "CAMPAIGN") _pickedCampaignUuid = null;

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
                case "PACT": case "TRADE_DEAL":
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
