using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_ALife
{
    /// <summary>
    /// v2.2 "Rumors" journal tab — the player-facing window into A-Life, deliberately
    /// genre-neutral (no "PDA": rumors work in fantasy, sci-fi, and modern alike).
    /// Shows only fog-of-warred knowledge: whispers (known events), known bands with
    /// intel age, killing grounds, and war fronts.
    ///
    /// Injection follows WorldEventsUI's 07/11-build lessons exactly: re-wire the tab
    /// button on EVERY JournalModal.Init (native Init steals it), overlay our own TMP
    /// label (buttons are icon-only now), no tab-count assumptions, SoundManager.I
    /// singleton guarded so cosmetics can't kill the listener.
    /// </summary>
    public static class ALifeRumorsUI
    {
        private const string VIEW_NAME = "ALifeRumorsTabView_Mod";
        private const string BTN_NAME = "ALifeRumorsTabButton";

        private static GameObject _contentObj;
        private static TMP_FontAsset _commonFont;

        [HarmonyPatch(typeof(JournalModal), "Init")]
        [HarmonyPostfix]
        public static void Postfix_JournalModal_Init(JournalModal __instance)
        {
            try { InjectIntoJournalModal(__instance); }
            catch (Exception ex) { Debug.LogError("[ALife] Rumors tab injection failed: " + ex); }
        }

        [HarmonyPatch(typeof(JournalModal), "UnsetTabTransesAndBtns")]
        [HarmonyPostfix]
        public static void Postfix_UnsetTabTransesAndBtns(JournalModal __instance)
        {
            try
            {
                Transform tabBtn = __instance.tabBtnsHolder.Find(BTN_NAME);
                if (tabBtn != null)
                {
                    var img = tabBtn.GetComponentInChildren<Image>();
                    if (img != null) img.color = Utils.GetColorFromStr(JournalModal.UNSELECTED_TAB_COLOR_STR);
                }
                Transform view = __instance.tabTransesHolder.Find(VIEW_NAME);
                if (view != null) view.gameObject.SetActive(false);
            }
            catch { }
        }

        public static void InjectIntoJournalModal(JournalModal modal)
        {
            if (!ALifePlugin.CfgRumorsTab.Value) return;
            if (modal == null || modal.tabBtnsHolder == null || modal.tabTransesHolder == null) return;

            Transform view = modal.tabTransesHolder.Find(VIEW_NAME);
            if (view == null)
            {
                GameObject viewObj = new GameObject(VIEW_NAME, typeof(RectTransform));
                viewObj.transform.SetParent(modal.tabTransesHolder, false);
                viewObj.layer = modal.gameObject.layer;

                RectTransform rt = viewObj.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                viewObj.AddComponent<Image>().color = new Color(0.09f, 0.1f, 0.09f, 0.95f);

                var scrollViewObj = new GameObject("Scroll View", typeof(RectTransform));
                scrollViewObj.transform.SetParent(viewObj.transform, false);
                scrollViewObj.layer = viewObj.layer;
                var scrollRect = scrollViewObj.AddComponent<ScrollRect>();

                var scrollRectT = scrollViewObj.GetComponent<RectTransform>();
                scrollRectT.anchorMin = Vector2.zero;
                scrollRectT.anchorMax = Vector2.one;
                scrollRectT.offsetMin = new Vector2(24, 24);
                scrollRectT.offsetMax = new Vector2(-24, -24);

                // RectMask2D, never Mask+transparent Image (cullTransparentMesh gotcha)
                var viewportObj = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
                viewportObj.transform.SetParent(scrollViewObj.transform, false);
                viewportObj.layer = viewObj.layer;
                var viewportRect = viewportObj.GetComponent<RectTransform>();
                viewportRect.anchorMin = Vector2.zero;
                viewportRect.anchorMax = Vector2.one;
                viewportRect.offsetMin = Vector2.zero;
                viewportRect.offsetMax = Vector2.zero;

                GameObject contentObj = new GameObject("Content", typeof(RectTransform));
                contentObj.transform.SetParent(viewportObj.transform, false);
                contentObj.layer = viewObj.layer;
                var contentRect = contentObj.GetComponent<RectTransform>();
                contentRect.anchorMin = new Vector2(0, 1);
                contentRect.anchorMax = new Vector2(1, 1);
                contentRect.pivot = new Vector2(0.5f, 1);
                contentRect.sizeDelta = new Vector2(0, 500);

                var vlg = contentObj.AddComponent<VerticalLayoutGroup>();
                vlg.childControlHeight = true;
                vlg.childControlWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.childForceExpandWidth = true;
                vlg.spacing = 8;
                vlg.padding = new RectOffset(10, 10, 10, 10);

                var csf = contentObj.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                scrollRect.content = contentRect;
                scrollRect.viewport = viewportRect;
                scrollRect.vertical = true;
                scrollRect.horizontal = false;
                scrollRect.scrollSensitivity = 25f;

                _contentObj = contentObj;
                viewObj.SetActive(false);
            }
            else
            {
                _contentObj = view.Find("Scroll View/Viewport/Content")?.gameObject;
            }

            // Re-wire our tab button on EVERY Init — the native Init steals it otherwise.
            Transform btnTrans = modal.tabBtnsHolder.Find(BTN_NAME);
            GameObject btnObj;
            if (btnTrans == null)
            {
                Transform refBtn = modal.tabBtnsHolder.GetChild(0);
                btnObj = UnityEngine.Object.Instantiate(refBtn.gameObject, modal.tabBtnsHolder);
                btnObj.name = BTN_NAME;
            }
            else
            {
                btnObj = btnTrans.gameObject;
            }
            SetupTabButton(btnObj.GetComponent<Button>(), modal);
        }

        private static void SetupTabButton(Button btn, JournalModal modal)
        {
            var loc = btn.GetComponentInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>();
            if (loc != null) UnityEngine.Object.DestroyImmediate(loc);

            var btnText = btn.GetComponentInChildren<TMP_Text>(true);
            if (btnText != null)
            {
                btnText.text = "Rumors";
                _commonFont = btnText.font;
            }
            else
            {
                if (modal.currentQuestDetailsTitle != null)
                    _commonFont = modal.currentQuestDetailsTitle.font;
                if (btn.transform.Find("ALifeRumorsLabel_Mod") == null)
                {
                    var labelObj = new GameObject("ALifeRumorsLabel_Mod", typeof(RectTransform));
                    labelObj.transform.SetParent(btn.transform, false);
                    labelObj.layer = btn.gameObject.layer;
                    var lrt = (RectTransform)labelObj.transform;
                    lrt.anchorMin = Vector2.zero;
                    lrt.anchorMax = Vector2.one;
                    lrt.offsetMin = Vector2.zero;
                    lrt.offsetMax = Vector2.zero;
                    var lbl = labelObj.AddComponent<TextMeshProUGUI>();
                    lbl.text = "RUMOR";
                    lbl.fontSize = 16;
                    lbl.fontStyle = FontStyles.Bold;
                    lbl.alignment = TextAlignmentOptions.Center;
                    lbl.color = new Color(0.2f, 0.12f, 0.05f);
                    lbl.raycastTarget = false;
                    if (_commonFont != null) lbl.font = _commonFont;
                }
            }

            btn.onClick = new Button.ButtonClickedEvent(); // drop cloned persistent listeners
            btn.onClick.AddListener(() =>
            {
                try { SoundManager.I.smallClickSoundFxObj.PlayNextSound(); } catch { }
                modal.UnsetTabTransesAndBtns();
                var img = btn.GetComponentInChildren<Image>();
                if (img != null) img.color = Utils.GetColorFromStr(JournalModal.SELECTED_TAB_COLOR_STR);
                Transform v = modal.tabTransesHolder.Find(VIEW_NAME);
                if (v != null)
                {
                    v.gameObject.SetActive(true);
                    try { RefreshView(); }
                    catch (Exception ex) { Debug.LogError("[ALife] Rumors refresh failed: " + ex); }
                }
            });
        }

        // ─── View content ─────────────────────────────────────────────────────────

        private static void RefreshView()
        {
            if (_contentObj == null) return;
            for (int i = _contentObj.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_contentObj.transform.GetChild(i).gameObject);

            var state = ALifeData.State;

            // ── Header: who you are to the wandering world ────────────────────────
            string tier = ALifeLegend.LegendTier();
            Text($"<b>Word on the Roads</b>   —   turn {state.CurrentTurn}", 26, Color.white);
            Text(tier != null
                ? $"Among the wandering bands you are <b>{tier}</b>."
                : "The wandering bands do not yet speak your name.", 19, new Color(0.85f, 0.8f, 0.65f));
            Separator();

            // ── War fronts ────────────────────────────────────────────────────────
            var wars = ALifeWorldBridge.GetActiveWars();
            if (wars.Count > 0)
            {
                Text("<b>⚔ Fronts</b>", 22, new Color(1f, 0.45f, 0.45f));
                foreach (var w in wars)
                {
                    string front = ALifeWar.FrontLine(w);
                    Text($"{w.ActorName} vs {w.TargetName} — {front ?? "the front is unbroken"}",
                        18, new Color(0.95f, 0.75f, 0.7f));
                }
                Separator();
            }

            // ── Known bands ───────────────────────────────────────────────────────
            var known = state.Knowledge.Values
                .OrderByDescending(k => k.LastKnownTurn).Take(15).ToList();
            Text($"<b>Known Bands</b> ({known.Count})", 22, new Color(0.8f, 0.9f, 1f));
            if (known.Count == 0)
                Text("None yet — travel, listen, survive.", 18, new Color(0.6f, 0.6f, 0.65f));
            foreach (var k in known)
            {
                var live = ALifeKnowledge.LiveSquad(k);
                bool stale = ALifeKnowledge.IsStale(k);
                int ago = Math.Max(0, state.CurrentTurn - k.LastKnownTurn);

                string headline = $"<b>{ALifeSimulation.Cap(k.KnownName)}</b>";
                if (k.KnownLeaderName != null) headline += $" — led by {k.KnownLeaderName}";
                Text(headline, 19, k.Met ? new Color(1f, 0.9f, 0.6f) : new Color(0.85f, 0.85f, 0.9f));

                string detail = $"   last {(k.Met ? "seen" : "rumored")} at {k.LastKnownPlaceName}, " +
                                $"{(ago == 0 ? "this turn" : ago + " turn" + (ago == 1 ? "" : "s") + " ago")}" +
                                $" · ~{k.LastKnownSize} strong · {k.LastKnownActivity}";
                if (live == null) detail += "   <i>(fate unknown)</i>";
                Text(detail, 16, stale || live == null
                    ? new Color(0.55f, 0.55f, 0.6f)
                    : new Color(0.75f, 0.78f, 0.8f));

                // Face-to-face intel: regard and feuds
                if (k.Met && live != null)
                {
                    var notes = new List<string>();
                    if (live.FearOfPlayer >= ALifeLegend.FEAR_FLEE) notes.Add("they will flee from you");
                    else if (live.FearOfPlayer >= ALifeLegend.FEAR_WARY) notes.Add("they fear you");
                    if (live.AweOfPlayer >= 40) notes.Add("they respect you");
                    foreach (var f in live.Feuds.Where(f => f.Heat >= 30))
                        notes.Add($"blood feud with {f.EnemySquadName}");
                    if (notes.Count > 0)
                        Text("   " + string.Join(" · ", notes), 15, new Color(0.9f, 0.65f, 0.5f));
                }
            }
            Separator();

            // ── Killing grounds ───────────────────────────────────────────────────
            if (state.DreadMap.Count > 0)
            {
                Text("<b>☠ Killing Grounds</b>", 22, new Color(1f, 0.5f, 0.4f));
                foreach (var kv in state.DreadMap.OrderByDescending(d => d.Value).Take(6))
                {
                    string name = state.DreadNames.TryGetValue(kv.Key, out var n) ? n : "an unnamed place";
                    Text($"{name} — travelers avoid it out of dread (fading in {kv.Value * 4} turns)",
                        17, new Color(0.85f, 0.6f, 0.55f));
                }
                Separator();
            }

            // ── Whispers: the rumor feed ──────────────────────────────────────────
            var events = ALifeKnowledge.KnownEvents(25);
            Text("<b>Whispers</b>", 22, new Color(0.75f, 0.85f, 0.75f));
            if (events.Count == 0)
                Text("Nothing worth repeating yet.", 18, new Color(0.6f, 0.6f, 0.65f));
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                int ago = Math.Max(0, state.CurrentTurn - e.Turn);
                Text($"<color=#888888>T{e.Turn} · {e.PlaceName}</color>  {e.Description}",
                    16, EventColor(e.Type) * (ago > 25 ? 0.65f : 1f) + new Color(0, 0, 0, 1f));
            }
        }

        private static Color EventColor(string type)
        {
            switch (type)
            {
                case "BATTLE":
                case "WIPE": return new Color(1f, 0.55f, 0.5f);
                case "WAR": return new Color(1f, 0.4f, 0.4f);
                case "RAID": return new Color(1f, 0.7f, 0.45f);
                case "FEUD": return new Color(0.95f, 0.6f, 0.75f);
                case "LEGEND": return new Color(1f, 0.9f, 0.55f);
                case "ENCOUNTER": return new Color(0.75f, 0.9f, 1f);
                case "LIFECYCLE": return new Color(0.75f, 0.85f, 0.75f);
                case "MIGRATION": return new Color(0.7f, 0.8f, 0.9f);
                default: return new Color(0.8f, 0.8f, 0.82f);
            }
        }

        private static void Text(string text, float fontSize, Color color)
        {
            GameObject textObj = new GameObject("RumorText", typeof(RectTransform));
            textObj.transform.SetParent(_contentObj.transform, false);
            textObj.layer = _contentObj.layer;
            var txt = textObj.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = fontSize;
            txt.color = color;
            txt.enableWordWrapping = true;
            txt.alignment = TextAlignmentOptions.TopLeft;
            if (_commonFont != null) txt.font = _commonFont;
        }

        private static void Separator()
        {
            Text("──────────────────────────────", 16, new Color(0.3f, 0.32f, 0.3f));
        }
    }
}
