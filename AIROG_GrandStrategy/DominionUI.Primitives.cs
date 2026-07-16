using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_GrandStrategy
{
    // Generic procedural-UI building blocks (text, row, button) shared by Populate().
    public static partial class DominionUI
    {
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
