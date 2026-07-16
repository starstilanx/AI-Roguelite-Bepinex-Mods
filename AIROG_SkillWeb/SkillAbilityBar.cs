using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Compact in-gameplay panel listing the usable abilities unlocked from Keystone/Confluence
    /// nodes. Clicking one closes the panel and hands off to the game's native target-selection
    /// flow (PrePrepareToReceive + PrepareToReceive), exactly like the inventory's ability picker —
    /// so the ability can be used on any object, creature, or place, with the native roll bonus,
    /// cooldown, and AI resolution.
    /// </summary>
    public class SkillAbilityBar : MonoBehaviour
    {
        public static SkillAbilityBar Instance { get; private set; }

        private GameplayManager _manager;
        private GameObject      _window;
        private RectTransform   _listContent;
        private readonly List<(string uuid, RawImage img)> _iconRefs = new List<(string, RawImage)>();

        private bool Showing => _window != null && _window.activeSelf;

        // ── Entry point ───────────────────────────────────────────────────────────

        public static void Open(GameplayManager manager)
        {
            if (Instance == null)
            {
                var obj = new GameObject("SkillAbilityBar");
                Instance = obj.AddComponent<SkillAbilityBar>();
            }
            Instance.Show(manager);
        }

        public void Show(GameplayManager manager)
        {
            _manager = manager;
            if (_window == null) Build();
            _window.SetActive(true);
            Refresh();
        }

        public void Close()
        {
            if (_window != null) _window.SetActive(false);
        }

        public void RefreshIfShowing()
        {
            if (Showing) Refresh();
        }

        void OnDestroy()
        {
            if (_window != null) Destroy(_window);
        }

        void Update()
        {
            if (!Showing) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) Close();
        }

        // ── Construction ──────────────────────────────────────────────────────────

        void Build()
        {
            _window = new GameObject("SkillAbilityBarWindow", typeof(RectTransform));
            _window.transform.SetParent(null, false);
            var canvas = _window.AddComponent<Canvas>();
            canvas.renderMode      = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder    = 450; // just below the full-screen constellation (500)
            _window.AddComponent<GraphicRaycaster>();
            var scaler = _window.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight  = 0.5f;
            Stretch(_window.GetComponent<RectTransform>());

            // Click-away backdrop
            var backdrop = NewImg("Backdrop", _window.transform, new Color(0, 0, 0, 0.55f));
            Stretch(backdrop.rectTransform);
            var backBtn = backdrop.gameObject.AddComponent<Button>();
            backBtn.transition = Selectable.Transition.None;
            backBtn.onClick.AddListener(Close);

            // Panel
            var panel = NewImg("Panel", _window.transform, new Color(0.07f, 0.04f, 0.02f, 0.98f));
            var pr = panel.rectTransform;
            pr.anchorMin = new Vector2(0.5f, 0.5f);
            pr.anchorMax = new Vector2(0.5f, 0.5f);
            pr.pivot     = new Vector2(0.5f, 0.5f);
            pr.sizeDelta = new Vector2(560, 640);
            var panelOutline = panel.gameObject.AddComponent<Outline>();
            panelOutline.effectColor    = new Color(1f, 0.82f, 0.4f, 0.35f);
            panelOutline.effectDistance = new Vector2(2, 2);

            // Title
            var title = NewText("Title", panel.transform, "✦  Skill Abilities", 26,
                TextAlignmentOptions.Center, new Color(1f, 0.9f, 0.55f));
            Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -54), new Vector2(-16, -10));

            var subtitle = NewText("Subtitle", panel.transform,
                "Unlocked from Keystones & Confluences — click to use on any target.", 13,
                TextAlignmentOptions.Center, new Color(0.8f, 0.75f, 0.6f));
            Anchor(subtitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -80), new Vector2(-16, -56));

            // Close button
            var closeBtn = NewButton("✕", panel.transform, new Color(0.3f, 0.1f, 0.06f));
            var cr = closeBtn.GetComponent<RectTransform>();
            cr.anchorMin = new Vector2(1, 1); cr.anchorMax = new Vector2(1, 1); cr.pivot = new Vector2(1, 1);
            cr.anchoredPosition = new Vector2(-8, -8); cr.sizeDelta = new Vector2(34, 34);
            closeBtn.GetComponent<Button>().onClick.AddListener(Close);

            // Scroll view
            var scrollObj = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(Mask));
            scrollObj.transform.SetParent(panel.transform, false);
            scrollObj.GetComponent<Image>().color = new Color(0, 0, 0, 0.15f);
            var scrollRt = scrollObj.GetComponent<RectTransform>();
            Anchor(scrollRt, new Vector2(0, 0), new Vector2(1, 1), new Vector2(14, 14), new Vector2(-14, -92));

            var contentObj = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObj.transform.SetParent(scrollObj.transform, false);
            _listContent = contentObj.GetComponent<RectTransform>();
            _listContent.anchorMin = new Vector2(0, 1);
            _listContent.anchorMax = new Vector2(1, 1);
            _listContent.pivot     = new Vector2(0.5f, 1);
            _listContent.anchoredPosition = Vector2.zero;
            var vlg = contentObj.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = 8; vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            var fitter = contentObj.GetComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = scrollObj.GetComponent<ScrollRect>();
            sr.content = _listContent;
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 24f;
        }

        // ── Population ────────────────────────────────────────────────────────────

        void Refresh()
        {
            if (_listContent == null) return;
            _iconRefs.Clear();
            foreach (Transform child in _listContent) Destroy(child.gameObject);

            var plugin = SkillWebPlugin.Instance;
            var abilities = plugin?.GetUsableAbilities() ?? new List<KeyValuePair<WebNode, GameAbility>>();

            if (abilities.Count == 0)
            {
                var empty = NewText("Empty", _listContent,
                    "No usable abilities yet.\n\nUnlock a Keystone (or Confluence) in the Constellation to gain one.",
                    16, TextAlignmentOptions.Center, new Color(0.75f, 0.72f, 0.65f));
                var le = empty.gameObject.AddComponent<LayoutElement>();
                le.minHeight = 120; le.preferredHeight = 120;
                return;
            }

            foreach (var pair in abilities)
                BuildRow(pair.Key, pair.Value);
        }

        void BuildRow(WebNode node, GameAbility ability)
        {
            bool ready = ability.IsAvailableToUse();

            var row = new GameObject("Row_" + node.id, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            row.transform.SetParent(_listContent, false);
            row.GetComponent<Image>().color = ready ? new Color(0.16f, 0.11f, 0.05f, 0.95f)
                                                     : new Color(0.10f, 0.09f, 0.08f, 0.95f);
            row.GetComponent<LayoutElement>().minHeight = 84;

            // Icon
            var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(RawImage));
            iconObj.transform.SetParent(row.transform, false);
            var iconRt = iconObj.GetComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0, 0.5f); iconRt.anchorMax = new Vector2(0, 0.5f);
            iconRt.pivot = new Vector2(0, 0.5f);
            iconRt.anchoredPosition = new Vector2(10, 0); iconRt.sizeDelta = new Vector2(64, 64);
            var iconImg = iconObj.GetComponent<RawImage>();
            iconImg.color = ready ? Color.white : new Color(0.6f, 0.6f, 0.6f, 1f);
            Utils.BytesToTexture2(iconImg, ability.uuid, SS.I.saveSubDirAsArg, TextureAssets.I.placeholderAbilityTex);
            _iconRefs.Add((ability.uuid, iconImg));

            // Type tag colour
            string typeTag = node.type == WebNodeType.Confluence
                ? "<color=#B58CFF>[✶ Confluence]</color>"
                : "<color=#FFD24A>[✦ Keystone]</color>";

            // Name line
            var name = NewText("Name", row.transform, $"{typeTag}  {ability.GetPrettyName()}", 18,
                TextAlignmentOptions.Left, new Color(1f, 0.95f, 0.85f));
            Anchor(name.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(86, -8), new Vector2(-10, -34));

            // Description line
            string descRaw = ability.GetPotentiallyNullDescription();
            string desc = string.IsNullOrWhiteSpace(descRaw) ? "<i>Generating ability details…</i>" : descRaw.Trim();
            var descText = NewText("Desc", row.transform, desc, 13,
                TextAlignmentOptions.TopLeft, new Color(0.82f, 0.8f, 0.72f));
            descText.enableWordWrapping = true;
            Anchor(descText.rectTransform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(86, 24), new Vector2(-10, -34));

            // Status line (ready / cooldown)
            string status = ready
                ? "<color=#7CFF7C>● Ready — click to use</color>"
                : $"<color=#AAAAAA>◔ Cooldown: {ability.cooldownTurnsRemaining} turn(s)</color>";
            var statusText = NewText("Status", row.transform, status, 12,
                TextAlignmentOptions.BottomLeft, Color.white);
            Anchor(statusText.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(86, 6), new Vector2(-10, 22));

            var btn = row.GetComponent<Button>();
            btn.interactable = ready;
            btn.onClick.AddListener(() => CastAbility(ability));
        }

        // ── Casting ───────────────────────────────────────────────────────────────

        void CastAbility(GameAbility ability)
        {
            if (_manager == null || ability == null) return;

            if (Utils.PlayerInteractionsDisabled())
            {
                SoundManager.I.deniedSoundFxObj.PlayNextSound();
                return;
            }
            if (!ability.IsAvailableToUse())
            {
                SoundManager.I.deniedSoundFxObj.PlayNextSound();
                Toast.I.ShowToast(Utils.Colorize("That ability is on cooldown.", "ff3c00"));
                return;
            }
            // Energy / survival-bar gate (returns true when it blocked and showed a modal).
            if (_manager.MaybeShowModalForNotEnoughSurvivalBarForAbil(ability))
                return;

            SoundManager.I.smallClickSoundFxObj.PlayNextSound();
            Close();

            // Hand off to the native target-selection flow — identical to the inventory ability picker.
            _manager.UnprepareToReceiveAll();
            _manager.PrePrepareToReceive(ability); // shows "Select a target…" and arms expecting-interactee
            _manager.PrepareToReceive(ability);    // makes every selectable/grid tile receive this ability
        }

        // ── Tiny UI helpers ───────────────────────────────────────────────────────

        static Image NewImg(string name, Transform parent, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().color = color;
            return obj.GetComponent<Image>();
        }

        static TextMeshProUGUI NewText(string name, Transform parent, string text, float size,
            TextAlignmentOptions align, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            var tmp = obj.GetComponent<TextMeshProUGUI>();
            tmp.text = text; tmp.fontSize = size; tmp.alignment = align; tmp.color = color;
            tmp.raycastTarget = false;
            return tmp;
        }

        static GameObject NewButton(string label, Transform parent, Color bgColor)
        {
            var obj = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().color = bgColor;
            var t = NewText("Label", obj.transform, label, 16, TextAlignmentOptions.Center, Color.white);
            Stretch(t.rectTransform);
            return obj;
        }

        static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        static void Anchor(RectTransform r, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            r.anchorMin = aMin; r.anchorMax = aMax;
            r.offsetMin = offMin; r.offsetMax = offMax;
        }
    }
}
