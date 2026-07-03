using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Read-only summary of the attribute bonuses the Skill Web layer is contributing on top of
    /// the native perk tree. The native ViewPerksModal handles learning/activation; this only
    /// shows the derived mechanical effect of the player's learned perks.
    /// </summary>
    public class SkillWebUI : MonoBehaviour
    {
        public static SkillWebUI Instance { get; private set; }

        private GameplayManager _manager;
        private SkillWebData _data;
        private GameObject _overlay;
        private RectTransform _listContent;
        private TextMeshProUGUI _totalsText;

        private static readonly SS.PlayerAttribute[] AttrOrder =
        {
            SS.PlayerAttribute.Strength, SS.PlayerAttribute.Dexterity, SS.PlayerAttribute.Intellect,
            SS.PlayerAttribute.Cunning, SS.PlayerAttribute.Charisma
        };

        public static void Open(GameplayManager manager, SkillWebData data)
        {
            if (Instance == null)
            {
                var go = new GameObject("SkillWebSummaryUI");
                Instance = go.AddComponent<SkillWebUI>();
            }
            Instance._manager = manager;
            Instance._data = data;
            Instance.Build();
            Instance.Refresh();
        }

        public static void Close()
        {
            if (Instance != null && Instance._overlay != null)
                Instance._overlay.SetActive(false);
        }

        private void Update()
        {
            if (_overlay != null && _overlay.activeSelf && Input.GetKeyDown(KeyCode.Escape))
                Close();
        }

        // ── Construction ──────────────────────────────────────────────────────────

        private void Build()
        {
            if (_overlay != null) { _overlay.SetActive(true); return; }

            Canvas rootCanvas = FindRootCanvas();
            if (rootCanvas == null) { Debug.LogError("[SkillWeb] No canvas found for summary UI."); return; }

            // Dim full-screen backdrop (click to close).
            _overlay = NewUI("SkillWebOverlay", rootCanvas.transform);
            var ovRect = _overlay.GetComponent<RectTransform>();
            Stretch(ovRect);
            var ovImg = _overlay.AddComponent<Image>();
            ovImg.color = new Color(0f, 0f, 0f, 0.6f);
            var ovBtn = _overlay.AddComponent<Button>();
            ovBtn.transition = Selectable.Transition.None;
            ovBtn.onClick.AddListener(Close);
            _overlay.transform.SetAsLastSibling();

            // Centered panel.
            var panel = NewUI("Panel", _overlay.transform);
            var pRect = panel.GetComponent<RectTransform>();
            pRect.anchorMin = pRect.anchorMax = pRect.pivot = new Vector2(0.5f, 0.5f);
            pRect.sizeDelta = new Vector2(560f, 640f);
            var pImg = panel.AddComponent<Image>();
            pImg.color = new Color(0.10f, 0.08f, 0.06f, 0.98f);
            // Swallow clicks so they don't fall through to the backdrop's close handler.
            panel.AddComponent<Button>().transition = Selectable.Transition.None;

            // Title.
            var title = NewText("Title", panel.transform, "✦ Skill Web Bonuses", 24, FontStyles.Bold);
            var tRect = title.GetComponent<RectTransform>();
            tRect.anchorMin = new Vector2(0f, 1f); tRect.anchorMax = new Vector2(1f, 1f); tRect.pivot = new Vector2(0.5f, 1f);
            tRect.anchoredPosition = new Vector2(0f, -16f); tRect.sizeDelta = new Vector2(-32f, 36f);
            title.alignment = TextAlignmentOptions.Center;

            // Subtitle.
            var sub = NewText("Subtitle", panel.transform,
                "Attribute bonuses from your learned perks. Learn & activate perks in the Perks panel.",
                13, FontStyles.Italic);
            var sRect = sub.GetComponent<RectTransform>();
            sRect.anchorMin = new Vector2(0f, 1f); sRect.anchorMax = new Vector2(1f, 1f); sRect.pivot = new Vector2(0.5f, 1f);
            sRect.anchoredPosition = new Vector2(0f, -54f); sRect.sizeDelta = new Vector2(-40f, 34f);
            sub.alignment = TextAlignmentOptions.Center;
            sub.color = new Color(0.75f, 0.7f, 0.6f);

            // Totals box.
            _totalsText = NewText("Totals", panel.transform, "", 16, FontStyles.Bold);
            var totRect = _totalsText.GetComponent<RectTransform>();
            totRect.anchorMin = new Vector2(0f, 1f); totRect.anchorMax = new Vector2(1f, 1f); totRect.pivot = new Vector2(0.5f, 1f);
            totRect.anchoredPosition = new Vector2(0f, -96f); totRect.sizeDelta = new Vector2(-40f, 30f);
            _totalsText.alignment = TextAlignmentOptions.Center;

            // Scrollable per-perk list.
            var scrollGO = NewUI("Scroll", panel.transform);
            var scRect = scrollGO.GetComponent<RectTransform>();
            scRect.anchorMin = new Vector2(0f, 0f); scRect.anchorMax = new Vector2(1f, 1f); scRect.pivot = new Vector2(0.5f, 0.5f);
            scRect.offsetMin = new Vector2(16f, 56f);   // leave room for close button
            scRect.offsetMax = new Vector2(-16f, -132f);
            var scImg = scrollGO.AddComponent<Image>();
            scImg.color = new Color(0f, 0f, 0f, 0.25f);
            var scroll = scrollGO.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            var mask = scrollGO.AddComponent<RectMask2D>();

            var content = NewUI("Content", scrollGO.transform);
            _listContent = content.GetComponent<RectTransform>();
            _listContent.anchorMin = new Vector2(0f, 1f); _listContent.anchorMax = new Vector2(1f, 1f); _listContent.pivot = new Vector2(0.5f, 1f);
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.spacing = 4f; vlg.padding = new RectOffset(8, 8, 8, 8);
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _listContent;

            // Close button.
            var closeGO = NewUI("Close", panel.transform);
            var cRect = closeGO.GetComponent<RectTransform>();
            cRect.anchorMin = new Vector2(0.5f, 0f); cRect.anchorMax = new Vector2(0.5f, 0f); cRect.pivot = new Vector2(0.5f, 0f);
            cRect.anchoredPosition = new Vector2(0f, 14f); cRect.sizeDelta = new Vector2(140f, 32f);
            closeGO.AddComponent<Image>().color = new Color(0.3f, 0.15f, 0.08f);
            var cBtn = closeGO.AddComponent<Button>();
            cBtn.onClick.AddListener(Close);
            var cTxt = NewText("Txt", closeGO.transform, "Close", 15, FontStyles.Normal);
            Stretch(cTxt.GetComponent<RectTransform>());
            cTxt.alignment = TextAlignmentOptions.Center;
        }

        // ── Population ────────────────────────────────────────────────────────────

        private void Refresh()
        {
            if (_listContent == null || _data == null) return;

            foreach (Transform child in _listContent) Destroy(child.gameObject);

            // Totals line from the cached, applied bonuses.
            var sb = new StringBuilder();
            bool any = false;
            foreach (var attr in AttrOrder)
            {
                if (_data.CachedStats != null && _data.CachedStats.TryGetValue(attr, out float v) && Mathf.Abs(v) > 0.01f)
                {
                    if (any) sb.Append("    ");
                    sb.Append($"{Abbrev(attr)} {Signed(v)}");
                    any = true;
                }
            }
            _totalsText.text = any ? "Applied: " + sb : "No attribute bonuses yet.";

            // Per-perk rows for the current actor's learned perks.
            var actor = _manager?.playerCharacter?.GetCurrentActor();
            var trees = actor?.playableData?.perkTrees;
            int rows = 0;
            if (trees != null)
            {
                foreach (var pt in trees)
                {
                    if (pt?.rootPerkNode == null) continue;
                    foreach (var pn in pt.GetAllPerkNodes())
                    {
                        if (pn == null || !pn.isLearned) continue;
                        if (!_data.perkBonuses.TryGetValue(pn.uuid, out PerkBonus pb)) continue;

                        string statStr = (pb.stats == null || pb.stats.Count == 0)
                            ? "<color=#888888>narrative only</color>"
                            : string.Join("  ", pb.stats.Select(kv => $"{Abbrev(kv.Key)} {Signed(kv.Value)}"));
                        string activeTag = pn.isActivated ? "  <color=#79E84A>[active]</color>" : "";
                        AddRow($"<b>{pn.GetPrettyName()}</b>{activeTag}\n<size=12>{statStr}</size>");
                        rows++;
                    }
                }
            }

            if (rows == 0)
                AddRow("<i>No perks learned yet. Open the Perks panel to learn perks — their attribute bonuses will appear here.</i>");
        }

        private void AddRow(string text)
        {
            var row = NewUI("Row", _listContent);
            row.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 44f;
            var t = NewText("Txt", row.transform, text, 14, FontStyles.Normal);
            var tr = t.GetComponent<RectTransform>();
            Stretch(tr);
            tr.offsetMin = new Vector2(8f, 4f); tr.offsetMax = new Vector2(-8f, -4f);
            t.alignment = TextAlignmentOptions.Left;
            t.enableWordWrapping = true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string Abbrev(SS.PlayerAttribute a)
        {
            switch (a)
            {
                case SS.PlayerAttribute.Strength:  return "STR";
                case SS.PlayerAttribute.Dexterity: return "DEX";
                case SS.PlayerAttribute.Intellect: return "INT";
                case SS.PlayerAttribute.Cunning:   return "CUN";
                case SS.PlayerAttribute.Charisma:  return "CHA";
                default: return a.ToString();
            }
        }

        private static string Abbrev(string attrName)
        {
            if (System.Enum.TryParse(attrName, true, out SS.PlayerAttribute a)) return Abbrev(a);
            return attrName;
        }

        private static string Signed(float v)
        {
            int i = Mathf.RoundToInt(v);
            return (i >= 0 ? "+" : "") + i;
        }

        private static Canvas FindRootCanvas()
        {
            Canvas best = null;
            foreach (var c in Object.FindObjectsOfType<Canvas>())
            {
                if (!c.isActiveAndEnabled) continue;
                if (best == null || c.sortingOrder >= best.sortingOrder) best = c;
            }
            return best;
        }

        private static GameObject NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static TextMeshProUGUI NewText(string name, Transform parent, string text, float size, FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.fontStyle = style;
            t.color = Color.white;
            return t;
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }
    }
}
