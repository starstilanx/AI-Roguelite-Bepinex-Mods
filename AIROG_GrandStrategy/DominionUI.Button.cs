using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_GrandStrategy
{
    // Toggle button (the "DOM" button beside the map's jump-to-location button) and the
    // capital marker pinned to the dominion's capital on the world map.
    public static partial class DominionUI
    {
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
                label.text = $"<color=#FFD34D>{GrandStrategyData.L.Icon} {s.DominionName}</color> <color=#FF9E9E>⚔{s.ArmyStrength}</color>";
            markerObj.transform.SetAsLastSibling();
        }
    }
}
