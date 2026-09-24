using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Palette, procedural sprites and widget factory for the NPC dossier panel.
    /// Everything is built from code (no prefabs), matching the rest of the mod's UI.
    /// </summary>
    internal static class DK
    {
        // ─── Palette ──────────────────────────────────────────────────────────────
        public static readonly Color Backdrop   = Hex("#06070900", 0.72f);
        public static readonly Color PanelBg    = Hex("#16181d");
        public static readonly Color HeaderBg   = Hex("#1a1c22");
        public static readonly Color RailBg     = Hex("#181a20");
        public static readonly Color Card       = Hex("#1d2027");
        public static readonly Color CardInner  = Hex("#20242c");
        public static readonly Color Tile       = Hex("#1f222a");
        public static readonly Color Line       = Hex("#2a2e38");
        public static readonly Color LineStrong = Hex("#3a3f4c");
        public static readonly Color InputBg    = Hex("#15171c");
        public static readonly Color InputLine  = Hex("#343947");
        public static readonly Color Track      = Hex("#2a2e38");
        public static readonly Color TextBright = Hex("#f3ecdc");
        public static readonly Color Ink        = Hex("#ebe5d6");
        public static readonly Color Body       = Hex("#d9d2c2");
        public static readonly Color Muted      = Hex("#a39d8f");
        public static readonly Color Faint      = Hex("#8a8578");
        public static readonly Color Dim        = Hex("#6e6a60");
        public static readonly Color Accent     = Hex("#d4a857");
        public static readonly Color AccentText = Hex("#e8c47e");
        public static readonly Color OnAccent   = Hex("#1a1408");
        public static readonly Color Pos        = Hex("#7cc4b0");
        public static readonly Color PosText    = Hex("#9fdcca");
        public static readonly Color Neg        = Hex("#e0805e");
        public static readonly Color NegTrack   = Hex("#b5573a");
        public static readonly Color PosTrack   = Hex("#3f8f7b");
        public static readonly Color Info       = Hex("#9cc3e6");
        public static readonly Color ChipBg     = Hex("#262a33");
        public static readonly Color WarmChip   = Hex("#3a3020");
        public static readonly Color PosChip    = Hex("#1f3a33");
        public static readonly Color InfoChip   = Hex("#232f3a");
        public static readonly Color NegChip    = Hex("#3a231d");
        public static readonly Color CloseBg    = Hex("#231d1c");
        public static readonly Color CloseFg    = Hex("#f0b9a3");
        public static readonly Color EditedLine = Hex("#6b5530");

        public static TMP_FontAsset BodyFont;
        public static TMP_FontAsset DisplayFont;

        public static Color Hex(string hex, float alphaOverride = -1f)
        {
            ColorUtility.TryParseHtmlString(hex.Length == 9 ? hex.Substring(0, 7) : hex, out var c);
            if (alphaOverride >= 0) c.a = alphaOverride;
            return c;
        }

        public static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        // ─── Procedural sprites (9-sliced rounded rects) ──────────────────────────
        private static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();

        /// <summary>Filled rounded rect, 9-sliced so it stretches to any size.</summary>
        public static Sprite Rounded(int radius) => GetSprite(radius, false);

        /// <summary>1px outline of a rounded rect, 9-sliced; layered over a fill to draw a border.</summary>
        public static Sprite RoundedOutline(int radius) => GetSprite(radius, true);

        private static Sprite GetSprite(int radius, bool outline)
        {
            radius = Mathf.Max(1, radius);
            string key = (outline ? "o" : "f") + radius;
            if (_sprites.TryGetValue(key, out var cached) && cached != null) return cached;

            int size = radius * 2 + 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[size * size];
            float r = radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from the nearest corner-circle centre (0 inside the straight edges).
                    float cx = Mathf.Clamp(x + 0.5f, r, size - r);
                    float cy = Mathf.Clamp(y + 0.5f, r, size - r);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    float a;
                    if (outline)
                    {
                        float outer = Mathf.Clamp01(r - d + 0.5f);
                        float inner = Mathf.Clamp01((r - 1f) - d + 0.5f);
                        a = Mathf.Clamp01(outer - inner);
                    }
                    else a = Mathf.Clamp01(r - d + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var border = new Vector4(radius + 1, radius + 1, radius + 1, radius + 1);
            var sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            sp.name = "DK_" + key;
            _sprites[key] = sp;
            return sp;
        }

        // ─── Basic objects ────────────────────────────────────────────────────────

        public static RectTransform Obj(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5; // UI
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        public static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
        }

        public static Image Fill(RectTransform rt, Color color, int radius = 0, bool raycast = true)
        {
            var img = rt.gameObject.GetComponent<Image>() ?? rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            if (radius > 0) { img.sprite = Rounded(radius); img.type = Image.Type.Sliced; }
            return img;
        }

        /// <summary>Adds a 1px rounded border drawn over the element (child image, no raycast).</summary>
        public static Image Border(RectTransform rt, Color color, int radius)
        {
            var b = Obj("Border", rt);
            Stretch(b);
            b.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            var img = b.gameObject.AddComponent<Image>();
            img.sprite = RoundedOutline(radius);
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static LayoutElement LE(Component c, float prefW = -1, float prefH = -1, float flexW = -1, float flexH = -1, float minH = -1, float minW = -1)
        {
            var le = c.gameObject.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
            if (prefW >= 0) le.preferredWidth = prefW;
            if (prefH >= 0) le.preferredHeight = prefH;
            if (flexW >= 0) le.flexibleWidth = flexW;
            if (flexH >= 0) le.flexibleHeight = flexH;
            if (minH >= 0) le.minHeight = minH;
            if (minW >= 0) le.minWidth = minW;
            return le;
        }

        public static RectTransform VStack(Transform parent, float spacing, RectOffset pad = null, string name = "VStack", TextAnchor align = TextAnchor.UpperLeft)
        {
            var rt = Obj(name, parent);
            var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.padding = pad ?? new RectOffset(0, 0, 0, 0);
            v.childAlignment = align;
            v.childControlWidth = true; v.childForceExpandWidth = true;
            v.childControlHeight = true; v.childForceExpandHeight = false;
            return rt;
        }

        public static RectTransform HStack(Transform parent, float spacing, RectOffset pad = null, string name = "HStack", TextAnchor align = TextAnchor.MiddleLeft, bool expandWidth = false)
        {
            var rt = Obj(name, parent);
            var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.padding = pad ?? new RectOffset(0, 0, 0, 0);
            h.childAlignment = align;
            h.childControlWidth = true; h.childForceExpandWidth = expandWidth;
            h.childControlHeight = true; h.childForceExpandHeight = false;
            return rt;
        }

        public static RectTransform Spacer(Transform parent, float flexW = 1f)
        {
            var rt = Obj("Spacer", parent);
            LE(rt, flexW: flexW);
            return rt;
        }

        public static RectTransform Rule(Transform parent, Color? color = null)
        {
            var rt = Obj("Rule", parent);
            Fill(rt, color ?? Line, 0, false);
            LE(rt, prefH: 1, minH: 1);
            return rt;
        }

        // ─── Text ─────────────────────────────────────────────────────────────────

        public static TextMeshProUGUI Text(Transform parent, string s, float size, Color color,
            FontStyles style = FontStyles.Normal, bool wrap = true, bool display = false,
            TextAlignmentOptions align = TextAlignmentOptions.TopLeft, string name = "Text")
        {
            var rt = Obj(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            var font = display ? (DisplayFont ?? BodyFont) : BodyFont;
            if (font != null) t.font = font;
            t.text = s ?? "";
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.enableWordWrapping = wrap;
            t.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            t.alignment = align;
            t.raycastTarget = false;
            t.richText = true;
            return t;
        }

        /// <summary>Small uppercase section label, e.g. "CURRENT GOAL".</summary>
        public static TextMeshProUGUI Label(Transform parent, string s, Color? color = null)
        {
            var t = Text(parent, s.ToUpperInvariant(), 11, color ?? Faint, FontStyles.Bold, wrap: false);
            t.characterSpacing = 8f;
            return t;
        }

        /// <summary>Escapes TMP rich-text tags in AI/user-authored strings so they render literally.</summary>
        public static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "<​");

        // ─── Containers ───────────────────────────────────────────────────────────

        /// <summary>Rounded card with a 1px border and vertical layout. Returns the card itself.</summary>
        public static RectTransform CardBox(Transform parent, float spacing = 12, int padX = 20, int padY = 18,
            Color? fill = null, Color? border = null, int radius = 12, string name = "Card")
        {
            var rt = VStack(parent, spacing, new RectOffset(padX, padX, padY, padY), name);
            Fill(rt, fill ?? Card, radius, false);
            Border(rt, border ?? Line, radius);
            return rt;
        }

        /// <summary>
        /// A row of equal or weighted columns (uGUI has no CSS grid). Each returned column is a
        /// VStack whose width share is its weight.
        /// </summary>
        public static List<RectTransform> Columns(Transform parent, float gap, params float[] weights)
        {
            var row = HStack(parent, gap, null, "Columns", TextAnchor.UpperLeft, expandWidth: false);
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = false;
            var cols = new List<RectTransform>();
            foreach (var w in weights)
            {
                var col = VStack(row, gap, null, "Col");
                LE(col, prefW: 0, flexW: w);
                cols.Add(col);
            }
            return cols;
        }

        public static RectTransform Chip(Transform parent, string s, Color bg, Color fg, float size = 12, FontStyles style = FontStyles.Normal)
        {
            var rt = HStack(parent, 0, new RectOffset(10, 10, 4, 4), "Chip", TextAnchor.MiddleCenter);
            Fill(rt, bg, 11, false);
            var t = Text(rt, s, size, fg, style, wrap: false);
            t.alignment = TextAlignmentOptions.Center;
            return rt;
        }

        public static RectTransform Flow(Transform parent, float spacing = 6)
        {
            var rt = Obj("Flow", parent);
            var f = rt.gameObject.AddComponent<FlowLayout>();
            f.spacingX = spacing; f.spacingY = spacing;
            return rt;
        }

        // ─── Buttons ──────────────────────────────────────────────────────────────

        public enum BtnStyle { Primary, Secondary, Ghost, Link, Danger, Locked }

        public class Btn
        {
            public Button button;
            public Image bg;
            public Image border;
            public TextMeshProUGUI label;
            public RectTransform rt;

            public void SetInteractable(bool on)
            {
                button.interactable = on;
                if (label != null) label.alpha = on ? 1f : 0.55f;
            }
        }

        public static Btn Button(Transform parent, string text, BtnStyle style, Action onClick,
            float height = 44, float width = -1, float fontSize = 13, int radius = 10, string tooltip = null)
        {
            var rt = HStack(parent, 8, new RectOffset(16, 16, 0, 0), "Btn_" + text, TextAnchor.MiddleCenter);
            LE(rt, prefH: height, minH: height, prefW: width >= 0 ? width : -1);
            if (width >= 0) rt.GetComponent<LayoutElement>().minWidth = width;

            Color fill, fg, line;
            switch (style)
            {
                case BtnStyle.Primary:   fill = Accent;  fg = OnAccent; line = Color.clear; break;
                case BtnStyle.Secondary: fill = Tile;    fg = Ink;     line = LineStrong;  break;
                case BtnStyle.Ghost:     fill = Color.clear; fg = Ink; line = LineStrong;  break;
                case BtnStyle.Link:      fill = Color.clear; fg = Accent; line = Color.clear; break;
                case BtnStyle.Danger:    fill = CloseBg; fg = CloseFg;  line = LineStrong;  break;
                default:                 fill = Color.clear; fg = Faint; line = LineStrong; break;
            }

            var bg = Fill(rt, fill.a > 0 ? fill : new Color(0, 0, 0, 0.001f), radius, true);
            Image border = line.a > 0 ? Border(rt, line, radius) : null;

            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            var cb = btn.colors;
            // Tint multiplies the fill, so "normal" sits slightly below white to leave room for a hover lift.
            cb.normalColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            cb.highlightedColor = Color.white;
            cb.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            cb.selectedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            cb.disabledColor = new Color(0.9f, 0.9f, 0.9f, 0.45f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            var nav = btn.navigation; nav.mode = Navigation.Mode.None; btn.navigation = nav;
            if (onClick != null) btn.onClick.AddListener(() => { try { onClick(); } catch (Exception ex) { Debug.LogWarning("[NPCDossier] Button action failed: " + ex); } });

            var t = Text(rt, text, fontSize, fg, style == BtnStyle.Primary ? FontStyles.Bold : FontStyles.Bold, wrap: false);
            t.alignment = TextAlignmentOptions.Center;

            if (!string.IsNullOrEmpty(tooltip)) rt.gameObject.AddComponent<DossierTooltip>().text = tooltip;

            return new Btn { button = btn, bg = bg, border = border, label = t, rt = rt };
        }

        /// <summary>Square icon-style button with a short text glyph ("X", "<", ">").</summary>
        public static Btn IconButton(Transform parent, string glyph, BtnStyle style, Action onClick, float size = 44, float fontSize = 16, string tooltip = null)
        {
            var b = Button(parent, glyph, style, onClick, size, size, fontSize, 10, tooltip);
            var h = b.rt.GetComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(0, 0, 0, 0);
            return b;
        }

        // ─── Bars / meters ────────────────────────────────────────────────────────

        /// <summary>Horizontal progress bar, pct in [0,1].</summary>
        public static RectTransform Bar(Transform parent, float pct, Color color, float height = 8)
        {
            var track = Obj("Bar", parent);
            Fill(track, Track, Mathf.Max(1, (int)(height / 2)), false);
            LE(track, prefH: height, minH: height);
            var fill = Obj("Fill", track);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(Mathf.Clamp01(pct), 1f);
            fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
            if (pct > 0.001f) Fill(fill, color, Mathf.Max(1, (int)(height / 2)), false);
            return track;
        }

        /// <summary>Bar centred on zero for values in [-100, 100]: positive grows right, negative left.</summary>
        public static RectTransform CenterBar(Transform parent, int value, float height = 6)
        {
            var track = Obj("CenterBar", parent);
            Fill(track, Track, Mathf.Max(1, (int)(height / 2)), false);
            LE(track, prefH: height, minH: height);
            float v = Mathf.Clamp(value, -100, 100) / 200f; // [-0.5, 0.5]
            var fill = Obj("Fill", track);
            fill.anchorMin = new Vector2(v >= 0 ? 0.5f : 0.5f + v, 0f);
            fill.anchorMax = new Vector2(v >= 0 ? 0.5f + v : 0.5f, 1f);
            fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
            if (Mathf.Abs(v) > 0.001f) Fill(fill, value >= 0 ? Pos : Neg, Mathf.Max(1, (int)(height / 2)), false);
            var tick = Obj("Zero", track);
            tick.anchorMin = new Vector2(0.5f, 0f); tick.anchorMax = new Vector2(0.5f, 1f);
            tick.sizeDelta = new Vector2(2f, height + 6f);
            tick.anchoredPosition = Vector2.zero;
            Fill(tick, Dim, 0, false);
            return track;
        }

        // ─── Switch ───────────────────────────────────────────────────────────────

        public class Switch
        {
            public Button button;
            public Image track;
            public RectTransform knob;
            public bool danger;

            public void Set(bool on)
            {
                track.color = on ? (danger ? NegTrack : PosTrack) : InputLine;
                knob.anchoredPosition = new Vector2(on ? 11f : -11f, 0f);
            }
        }

        public static Switch Toggle(Transform parent, bool on, bool danger, Action onClick)
        {
            var rt = Obj("Switch", parent);
            LE(rt, prefW: 52, prefH: 30, minH: 30, minW: 52);
            var track = Fill(rt, InputLine, 15, true);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = track;
            var nav = btn.navigation; nav.mode = Navigation.Mode.None; btn.navigation = nav;
            btn.onClick.AddListener(() => onClick?.Invoke());
            var knob = Obj("Knob", rt);
            knob.anchorMin = knob.anchorMax = new Vector2(0.5f, 0.5f);
            knob.sizeDelta = new Vector2(24, 24);
            Fill(knob, TextBright, 12, false);
            var sw = new Switch { button = btn, track = track, knob = knob, danger = danger };
            sw.Set(on);
            return sw;
        }

        // ─── Inputs ───────────────────────────────────────────────────────────────

        public static TMP_InputField Input(Transform parent, string value, string placeholder, bool multiline,
            float height, Action<string> onChanged, float fontSize = 14, Action<string> onEndEdit = null)
        {
            var rt = Obj("Input", parent);
            LE(rt, prefH: height, minH: height, flexW: 1);
            Fill(rt, InputBg, 8, true);
            Border(rt, InputLine, 8);

            var area = Obj("TextArea", rt);
            Stretch(area);
            area.offsetMin = new Vector2(12, 8); area.offsetMax = new Vector2(-12, -8);
            area.gameObject.AddComponent<RectMask2D>();

            var ph = Text(area, placeholder ?? "", fontSize, Dim, FontStyles.Italic, wrap: multiline, name: "Placeholder");
            Stretch(ph.rectTransform);
            var txt = Text(area, "", fontSize, Ink, FontStyles.Normal, wrap: multiline, name: "Text");
            Stretch(txt.rectTransform);
            txt.richText = false;
            if (!multiline) { ph.alignment = TextAlignmentOptions.MidlineLeft; txt.alignment = TextAlignmentOptions.MidlineLeft; }

            // Deactivated during setup so TMP_InputField.OnEnable sees its wiring complete.
            rt.gameObject.SetActive(false);
            var input = rt.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = area;
            input.textComponent = txt;
            input.placeholder = ph;
            input.fontAsset = txt.font;
            input.pointSize = fontSize;
            input.richText = false;
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.scrollSensitivity = 20f;
            input.caretColor = Accent;
            input.customCaretColor = true;
            input.selectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.35f);
            input.restoreOriginalTextOnEscape = false;
            var nav = input.navigation; nav.mode = Navigation.Mode.None; input.navigation = nav;
            input.SetTextWithoutNotify(value ?? "");
            if (onChanged != null) input.onValueChanged.AddListener(s => onChanged(s));
            if (onEndEdit != null) input.onEndEdit.AddListener(s => onEndEdit(s));
            rt.gameObject.SetActive(true);
            return input;
        }

        // ─── Scroll view ──────────────────────────────────────────────────────────

        /// <summary>
        /// Vertical scroll view that fills its parent. Viewport uses RectMask2D, never
        /// Mask + transparent Image (cullTransparentMesh would hide every child).
        /// Returns the content transform (a VStack).
        /// </summary>
        public static RectTransform ScrollView(RectTransform host, RectOffset pad, float spacing, out ScrollRect scroll)
        {
            scroll = host.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 35f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;

            var viewport = Obj("Viewport", host);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            // Invisible raycast surface so the wheel scrolls over empty space too.
            var hit = viewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0.001f);
            scroll.viewport = viewport;

            var content = VStack(viewport, spacing, pad, "Content");
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero; content.offsetMax = Vector2.zero;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = content;

            // Slim scrollbar on the right edge.
            var sbRt = Obj("Scrollbar", host);
            sbRt.anchorMin = new Vector2(1, 0); sbRt.anchorMax = new Vector2(1, 1);
            sbRt.pivot = new Vector2(1, 0.5f);
            sbRt.sizeDelta = new Vector2(8, -12);
            sbRt.anchoredPosition = new Vector2(-3, 0);
            var sbImg = Fill(sbRt, new Color(0, 0, 0, 0.001f), 0, true);
            var handleArea = Obj("Sliding", sbRt); Stretch(handleArea);
            var handle = Obj("Handle", handleArea); Stretch(handle);
            var hImg = Fill(handle, LineStrong, 4, true);
            var sb = sbRt.gameObject.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;
            sb.handleRect = handle;
            sb.targetGraphic = hImg;
            var nav = sb.navigation; nav.mode = Navigation.Mode.None; sb.navigation = nav;
            scroll.verticalScrollbar = sb;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scroll.verticalScrollbarSpacing = 0;
            return content;
        }

        public static void ClearChildren(Transform t)
        {
            if (t == null) return;
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                var child = t.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }
    }

    /// <summary>Wrapping row layout (chips / tags). uGUI has no built-in flow layout.</summary>
    internal class FlowLayout : LayoutGroup
    {
        public float spacingX = 6f;
        public float spacingY = 6f;
        private float _height;

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            SetLayoutInputForAxis(0, 0, 1, 0);
        }

        public override void CalculateLayoutInputVertical()
        {
            _height = Arrange(false);
            SetLayoutInputForAxis(_height, _height, 0, 1);
        }

        public override void SetLayoutHorizontal() { }
        public override void SetLayoutVertical() { Arrange(true); }

        private float Arrange(bool apply)
        {
            float width = rectTransform.rect.width - padding.horizontal;
            if (width <= 0) width = 800f;
            float x = 0, y = 0, rowH = 0;
            foreach (var child in rectChildren)
            {
                float w = Mathf.Min(LayoutUtility.GetPreferredWidth(child), width);
                float h = LayoutUtility.GetPreferredHeight(child);
                if (x > 0 && x + w > width) { x = 0; y += rowH + spacingY; rowH = 0; }
                if (apply)
                {
                    SetChildAlongAxis(child, 0, padding.left + x, w);
                    SetChildAlongAxis(child, 1, padding.top + y, h);
                }
                x += w + spacingX;
                rowH = Mathf.Max(rowH, h);
            }
            return padding.vertical + y + rowH;
        }
    }

    /// <summary>Drags a target window by its header. Delta is divided by canvas scale so it tracks the cursor.</summary>
    internal class DossierDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public RectTransform target;
        private Canvas _canvas;

        public void OnBeginDrag(PointerEventData eventData)
        {
            _canvas = target != null ? target.GetComponentInParent<Canvas>()?.rootCanvas : null;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (target == null) return;
            float scale = _canvas != null ? _canvas.scaleFactor : 1f;
            target.anchoredPosition += eventData.delta / Mathf.Max(0.01f, scale);
        }
    }

    /// <summary>Hover tooltip shown in the dossier's shared tooltip label.</summary>
    internal class DossierTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string text;
        public static Action<string, RectTransform> Show;
        public static Action Hide;

        public void OnPointerEnter(PointerEventData eventData) => Show?.Invoke(text, (RectTransform)transform);
        public void OnPointerExit(PointerEventData eventData) => Hide?.Invoke();
        private void OnDisable() => Hide?.Invoke();
    }
}
