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
    /// v2.2 "Tracks" map lens: a toggleable overlay on the world map showing the
    /// player's fog-of-warred intel — last-known band positions (with intel age),
    /// recent battle sites, and killing grounds. Built on the StrategicMapUI lessons:
    /// overlay pinned to mapLocationsParent's PIVOT (the space MapLocation icons use),
    /// rebuilt in the ShowWorldView postfix (map redraws destroy it), toggle button
    /// cloned from jumpToCurrentLocationButton with a fresh ButtonClickedEvent and the
    /// ButtonPressEffect removed (it caches child Graphics we delete).
    /// </summary>
    public static class ALifeTracksLens
    {
        private const string OVERLAY_NAME = "ALifeTracksOverlay_Mod";
        private const string BUTTON_NAME = "ALifeTracksLensButton_Mod";

        private static bool _lensOn;
        public static bool LensRequested; // set by the ALIFE_MAP console command

        // ─── MapModal hooks ───────────────────────────────────────────────────────

        [HarmonyPatch(typeof(MapModal), "ShowWorldView")]
        [HarmonyPostfix]
        public static void Postfix_ShowWorldView(MapModal __instance, VoronoiWorld vw)
        {
            try
            {
                if (!ALifePlugin.CfgTracksLens.Value) return;
                if (LensRequested) { _lensOn = true; LensRequested = false; }
                EnsureLensButton(__instance);
                if (_lensOn) BuildOverlay(__instance);
                else ClearOverlay(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[ALife] Tracks lens failed: " + e);
            }
        }

        [HarmonyPatch(typeof(MapModal), "ShowDetachedView")]
        [HarmonyPostfix]
        public static void Postfix_ShowDetachedView(MapModal __instance)
        {
            SetButtonVisible(__instance, false);
        }

        [HarmonyPatch(typeof(MapModal), "ShowUniv")]
        [HarmonyPostfix]
        public static void Postfix_ShowUniv(MapModal __instance)
        {
            SetButtonVisible(__instance, false);
        }

        // ─── Toggle button ────────────────────────────────────────────────────────

        private static void EnsureLensButton(MapModal modal)
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

            // ButtonPressEffect caches child Graphics in Awake; we delete those below.
            var pressFx = btnObj.GetComponent<ButtonPressEffect>();
            if (pressFx != null) UnityEngine.Object.DestroyImmediate(pressFx);

            // Step LEFT out of the vertical button column — one slot further out for
            // every other mod lens button already there (e.g. WorldExpansion's POL).
            int lensButtons = 0;
            foreach (Transform sib in parent)
                if (sib.name.EndsWith("LensButton_Mod") && sib.name != BUTTON_NAME) lensButtons++;
            RectTransform rt = (RectTransform)btnObj.transform;
            RectTransform srcRt = (RectTransform)modal.jumpToCurrentLocationButton.transform;
            rt.anchoredPosition = srcRt.anchoredPosition
                + new Vector2(-(srcRt.rect.width + 12f) * (lensButtons + 1), 0f);

            foreach (var img in btnObj.GetComponentsInChildren<Image>(true))
                if (img.gameObject != btnObj) UnityEngine.Object.DestroyImmediate(img.gameObject);
            foreach (var raw in btnObj.GetComponentsInChildren<RawImage>(true))
                if (raw != null && raw.gameObject != btnObj) UnityEngine.Object.DestroyImmediate(raw.gameObject);
            foreach (var t in btnObj.GetComponentsInChildren<TMP_Text>(true))
                if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);

            GameObject txtObj = new GameObject("TrkLabel", typeof(RectTransform));
            txtObj.layer = btnObj.layer;
            txtObj.transform.SetParent(btnObj.transform, false);
            var trt = (RectTransform)txtObj.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var lbl = txtObj.AddComponent<TextMeshProUGUI>();
            if (modal.voronoiWorldTitle != null) lbl.font = modal.voronoiWorldTitle.font;
            lbl.text = "TRK";
            lbl.fontSize = 15;
            lbl.fontStyle = FontStyles.Bold;
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.raycastTarget = false;

            Button btn = btnObj.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent(); // drop cloned persistent listeners
            btn.onClick.AddListener(() => OnLensToggled(modal));

            UpdateButtonLabel(btnObj);
        }

        private static void UpdateButtonLabel(GameObject btnObj)
        {
            var txt = btnObj.GetComponentInChildren<TMP_Text>();
            if (txt != null) txt.color = _lensOn ? new Color(0.55f, 1f, 0.6f) : new Color(0.78f, 0.78f, 0.83f);
            var frame = btnObj.GetComponent<Image>();
            if (frame != null) frame.color = _lensOn ? new Color(0.75f, 1f, 0.8f) : Color.white;
        }

        private static void OnLensToggled(MapModal modal)
        {
            _lensOn = !_lensOn;
            try { SoundManager.I.smallClickSoundFxObj.PlayNextSound(); } catch { }

            Transform btn = modal.jumpToCurrentLocationButton?.transform.parent.Find(BUTTON_NAME);
            if (btn != null) UpdateButtonLabel(btn.gameObject);

            if (_lensOn) BuildOverlay(modal);
            else ClearOverlay(modal);
        }

        private static void SetButtonVisible(MapModal modal, bool visible)
        {
            Transform btn = modal.jumpToCurrentLocationButton?.transform.parent.Find(BUTTON_NAME);
            if (btn != null) btn.gameObject.SetActive(visible);
        }

        // ─── Overlay ──────────────────────────────────────────────────────────────

        private static void ClearOverlay(MapModal modal)
        {
            Transform old = modal.mapLocationsParent?.Find(OVERLAY_NAME);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
        }

        private static void BuildOverlay(MapModal modal)
        {
            ClearOverlay(modal);
            if (modal.mapLocationsParent == null) return;

            var state = ALifeData.State;

            GameObject root = new GameObject(OVERLAY_NAME, typeof(RectTransform));
            root.layer = modal.mapLocationsParent.gameObject.layer;
            PinToParentPivot(root, modal.mapLocationsParent);

            TMP_FontAsset font = modal.voronoiWorldTitle != null ? modal.voronoiWorldTitle.font : null;

            // Recent known battle sites (the fields the whispers speak of)
            var battleSites = ALifeKnowledge.KnownEvents(60)
                .Where(e => (e.Type == "BATTLE" || e.Type == "WIPE") && e.Turn >= state.CurrentTurn - 25)
                .GroupBy(e => e.PlaceUuid)
                .Select(g => g.Last()).ToList();
            foreach (var e in battleSites)
            {
                Place pl = ALifeGraph.PlaceByUuid(e.PlaceUuid);
                if (pl == null) continue;
                AddMarker(root, font, pl.worldCoords + new Vector2(0f, -26f), "⚔",
                    new Color(1f, 0.35f, 0.3f, 0.9f), 30, null, default);
            }

            // Killing grounds (dread zones)
            foreach (var kv in state.DreadMap)
            {
                Place pl = ALifeGraph.PlaceByUuid(kv.Key);
                if (pl == null) continue;
                AddMarker(root, font, pl.worldCoords + new Vector2(0f, 26f), "☠",
                    new Color(0.85f, 0.2f, 0.2f, 0.85f), 32, null, default);
            }

            // Last-known band positions, stacked when several share a place
            var byPlace = state.Knowledge.Values
                .Where(k => k.LastKnownPlaceUuid != null)
                .GroupBy(k => k.LastKnownPlaceUuid);
            foreach (var grp in byPlace)
            {
                Place pl = ALifeGraph.PlaceByUuid(grp.Key);
                if (pl == null) continue;
                int slot = 0;
                foreach (var k in grp.OrderByDescending(x => x.LastKnownTurn))
                {
                    bool stale = ALifeKnowledge.IsStale(k);
                    bool gone = ALifeKnowledge.LiveSquad(k) == null;
                    float alpha = gone ? 0.35f : stale ? 0.55f : 0.95f;
                    Color c = k.Met ? new Color(1f, 0.85f, 0.4f, alpha) : new Color(0.8f, 0.9f, 1f, alpha);

                    int ago = Math.Max(0, state.CurrentTurn - k.LastKnownTurn);
                    string caption = ALifeSimulation.Cap(k.KnownName)
                        + (gone ? "†" : stale ? $"? ({ago}t)" : "");

                    Vector2 offset = new Vector2(34f, 18f - slot * 22f);
                    AddMarker(root, font, pl.worldCoords + offset, Glyph(k.Archetype), c, 26, caption, c);
                    slot++;
                    if (slot >= 3) break; // don't bury the place icon
                }
            }

            // Keep place icons clickable above our markers
            foreach (var loc in modal.mapLocationsParent.GetComponentsInChildren<MapLocation>())
                loc.transform.SetAsLastSibling();
        }

        private static string Glyph(string archetype)
        {
            switch (archetype)
            {
                case SquadArchetype.HUNTERS: return "☠";
                case SquadArchetype.CARAVAN: return "💰";
                case SquadArchetype.RAIDERS: return "🗡";
                case SquadArchetype.PILGRIMS: return "·";
                default: return "⚔"; // patrols & warbands
            }
        }

        private static void AddMarker(GameObject root, TMP_FontAsset font, Vector2 pos,
            string glyph, Color color, float size, string caption, Color captionColor)
        {
            GameObject obj = new GameObject("Track", typeof(RectTransform));
            obj.layer = root.layer;
            PinToParentPivot(obj, root.transform);
            var rt = (RectTransform)obj.transform;
            if (caption != null) rt.pivot = new Vector2(0f, 0.5f); // caption flows rightward from the point
            rt.localPosition = pos;
            rt.sizeDelta = new Vector2(600, 40);
            var txt = obj.AddComponent<TextMeshProUGUI>();
            if (font != null) txt.font = font;
            txt.text = caption == null ? glyph : $"{glyph} <size=60%>{caption}</size>";
            txt.fontSize = size;
            txt.color = color;
            txt.alignment = caption == null ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft;
            txt.raycastTarget = false;
            txt.enableWordWrapping = false;
        }

        // Same reference point MapLocation icons use (localPosition == worldCoords).
        private static void PinToParentPivot(GameObject obj, Transform parent)
        {
            var rt = (RectTransform)obj.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.localPosition = Vector3.zero;
        }
    }
}
