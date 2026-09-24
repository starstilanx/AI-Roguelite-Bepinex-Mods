using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// The NPC dossier: one modal window holding everything NPC Expansion knows about an NPC
    /// (profile, mind, bonds, secrets, combat, quests, autonomy) plus in-place profile editing.
    ///
    /// Split across partial files:
    ///   NPCExamineUI.cs       – lifecycle, shell (header / rail / tab bar / save bar), editing state, actions
    ///   NPCExamineUI.Tabs.cs  – the eight tab builders
    ///   UI/DossierKit.cs      – palette + widget factory
    ///
    /// Input model: a full-screen blocker with its own sorting canvas sits under the window, so
    /// nothing behind the dossier receives clicks while it is open (unless the player pins it).
    /// </summary>
    public partial class NPCExamineUI : MonoBehaviour
    {
        public static NPCExamineUI Instance { get; private set; }

        public enum Tab { Overview, Profile, Mind, Bonds, Secrets, Combat, Quests, Autonomy }

        private const float WIN_W = 1200f;
        private const float WIN_H = 800f;
        private const float RAIL_W = 300f;

        private GameplayManager _manager;
        private GameCharacter _npc;

        // Shell
        private GameObject _blocker;
        private RectTransform _window;
        private RectTransform _headerHost;
        private RectTransform _railHost;
        private RectTransform _railContent;
        private RectTransform _tabBarHost;
        private RectTransform _contentHost;
        private RectTransform _content;
        private ScrollRect _contentScroll;
        private RectTransform _footerHost;
        private RectTransform _tooltip;
        private TextMeshProUGUI _tooltipText;
        private RawImage _portrait;
        private bool _portraitFitted;

        // View state
        private Tab _tab = Tab.Overview;
        private bool _editing;
        private bool _advancedOpen;
        private bool _pinned;
        private bool _savedFlash;
        private string _footerError;
        private Action _pendingAfterConfirm;   // non-null while the "unsaved changes" prompt is showing
        private readonly HashSet<string> _busy = new HashSet<string>();

        // Editing
        private Draft _draft;
        private Draft _orig;
        private readonly Dictionary<string, FieldWidgets> _fieldWidgets = new Dictionary<string, FieldWidgets>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            GameplayManager.TurnHappenedEvent -= OnTurnHappened;
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
        }

        private void OnDestroy()
        {
            GameplayManager.TurnHappenedEvent -= OnTurnHappened;
            if (Instance == this) Instance = null;
        }

        public static void Init()
        {
            if (Instance == null)
            {
                var obj = new GameObject("NPCExamineUI");
                Instance = obj.AddComponent<NPCExamineUI>();
            }
        }

        public static void OpenFor(GameCharacter npc, GameplayManager manager) => OpenFor(npc, manager, Tab.Overview, false);

        public static void OpenFor(GameCharacter npc, GameplayManager manager, Tab tab, bool edit)
        {
            if (npc == null) return;
            Init();
            Instance.Show(npc, manager, tab, edit);
        }

        public bool IsOpen => _window != null && _window.gameObject.activeSelf;

        public void RefreshIfNpc(string uuid)
        {
            if (!IsOpen || _npc == null || _npc.uuid != uuid) return;
            // Background generation finished for this NPC: take the new values unless the
            // player has unsaved edits in flight (those win until they save or discard).
            if (!IsDirty) LoadDraft();
            Refresh();
        }

        // ─── Open / close ─────────────────────────────────────────────────────────

        private void Show(GameCharacter npc, GameplayManager manager, Tab tab, bool edit)
        {
            if (!NPCUI.TryResolveManager(manager, "NPCExamineUI", out var resolved)) return;

            if (IsOpen && _npc != null && _npc.uuid != npc.uuid && IsDirty)
            {
                ConfirmThen(() => Show(npc, resolved, tab, edit));
                return;
            }

            _manager = resolved;
            _npc = npc;
            _tab = tab;
            _editing = edit;
            _pendingAfterConfirm = null;
            _footerError = null;
            _savedFlash = false;

            if (_window == null) CreateShell();

            ApplyScale();
            _blocker.SetActive(!_pinned);
            _window.gameObject.SetActive(true);
            _blocker.transform.SetAsLastSibling();
            _window.SetAsLastSibling();

            LoadDraft();
            Refresh();
        }

        private void RequestClose()
        {
            if (IsDirty) { ConfirmThen(CloseNow); return; }
            CloseNow();
        }

        private void CloseNow()
        {
            _pendingAfterConfirm = null;
            ReleasePortrait();
            HideTooltip();
            if (_window != null) _window.gameObject.SetActive(false);
            if (_blocker != null) _blocker.SetActive(false);
            _npc = null;
            _draft = _orig = null;
        }

        /// <summary>Shows the unsaved-changes prompt in the save bar; runs <paramref name="then"/> after save or discard.</summary>
        private void ConfirmThen(Action then)
        {
            _pendingAfterConfirm = then;
            BuildFooter();
        }

        private void ApplyScale()
        {
            var canvasRt = _manager.canvasTransform as RectTransform;
            if (canvasRt == null || _window == null) return;
            var size = canvasRt.rect.size;
            float s = Mathf.Min(1f, (size.x - 48f) / WIN_W, (size.y - 96f) / WIN_H);
            _window.localScale = Vector3.one * Mathf.Max(0.5f, s);
        }

        // ─── Per-frame: keyboard + portrait fit ───────────────────────────────────

        private void Update()
        {
            if (!IsOpen) return;

            if (_npc == null || _manager == null) { CloseNow(); return; }

            if (_portrait != null && !_portraitFitted && _portrait.texture != null && _portrait.color.a > 0.01f)
                FitPortrait();

            var kb = Keyboard.current;
            if (kb == null) return;

            var focusedInput = EventSystem.current?.currentSelectedGameObject?.GetComponent<TMP_InputField>();
            bool typing = focusedInput != null && focusedInput.isFocused;

            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (typing) { focusedInput.DeactivateInputField(); EventSystem.current.SetSelectedGameObject(null); }
                else if (_pendingAfterConfirm != null) { _pendingAfterConfirm = null; BuildFooter(); }
                else RequestClose();
            }
            else if (kb.tabKey.wasPressedThisFrame && !typing)
            {
                int n = Enum.GetValues(typeof(Tab)).Length;
                int dir = kb.shiftKey.isPressed ? -1 : 1;
                SwitchTab((Tab)(((int)_tab + dir + n) % n));
            }
        }

        private void OnTurnHappened(int numTurns, long secs)
        {
            // Only a pinned dossier can be open while turns pass.
            if (IsOpen && !IsDirty) { LoadDraft(); Refresh(); }
        }

        // ─── Shell construction (once per canvas) ────────────────────────────────

        private void CreateShell()
        {
            DK.BodyFont = _manager.npcConvoTextInput?.textComponent?.font
                          ?? _manager.currentPlaceText?.font
                          ?? TMP_Settings.defaultFontAsset;
            DK.DisplayFont = _manager.currentPlaceText?.font ?? DK.BodyFont;

            // Full-screen blocker: its own sorting canvas + raycaster so it sits above every game panel.
            var blockerRt = DK.Obj("NPCDossierBlocker", _manager.canvasTransform);
            DK.Stretch(blockerRt);
            _blocker = blockerRt.gameObject;
            var bc = _blocker.AddComponent<Canvas>();
            bc.overrideSorting = true;
            bc.sortingOrder = 99;
            _blocker.AddComponent<GraphicRaycaster>();
            var blockImg = DK.Fill(blockerRt, DK.Backdrop, 0, true);
            var blockBtn = _blocker.AddComponent<Button>();
            blockBtn.transition = Selectable.Transition.None;
            blockBtn.targetGraphic = blockImg;
            blockBtn.onClick.AddListener(RequestClose);

            var hint = DK.HStack(blockerRt, 10, new RectOffset(14, 14, 8, 8), "InputHint", TextAnchor.MiddleCenter);
            hint.anchorMin = hint.anchorMax = new Vector2(0.5f, 0f);
            hint.pivot = new Vector2(0.5f, 0f);
            hint.anchoredPosition = new Vector2(0, 18);
            hint.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            hint.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            DK.Fill(hint, DK.PanelBg, 16, false);
            DK.Border(hint, DK.InputLine, 16);
            DK.Text(hint, "Game input paused while the dossier is open   |   <b><color=#ebe5d6>Esc</color></b> to close   |   <b><color=#ebe5d6>Tab</color></b> / <b><color=#ebe5d6>Shift+Tab</color></b> switch sections", 13, DK.Muted, wrap: false);

            // Window
            _window = DK.Obj("NPCDossierWindow", _manager.canvasTransform);
            _window.anchorMin = _window.anchorMax = new Vector2(0.5f, 0.5f);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _window.sizeDelta = new Vector2(WIN_W, WIN_H);
            _window.anchoredPosition = Vector2.zero;
            var wc = _window.gameObject.AddComponent<Canvas>();
            wc.overrideSorting = true;
            wc.sortingOrder = 100;
            _window.gameObject.AddComponent<GraphicRaycaster>();
            DK.Fill(_window, DK.PanelBg, 16, true); // raycast target: swallows clicks on empty panel areas
            DK.Border(_window, DK.InputLine, 16);

            var column = DK.VStack(_window, 0, new RectOffset(1, 1, 1, 1), "Column");
            DK.Stretch(column);

            _headerHost = DK.HStack(column, 16, new RectOffset(16, 20, 0, 0), "Header");
            DK.LE(_headerHost, prefH: 76, minH: 76, flexH: 0);
            DK.Fill(_headerHost, DK.HeaderBg, 0, true);
            _headerHost.gameObject.AddComponent<DossierDragHandle>().target = _window;
            DK.Rule(column);

            var body = DK.HStack(column, 0, null, "Body", TextAnchor.UpperLeft);
            body.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = true;
            DK.LE(body, flexH: 1);

            _railHost = DK.Obj("Rail", body);
            DK.LE(_railHost, prefW: RAIL_W, minW: RAIL_W, flexH: 1);
            DK.Fill(_railHost, DK.RailBg, 0, true);
            _railContent = DK.ScrollView(_railHost, new RectOffset(20, 20, 20, 20), 18, out _);

            var railRule = DK.Obj("RailRule", body);
            DK.Fill(railRule, DK.Line, 0, false);
            DK.LE(railRule, prefW: 1, minW: 1, flexH: 1);

            var main = DK.VStack(body, 0, null, "Main");
            main.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
            DK.LE(main, flexW: 1, flexH: 1);

            _tabBarHost = DK.HStack(main, 2, new RectOffset(16, 16, 0, 0), "Tabs", TextAnchor.LowerLeft);
            _tabBarHost.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = true;
            // flexH 0 is required: a layout group that force-expands its children's height reports
            // itself as flexible, and would otherwise split the spare height with the content area.
            DK.LE(_tabBarHost, prefH: 52, minH: 52, flexH: 0);
            DK.Rule(main);

            _contentHost = DK.Obj("ContentHost", main);
            DK.LE(_contentHost, flexH: 1, flexW: 1);
            _content = DK.ScrollView(_contentHost, new RectOffset(28, 28, 24, 28), 16, out _contentScroll);

            DK.Rule(main);
            _footerHost = DK.HStack(main, 12, new RectOffset(28, 20, 0, 0), "Footer");
            DK.LE(_footerHost, prefH: 60, minH: 60, flexH: 0);
            DK.Fill(_footerHost, DK.HeaderBg, 0, true);

            // Shared tooltip (last child so it draws on top).
            _tooltip = DK.HStack(_window, 0, new RectOffset(10, 10, 6, 6), "Tooltip", TextAnchor.MiddleLeft);
            _tooltip.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _tooltip.pivot = new Vector2(0.5f, 0f);
            _tooltip.anchorMin = _tooltip.anchorMax = new Vector2(0.5f, 0.5f);
            var tcsf = _tooltip.gameObject.AddComponent<ContentSizeFitter>();
            tcsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            tcsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            DK.Fill(_tooltip, DK.HeaderBg, 8, false);
            DK.Border(_tooltip, DK.LineStrong, 8);
            _tooltipText = DK.Text(_tooltip, "", 12, DK.Body, wrap: false);
            _tooltip.gameObject.SetActive(false);
            DossierTooltip.Show = ShowTooltip;
            DossierTooltip.Hide = HideTooltip;

            _blocker.SetActive(false);
            _window.gameObject.SetActive(false);
        }

        private void ShowTooltip(string text, RectTransform target)
        {
            if (_tooltip == null || target == null || string.IsNullOrEmpty(text)) return;
            _tooltipText.text = text;
            _tooltip.gameObject.SetActive(true);
            _tooltip.SetAsLastSibling();
            // Place above the hovered element, in window-local space.
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            Vector3 topCenter = (corners[1] + corners[2]) * 0.5f;
            Vector3 local = _window.InverseTransformPoint(topCenter);
            _tooltip.anchoredPosition = new Vector2(local.x, local.y + 6f);
        }

        private void HideTooltip()
        {
            if (_tooltip != null) _tooltip.gameObject.SetActive(false);
        }

        // ─── Refresh ──────────────────────────────────────────────────────────────

        private void Refresh()
        {
            if (!IsOpen || _npc == null) return;
            HideTooltip();
            BuildHeader();
            BuildRail();
            BuildTabBar();
            BuildContent();
            BuildFooter();
        }

        private void SwitchTab(Tab tab)
        {
            if (_tab == tab) return;
            _tab = tab;
            HideTooltip();
            BuildTabBar();
            BuildContent();
        }

        private NPCData Data => NPCData.Load(_npc.uuid);
        private bool IsAlive => _npc != null && _npc.corpseState == GameCharacter.CorpseState.NONE;
        private bool IsDeceased => !IsAlive || (Data?.IsDeceased ?? false);
        private int Affinity => Data?.Affinity ?? 0;

        // ─── Header ───────────────────────────────────────────────────────────────

        private void BuildHeader()
        {
            DK.ClearChildren(_headerHost);
            var data = Data;

            var grip = DK.Text(_headerHost, "::", 18, DK.Dim, FontStyles.Bold, wrap: false);
            DK.LE(grip, prefW: 20);
            grip.gameObject.AddComponent<DossierTooltip>().text = "Drag to move";
            grip.raycastTarget = true;

            var titleCol = DK.VStack(_headerHost, 2, null, "Title", TextAnchor.MiddleLeft);
            DK.LE(titleCol, flexW: 1, prefW: 0);
            var nameRow = DK.HStack(titleCol, 12, null, "NameRow", TextAnchor.MiddleLeft);
            DK.Text(nameRow, DK.Esc(_npc.GetPrettyName()), 30, DK.TextBright, FontStyles.Bold, wrap: false, display: true);
            if (IsDeceased) DK.Chip(nameRow, "DECEASED", DK.NegChip, DK.CloseFg, 11, FontStyles.Bold);
            else DK.Chip(nameRow, "ALIVE", DK.PosChip, DK.PosText, 11, FontStyles.Bold);
            DK.Chip(nameRow, RelationshipArcSystem.GetArcStage(data ?? new NPCData()).ToUpperInvariant(), DK.WarmChip, DK.AccentText, 11, FontStyles.Bold);
            if (data != null && data.IsNemesis) DK.Chip(nameRow, "NEMESIS", DK.NegChip, DK.Neg, 11, FontStyles.Bold);
            DK.Spacer(nameRow);

            var sub = new List<string> { $"Level {_npc.level}" };
            if (_npc.isMerchant) sub.Add("Merchant");
            if (_npc.IsEnemyType()) sub.Add("Hostile");
            string place = SafePlaceName();
            if (!string.IsNullOrEmpty(place)) sub.Add(place);
            DK.Text(titleCol, DK.Esc(string.Join("  ·  ", sub)), 13, DK.Muted, wrap: false);

            // Nearby switcher
            var nearby = NearbyNpcs();
            int idx = nearby.IndexOf(_npc);
            var sw = DK.HStack(_headerHost, 6, new RectOffset(4, 4, 4, 4), "Switcher", TextAnchor.MiddleCenter);
            DK.Fill(sw, new Color(0, 0, 0, 0.001f), 10, false);
            DK.Border(sw, DK.InputLine, 10);
            var prev = DK.IconButton(sw, "<", DK.BtnStyle.Link, () => CycleNpc(-1), 36, 16, "Previous NPC here");
            DK.Text(sw, idx >= 0 ? $"{idx + 1} of {nearby.Count} nearby" : (nearby.Count > 0 ? $"{nearby.Count} nearby" : "None nearby"), 12, DK.Muted, wrap: false);
            var next = DK.IconButton(sw, ">", DK.BtnStyle.Link, () => CycleNpc(1), 36, 16, "Next NPC here");
            prev.label.color = next.label.color = DK.Body;
            bool canCycle = nearby.Count > 1 || (nearby.Count == 1 && idx < 0);
            prev.SetInteractable(canCycle);
            next.SetInteractable(canCycle);

            var pin = DK.IconButton(_headerHost, "PIN", _pinned ? DK.BtnStyle.Primary : DK.BtnStyle.Ghost, TogglePin, 44, 11,
                _pinned ? "Unpin: pause the game again while open" : "Pin: keep the dossier open while you play");
            DK.IconButton(_headerHost, "X", DK.BtnStyle.Danger, RequestClose, 44, 16, "Close (Esc)");
        }

        private string SafePlaceName()
        {
            try { return _npc.parentPlace?.GetPrettyName() ?? ""; } catch { return ""; }
        }

        private List<GameCharacter> NearbyNpcs()
        {
            try { return _manager.GetCharsForNpcConvoSelectorDropdown()?.Where(c => c != null && !c.IsMainPlayer()).ToList() ?? new List<GameCharacter>(); }
            catch { return new List<GameCharacter>(); }
        }

        private void CycleNpc(int dir)
        {
            var nearby = NearbyNpcs();
            if (nearby.Count == 0) return;
            int idx = nearby.IndexOf(_npc);
            int nextIdx = idx < 0 ? 0 : (idx + dir + nearby.Count) % nearby.Count;
            var target = nearby[nextIdx];
            if (target == _npc) return;
            Action go = () => Show(target, _manager, _tab == Tab.Profile ? Tab.Profile : _tab, _editing);
            if (IsDirty) ConfirmThen(go); else go();
        }

        private void TogglePin()
        {
            _pinned = !_pinned;
            if (_blocker != null) _blocker.SetActive(!_pinned && IsOpen);
            BuildHeader();
        }

        // ─── Left rail ────────────────────────────────────────────────────────────

        private void BuildRail()
        {
            ReleasePortrait();
            DK.ClearChildren(_railContent);
            var rail = _railContent;
            var data = Data;

            // Portrait
            var portraitBox = DK.Obj("Portrait", rail);
            DK.LE(portraitBox, prefH: 228, minH: 228);
            DK.Fill(portraitBox, DK.CardInner, 12, false);
            var initials = DK.Text(portraitBox, Initials(_npc.GetPrettyName()), 40, DK.Accent, FontStyles.Bold, wrap: false, display: true, align: TextAlignmentOptions.Center);
            DK.Stretch(initials.rectTransform);
            var clip = DK.Obj("Clip", portraitBox);
            DK.Stretch(clip, 1);
            clip.gameObject.AddComponent<RectMask2D>();
            var raw = DK.Obj("Image", clip);
            DK.Stretch(raw);
            _portrait = raw.gameObject.AddComponent<RawImage>();
            _portrait.color = Color.clear;
            _portrait.raycastTarget = false;
            _portraitFitted = false;
            DK.Border(portraitBox, DK.InputLine, 12);
            try
            {
                if (!string.IsNullOrEmpty(SS.I?.saveSubDirAsArg))
                    _ = Utils.BytesToTexture2(_portrait, _npc.uuid, SS.I.saveSubDirAsArg, null, Color.white, Color.clear);
            }
            catch (Exception ex) { Debug.LogWarning("[NPCDossier] Portrait load failed: " + ex.Message); }

            // Vitals
            var vitals = DK.VStack(rail, 10, null, "Vitals");
            var hpRow = DK.HStack(vitals, 0, null, "HpRow");
            DK.Text(hpRow, "Health", 13, DK.Muted, wrap: false);
            DK.Spacer(hpRow);
            // GetHealth()/GetMaxHealth() read the parent entity; GameCharacter.health is a legacy field that stays 0.
            long hp = _npc.GetHealth(), maxHp = _npc.GetMaxHealth();
            DK.Text(hpRow, $"{hp} / {maxHp}", 13, DK.Ink, FontStyles.Bold, wrap: false);
            float hpPct = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
            DK.Bar(vitals, hpPct, hpPct > 0.35f ? DK.Pos : DK.Neg, 8);
            var tiles = DK.HStack(vitals, 8, new RectOffset(0, 0, 4, 0), "Tiles", TextAnchor.UpperLeft, expandWidth: true);
            StatTile(tiles, "Level", _npc.level.ToString(), DK.Ink);
            StatTile(tiles, "Damage", _npc.damage.ToString(), DK.Ink);
            StatTile(tiles, "Gold", _npc.numGold.ToString(), DK.Accent);

            // Affinity
            int aff = Affinity;
            var affCard = DK.CardBox(rail, 10, 14, 14, DK.Tile, DK.Tile, 12, "Affinity");
            var affTop = DK.HStack(affCard, 0, null, "AffTop", TextAnchor.LowerLeft);
            DK.Text(affTop, "Affinity toward you", 13, DK.Muted, wrap: false);
            DK.Spacer(affTop);
            DK.Text(affTop, (aff > 0 ? "+" : "") + aff, 20, aff >= 0 ? DK.PosText : DK.Neg, FontStyles.Bold, wrap: false);
            DK.CenterBar(affCard, aff, 10);
            var scale = DK.HStack(affCard, 0, null, "Scale");
            DK.Text(scale, "-100", 11, DK.Faint, wrap: false); DK.Spacer(scale);
            DK.Text(scale, "0", 11, DK.Faint, wrap: false); DK.Spacer(scale);
            DK.Text(scale, "+100", 11, DK.Faint, wrap: false);
            DK.Text(affCard, NextStageLine(aff), 12, DK.Muted);

            // Actions
            var actions = DK.VStack(rail, 8, null, "Actions");
            DK.Label(actions, "Actions");
            if (IsDeceased)
            {
                DK.Text(actions, "They can no longer be spoken to.", 13, DK.Muted);
                DK.Button(actions, "Hall of Fallen", DK.BtnStyle.Secondary, () => { CloseNow(); HallOfFallenUI.Open(_manager); });
                return;
            }

            var talk = NativeAction("talk");
            var gift = NativeAction("gift");
            var trade = NativeAction("trade");

            var row1 = DK.HStack(actions, 8, null, "Row1", TextAnchor.MiddleLeft, expandWidth: true);
            var bTalk = DK.Button(row1, "Talk", DK.BtnStyle.Primary, () => RunNativeAction(talk));
            bTalk.SetInteractable(talk != null);
            var bGift = DK.Button(row1, "Give item", DK.BtnStyle.Secondary, () => RunNativeAction(gift));
            bGift.SetInteractable(gift != null);
            FlexAll(row1);

            var row2 = DK.HStack(actions, 8, null, "Row2", TextAnchor.MiddleLeft, expandWidth: true);
            var bTrade = DK.Button(row2, "Trade", DK.BtnStyle.Secondary, () => RunNativeAction(trade), tooltip: trade == null ? "They will not trade right now" : null);
            bTrade.SetInteractable(trade != null);
            DK.Button(row2, "Ask secret", aff >= 60 ? DK.BtnStyle.Secondary : DK.BtnStyle.Locked, () => SwitchTab(Tab.Secrets),
                tooltip: aff >= 60 ? "Open their secrets" : "Needs Ally (60 affinity)");
            FlexAll(row2);

            var row3 = DK.HStack(actions, 8, null, "Row3", TextAnchor.MiddleLeft, expandWidth: true);
            string teachBlock = TeachBlocker(data);
            var bTeach = DK.Button(row3, _busy.Contains("teach") ? "Teaching..." : "Teach me",
                teachBlock == null ? DK.BtnStyle.Secondary : DK.BtnStyle.Locked, DoTeach, tooltip: teachBlock ?? "Costs 8 affinity: sharing is costly");
            bTeach.SetInteractable(teachBlock == null && !_busy.Contains("teach"));
            var bEquip = DK.Button(row3, "Equipment", DK.BtnStyle.Secondary, OpenEquipment);
            bEquip.SetInteractable(!_npc.IsEnemyType());
            FlexAll(row3);
        }

        private void OpenEquipment()
        {
            if (IsDirty) { ConfirmThen(OpenEquipment); return; }
            var npc = _npc;
            var manager = _manager;
            CloseNow();
            NPCEquipmentUI.OpenFor(npc, manager);
        }

        private static void FlexAll(RectTransform row)
        {
            foreach (Transform c in row) DK.LE(c as RectTransform, prefW: 0, flexW: 1);
        }

        private static void StatTile(Transform parent, string label, string value, Color valueColor)
        {
            var t = DK.VStack(parent, 2, new RectOffset(10, 10, 10, 10), "Tile");
            DK.Fill(t, DK.Tile, 10, false);
            DK.LE(t, prefW: 0, flexW: 1);
            DK.Label(t, label);
            DK.Text(t, value, 18, valueColor, FontStyles.Bold, wrap: false);
        }

        private static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            string s = parts.Length == 1 ? parts[0].Substring(0, Math.Min(2, parts[0].Length)) : ("" + parts[0][0] + parts[parts.Length - 1][0]);
            return s.ToUpperInvariant();
        }

        private static readonly (string name, int at, string unlock)[] ArcStages =
        {
            ("Acquaintance", 20, "Remembers you"),
            ("Friend", 40, "Warmer dealings"),
            ("Ally", 60, "Ask Secret unlocked"),
            ("Confidant", 75, "Teach Me unlocked"),
            ("Sworn Companion", 90, "Bond sealed"),
        };

        private static string NextStageLine(int aff)
        {
            foreach (var s in ArcStages)
                if (aff < s.at)
                    return $"<b><color=#ebe5d6>{s.at - aff}</color></b> to <b><color={DK.ToHex(DK.Accent)}>{s.name}</color></b>" +
                           (s.unlock.EndsWith("unlocked") ? $", which unlocks {s.unlock.Replace(" unlocked", "")}" : "");
            return "Their highest regard. Secrets surface on their own above 80.";
        }

        private void FitPortrait()
        {
            var tex = _portrait.texture;
            var rt = _portrait.rectTransform;
            float boxAspect = rt.rect.width / Mathf.Max(1f, rt.rect.height);
            float texAspect = (float)tex.width / Mathf.Max(1, tex.height);
            // Cover: crop whichever axis overflows, keep the upper part of tall portraits (faces).
            if (texAspect > boxAspect)
            {
                float w = boxAspect / texAspect;
                _portrait.uvRect = new Rect((1f - w) / 2f, 0f, w, 1f);
            }
            else
            {
                float h = texAspect / boxAspect;
                _portrait.uvRect = new Rect(0f, 1f - h - (1f - h) * 0.2f, 1f, h);
            }
            _portraitFitted = true;
        }

        private void ReleasePortrait()
        {
            if (_portrait == null) return;
            try { if (EntityTextureCache.KeyFor(_portrait) != null) EntityTextureCache.Unbind(_portrait); }
            catch { /* cache may already be cleared on scene change */ }
            _portrait = null;
        }

        // ─── Tab bar ──────────────────────────────────────────────────────────────

        private void BuildTabBar()
        {
            DK.ClearChildren(_tabBarHost);
            var data = Data;
            foreach (Tab t in Enum.GetValues(typeof(Tab)))
            {
                int? count = null;
                if (data != null)
                {
                    if (t == Tab.Mind) count = (_draft?.memories ?? data.LongTermMemories)?.Count;
                    else if (t == Tab.Secrets) count = data.Secrets?.Count;
                }
                if (t == Tab.Quests) count = QuestsForNpc().Count;

                bool active = t == _tab;
                var tabRt = DK.HStack(_tabBarHost, 8, new RectOffset(14, 14, 0, 0), "Tab_" + t, TextAnchor.MiddleCenter);
                var hit = DK.Fill(tabRt, new Color(0, 0, 0, 0.001f), 0, true);
                var btn = tabRt.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = hit;
                var nav = btn.navigation; nav.mode = Navigation.Mode.None; btn.navigation = nav;
                var captured = t;
                btn.onClick.AddListener(() => SwitchTab(captured));

                DK.Text(tabRt, t.ToString(), 14, active ? DK.TextBright : DK.Muted, FontStyles.Bold, wrap: false);
                if (count.HasValue && count.Value > 0)
                    DK.Chip(tabRt, count.Value.ToString(), DK.ChipBg, DK.Body, 11, FontStyles.Bold)
                      .GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(7, 7, 1, 1);

                if (active)
                {
                    var underline = DK.Obj("Underline", tabRt);
                    underline.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                    underline.anchorMin = new Vector2(0, 0); underline.anchorMax = new Vector2(1, 0);
                    underline.pivot = new Vector2(0.5f, 0);
                    underline.sizeDelta = new Vector2(0, 2);
                    underline.anchoredPosition = Vector2.zero;
                    DK.Fill(underline, DK.Accent, 0, false);
                }
            }
        }

        private void BuildContent()
        {
            _fieldWidgets.Clear();
            DK.ClearChildren(_content);
            try
            {
                switch (_tab)
                {
                    case Tab.Overview: BuildOverview(); break;
                    case Tab.Profile:  BuildProfile();  break;
                    case Tab.Mind:     BuildMind();     break;
                    case Tab.Bonds:    BuildBonds();    break;
                    case Tab.Secrets:  BuildSecrets();  break;
                    case Tab.Combat:   BuildCombat();   break;
                    case Tab.Quests:   BuildQuests();   break;
                    case Tab.Autonomy: BuildAutonomy(); break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NPCDossier] Failed to build {_tab} tab: {ex}");
                DK.Text(_content, $"This section failed to load. Details are in the BepInEx log.", 14, DK.Neg);
            }
            if (_contentScroll != null) _contentScroll.verticalNormalizedPosition = 1f;
        }

        // ─── Footer / save bar ────────────────────────────────────────────────────

        private void BuildFooter()
        {
            if (_footerHost == null) return;
            DK.ClearChildren(_footerHost);
            int n = DirtyCount;

            if (_pendingAfterConfirm != null)
            {
                Dot(_footerHost, DK.AccentText);
                DK.Text(_footerHost, $"You have {n} unsaved {(n == 1 ? "change" : "changes")}.", 13, DK.AccentText, FontStyles.Bold, wrap: false);
                DK.Spacer(_footerHost);
                DK.Button(_footerHost, "Keep editing", DK.BtnStyle.Ghost, () => { _pendingAfterConfirm = null; BuildFooter(); }, 40);
                DK.Button(_footerHost, "Discard", DK.BtnStyle.Ghost, () => { var a = _pendingAfterConfirm; Discard(); a?.Invoke(); }, 40);
                DK.Button(_footerHost, "Save and continue", DK.BtnStyle.Primary, () => { var a = _pendingAfterConfirm; if (Save()) a?.Invoke(); }, 40);
                return;
            }

            if (!string.IsNullOrEmpty(_footerError))
            {
                Dot(_footerHost, DK.Neg);
                DK.Text(_footerHost, _footerError, 13, DK.Neg, FontStyles.Bold, wrap: false);
                DK.Spacer(_footerHost);
                DK.Button(_footerHost, "Dismiss", DK.BtnStyle.Ghost, () => { _footerError = null; BuildFooter(); }, 40);
                return;
            }

            if (n > 0)
            {
                Dot(_footerHost, DK.AccentText);
                DK.Text(_footerHost, $"{n} unsaved {(n == 1 ? "change" : "changes")}", 13, DK.AccentText, FontStyles.Bold, wrap: false);
                DK.Text(_footerHost, "Saved to the NPC file and the native character sheet", 12, DK.Faint, wrap: false);
                DK.Spacer(_footerHost);
                DK.Button(_footerHost, "Discard", DK.BtnStyle.Ghost, () => { Discard(); Refresh(); }, 40);
                DK.Button(_footerHost, "Save changes", DK.BtnStyle.Primary, () => { if (Save()) Refresh(); }, 40);
            }
            else
            {
                DK.Text(_footerHost, "<b>✓</b>", 15, DK.Pos, wrap: false);
                DK.Text(_footerHost, _savedFlash ? "Changes saved" : "All changes saved", 13, DK.Muted, wrap: false);
                DK.Spacer(_footerHost);
                DK.Text(_footerHost, $"Turn {ScenarioUpdater.GlobalTurn}  ·  NPC Expansion {NPCExpansionPlugin.PLUGIN_VERSION}", 12, DK.Faint, wrap: false);
            }
        }

        private static void Dot(Transform parent, Color c)
        {
            var d = DK.Obj("Dot", parent);
            DK.LE(d, prefW: 8, prefH: 8, minH: 8, minW: 8);
            DK.Fill(d, c, 4, false);
        }

        // ─── Editing model ────────────────────────────────────────────────────────

        private class Draft
        {
            public Dictionary<string, string> text = new Dictionary<string, string>();
            public Dictionary<SS.PlayerAttribute, long> attrs = new Dictionary<SS.PlayerAttribute, long>();
            public List<string> memories = new List<string>();

            public Draft Clone() => new Draft
            {
                text = new Dictionary<string, string>(text),
                attrs = new Dictionary<SS.PlayerAttribute, long>(attrs),
                memories = new List<string>(memories)
            };
        }

        private class FieldWidgets
        {
            public GameObject editedChip;
            public Image border;
            public TextMeshProUGUI count;
        }

        private void LoadDraft()
        {
            if (_npc == null) return;
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            var d = new Draft();
            d.text["personality"]  = NPCData.GetPersonality(_npc, data);
            d.text["background"]   = NPCData.GetBackground(_npc, data);
            d.text["visual"]       = !string.IsNullOrEmpty(_npc.importantData?.visualDescription) ? _npc.importantData.visualDescription : (data.Description ?? "");
            d.text["firstMessage"] = data.FirstMessage ?? "";
            d.text["greetings"]    = string.Join("\n", data.AlternateGreetings ?? new List<string>());
            d.text["nature"]       = string.Join(", ", data.Tags ?? new List<string>());
            d.text["disposition"]  = string.Join("\n", data.InteractionTraits ?? new List<string>());
            d.text["examples"]     = data.MessageExamples ?? "";
            d.text["system"]       = data.SystemPrompt ?? "";
            d.text["notes"]        = data.CreatorNotes ?? "";
            d.text["posthist"]     = data.PostHistoryInstructions ?? "";
            d.text["extensions"]   = data.Extensions != null && data.Extensions.Count > 0 ? JsonConvert.SerializeObject(data.Extensions, Formatting.Indented) : "";
            if (data.Attributes != null) d.attrs = new Dictionary<SS.PlayerAttribute, long>(data.Attributes);
            d.memories = new List<string>(data.LongTermMemories ?? new List<string>());
            _orig = d;
            _draft = d.Clone();
        }

        private IEnumerable<string> DirtyKeys()
        {
            if (_draft == null || _orig == null) yield break;
            foreach (var kv in _draft.text)
                if (!_orig.text.TryGetValue(kv.Key, out var o) || (o ?? "") != (kv.Value ?? "")) yield return kv.Key;
            foreach (var kv in _draft.attrs)
                if (!_orig.attrs.TryGetValue(kv.Key, out var o) || o != kv.Value) yield return "attr:" + kv.Key;
            if (!_draft.memories.SequenceEqual(_orig.memories)) yield return "memories";
        }

        private int DirtyCount => DirtyKeys().Count();
        private bool IsDirty => DirtyCount > 0;
        private bool IsFieldDirty(string key) => _orig != null && _draft != null && (_orig.text.TryGetValue(key, out var o) ? o : "") != (_draft.text.TryGetValue(key, out var v) ? v : "");

        private void OnFieldChanged(string key, string value)
        {
            if (_draft == null) return;
            _draft.text[key] = value ?? "";
            _savedFlash = false;
            if (_fieldWidgets.TryGetValue(key, out var w))
            {
                bool dirty = IsFieldDirty(key);
                if (w.editedChip != null) w.editedChip.SetActive(dirty);
                if (w.border != null) w.border.color = dirty ? DK.EditedLine : DK.Line;
                if (w.count != null) w.count.text = $"{(value ?? "").Length} chars";
            }
            BuildFooter();
        }

        private void Discard()
        {
            _pendingAfterConfirm = null;
            _footerError = null;
            if (_orig != null) _draft = _orig.Clone();
            _savedFlash = false;
        }

        /// <summary>Writes the draft into NPCData, mirrors profile text into the native character sheet, flushes for GenContext.</summary>
        private bool Save()
        {
            if (_npc == null || _draft == null) return false;
            var t = _draft.text;

            Dictionary<string, string> ext = null;
            string extRaw = t["extensions"]?.Trim() ?? "";
            if (extRaw.Length > 0)
            {
                try { ext = JsonConvert.DeserializeObject<Dictionary<string, string>>(extRaw); }
                catch
                {
                    _footerError = "Extensions must be a JSON object of string values. Nothing was saved.";
                    _pendingAfterConfirm = null;
                    BuildFooter();
                    return false;
                }
            }

            try
            {
                var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
                data.Personality = t["personality"];
                data.Scenario = t["background"];
                data.Description = t["visual"];
                data.FirstMessage = t["firstMessage"];
                data.AlternateGreetings = SplitLines(t["greetings"]);
                data.Tags = t["nature"].Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                data.InteractionTraits = SplitLines(t["disposition"]);
                data.MessageExamples = t["examples"];
                data.SystemPrompt = t["system"];
                data.CreatorNotes = t["notes"];
                data.PostHistoryInstructions = t["posthist"];
                data.Extensions = ext ?? new Dictionary<string, string>();
                data.Attributes = new Dictionary<SS.PlayerAttribute, long>(_draft.attrs);
                data.LongTermMemories = new List<string>(_draft.memories);

                NPCData.Save(_npc.uuid, data);
                NPCData.SyncToNativeImportantData(_npc, data.Personality, data.Scenario, data.Description);
                NPCData.FlushSessionLore();
                NPCUI.RefreshAll();

                LoadDraft();
                _pendingAfterConfirm = null;
                _footerError = null;
                _savedFlash = true;
                Debug.Log($"[NPCDossier] Saved dossier edits for {_npc.GetPrettyName()}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[NPCDossier] Save failed: " + ex);
                _footerError = "Saving failed. Details are in the BepInEx log.";
                _pendingAfterConfirm = null;
                BuildFooter();
                return false;
            }
        }

        private static List<string> SplitLines(string s) =>
            (s ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        // ─── Actions ──────────────────────────────────────────────────────────────

        /// <summary>Finds one of the game's own NPC actions by its localized label key ("talk", "gift", "trade").</summary>
        private StrToAction NativeAction(string locKey)
        {
            try
            {
                string label = LS.I.GetLocStr(locKey);
                return _manager.GetActions(_npc, respectInteractionsDisabled: true)?.FirstOrDefault(a => a != null && a.str == label);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCDossier] Could not read native '{locKey}' action: {ex.Message}");
                return null;
            }
        }

        private async void RunNativeAction(StrToAction action)
        {
            if (action?.a == null) return;
            if (IsDirty) { ConfirmThen(() => RunNativeAction(action)); return; }
            CloseNow();
            try { await action.a(); }
            catch (Exception ex) { Debug.LogWarning("[NPCDossier] Native action failed: " + ex); }
        }

        private string TeachBlocker(NPCData data)
        {
            if (data == null || !NPCData.HasProfile(_npc, data)) return "Needs a profile first";
            if (data.Affinity < 75) return "Needs Confidant (75 affinity)";
            var taught = NPCTeachingSystem.PlayerTaughtSkills?.FirstOrDefault(s => s.TeacherName == _npc.GetPrettyName());
            if (taught != null) return $"Already taught you {taught.SkillName}";
            return null;
        }

        private async void DoTeach()
        {
            var npc = _npc; var data = Data;
            if (npc == null || data == null || _busy.Contains("teach")) return;
            _busy.Add("teach");
            BuildRail();
            try { await NPCTeachingSystem.TeachPlayer(npc, data, _manager); }
            finally
            {
                _busy.Remove("teach");
                if (IsOpen && _npc == npc) { if (!IsDirty) LoadDraft(); Refresh(); }
            }
        }

        private async void DoAskSecret()
        {
            var npc = _npc; var data = Data;
            if (npc == null || data == null || _busy.Contains("secret")) return;
            _busy.Add("secret");
            BuildContent();
            try { await NPCSecretSystem.TryRevealSecret(npc, data, _manager); }
            finally
            {
                _busy.Remove("secret");
                if (IsOpen && _npc == npc) Refresh();
            }
        }

        private async void DoGenerate()
        {
            var npc = _npc;
            if (npc == null || _busy.Contains("generate")) return;
            _busy.Add("generate");
            BuildContent();
            bool ok = false;
            try { ok = await NPCGenerator.GenerateLore(npc, _manager.GetContextForQuickActions()); }
            catch (Exception ex) { Debug.LogWarning("[NPCDossier] Generation failed: " + ex.Message); }
            finally
            {
                _busy.Remove("generate");
                if (IsOpen && _npc == npc)
                {
                    if (ok) { LoadDraft(); _savedFlash = false; }
                    else _footerError = "Profile generation failed. Check your AI connection and try again.";
                    Refresh();
                }
            }
        }

        private async void DoRequestQuest()
        {
            var npc = _npc; var data = Data;
            if (npc == null || data == null || _busy.Contains("quest")) return;
            _busy.Add("quest");
            BuildContent();
            try { await QuestManager.GenerateQuest(npc, data, _manager); }
            catch (Exception ex) { Debug.LogWarning("[NPCDossier] Quest request failed: " + ex.Message); }
            finally
            {
                _busy.Remove("quest");
                if (IsOpen && _npc == npc) Refresh();
            }
        }

        private List<QuestData> QuestsForNpc()
        {
            if (_npc == null || QuestManager.AllQuests == null) return new List<QuestData>();
            return QuestManager.AllQuests
                .Where(q => q != null && q.GiverId == _npc.uuid)
                .OrderBy(q => q.Status == QuestStatus.Active ? 0 : 1)
                .ThenByDescending(q => q.TurnGiven)
                .ToList();
        }
    }
}
