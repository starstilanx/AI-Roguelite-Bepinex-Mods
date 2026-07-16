using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace AIROG_Settlement
{
    // Injects "Establish Settlement" / "View Settlement" buttons into the map location
    // details panel, stacked above the native Enter Location button. Split out of
    // SettlementPlugin.cs.
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
                    rect.pivot = enterRect.pivot;
                    // Stack ABOVE the Enter Location button — positioning it to the left
                    // pushed it outside the details panel where it was clipped to a sliver.
                    rect.anchoredPosition = enterRect.anchoredPosition + new Vector2(0, enterRect.sizeDelta.y + 8f);
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
                    vRect.pivot = enterRect.pivot;
                    // Mutually exclusive with establish button — same stacked-above position
                    vRect.anchoredPosition = enterRect.anchoredPosition + new Vector2(0, enterRect.sizeDelta.y + 8f);
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
