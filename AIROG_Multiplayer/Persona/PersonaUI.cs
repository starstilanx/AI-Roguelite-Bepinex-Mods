using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Multiplayer.Persona
{
    /// <summary>
    /// Modal persona selector/editor panel.
    /// Call PersonaUI.Show(onApply) to open; onApply receives the chosen PersonaData.
    /// </summary>
    public class PersonaUI : MonoBehaviour
    {
        public static PersonaUI Instance { get; private set; }

        // Callback when the user clicks "Apply to Lobby"
        private Action<PersonaData> _onApply;

        // Left-pane persona list entries
        private Transform _listContent;
        private readonly List<(string id, Button btn, TMP_Text lbl)> _listItems
            = new List<(string, Button, TMP_Text)>();

        // Currently editing
        private PersonaData _current;

        // Right-pane editor fields
        private TMP_InputField _fPersonaName;
        private TMP_InputField _fCharName;
        private TMP_InputField _fClass;
        private TMP_InputField _fAge;
        private TMP_InputField _fBuild;
        private TMP_InputField _fDescription;
        private TMP_InputField _fBackground;
        private TMP_InputField _fPersonality;
        private TMP_InputField _fAppearance;
        private TMP_InputField _fHp;
        private TMP_InputField _fMaxHp;
        private TMP_InputField _fLevel;

        // Status / toast text
        private TMP_Text _statusText;
        private float _statusClearAt;

        // Import sub-panel
        private GameObject _importPanel;
        private TMP_InputField _importTextArea;

        // ───────────────────────────────────────────────────────────
        //  Public entry point
        // ───────────────────────────────────────────────────────────

        public static void Show(Action<PersonaData> onApply)
        {
            if (Instance != null)
            {
                Instance.gameObject.SetActive(true);
                Instance._onApply = onApply;
                Instance.RefreshList();
                return;
            }

            PersonaManager.Load();

            var go = new GameObject("AIROG_PersonaUI");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Instance = go.AddComponent<PersonaUI>();
            Instance._onApply = onApply;
            Instance.BuildUI();
        }

        public static void Hide()
        {
            Instance?.gameObject.SetActive(false);
        }

        // ───────────────────────────────────────────────────────────
        //  Unity
        // ───────────────────────────────────────────────────────────

        private void Update()
        {
            if (_statusClearAt > 0 && Time.realtimeSinceStartup > _statusClearAt)
            {
                _statusClearAt = 0;
                if (_statusText != null) _statusText.text = "";
            }
        }

        // ───────────────────────────────────────────────────────────
        //  Build UI
        // ───────────────────────────────────────────────────────────

        private void BuildUI()
        {
            // Root canvas
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            // Dark full-screen backdrop
            var overlay = MakePanel(transform, "Overlay", new Color(0, 0, 0, 0.78f));
            StretchFull(overlay.GetComponent<RectTransform>());

            // ── Main dialog ──────────────────────────────────────────
            // 860 wide, 700 tall
            var dialog = MakePanel(overlay.transform, "Dialog", new Color(0.10f, 0.10f, 0.16f, 0.98f));
            var dialogRT = dialog.GetComponent<RectTransform>();
            dialogRT.anchorMin = dialogRT.anchorMax = dialogRT.pivot = new Vector2(0.5f, 0.5f);
            dialogRT.sizeDelta = new Vector2(860, 700);
            var outline = dialog.AddComponent<Outline>();
            outline.effectColor = new Color(0.4f, 0.6f, 1f, 0.8f);
            outline.effectDistance = new Vector2(2, 2);

            var dlg = dialog.transform;

            // Title bar row
            var titleBar = MakeHorizRow(dlg, "TitleBar", new Color(0.06f, 0.06f, 0.12f, 1f),
                new Vector2(0, 330), new Vector2(860, 44));
            MakeLabel(titleBar.transform, "Title", "👤  Persona Selector", 20,
                FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft, new Vector2(16, 0));
            // Close button top-right
            MakeSmallButton(dlg, "CloseBtn", "✕", new Vector2(395, 330), new Vector2(32, 32),
                new Color(0.55f, 0.15f, 0.15f), () => Hide());

            // ── Left pane — persona list (200px) ─────────────────────
            var leftBg = MakePanel(dlg, "LeftPane", new Color(0.07f, 0.07f, 0.11f, 1f));
            var leftRT = leftBg.GetComponent<RectTransform>();
            leftRT.anchorMin = new Vector2(0, 0);
            leftRT.anchorMax = new Vector2(0, 1);
            leftRT.pivot = new Vector2(0, 0.5f);
            leftRT.offsetMin = new Vector2(0, 0);
            leftRT.offsetMax = new Vector2(200, -44);  // below title bar

            // Top buttons: [+ New] [📥 Import]
            var leftTopRow = MakeHorizRow(leftBg.transform, "LeftTop",
                new Color(0.08f, 0.08f, 0.14f, 1f), Vector2.zero, new Vector2(200, 38));
            AnchorTopStretch(leftTopRow.GetComponent<RectTransform>(), 200, 38);

            MakeSmallButton(leftTopRow.transform, "NewBtn", "+ New", Vector2.zero,
                new Vector2(92, 30), new Color(0.18f, 0.52f, 0.22f), OnNewClicked);
            MakeSmallButton(leftTopRow.transform, "ImportBtn", "📥 Import", Vector2.zero,
                new Vector2(96, 30), new Color(0.28f, 0.38f, 0.58f), OnImportClicked);

            // Scroll list
            var (scrollGo, content) = MakeScrollList(leftBg.transform, "PersonaList",
                new Vector2(0, -38), new Vector2(200, -38));
            _listContent = content.transform;

            // ── Right pane — editor ──────────────────────────────────
            var rightBg = MakePanel(dlg, "RightPane", new Color(0.09f, 0.09f, 0.13f, 1f));
            var rightRT = rightBg.GetComponent<RectTransform>();
            rightRT.anchorMin = new Vector2(0, 0);
            rightRT.anchorMax = new Vector2(1, 1);
            rightRT.pivot = new Vector2(0, 0.5f);
            rightRT.offsetMin = new Vector2(204, 0);
            rightRT.offsetMax = new Vector2(0, -44);

            // Status strip at very bottom of right pane
            var statusStrip = MakePanel(rightBg.transform, "StatusStrip",
                new Color(0.05f, 0.05f, 0.08f, 1f));
            var ssRT = statusStrip.GetComponent<RectTransform>();
            ssRT.anchorMin = new Vector2(0, 0);
            ssRT.anchorMax = new Vector2(1, 0);
            ssRT.pivot = new Vector2(0.5f, 0f);
            ssRT.offsetMin = Vector2.zero;
            ssRT.offsetMax = new Vector2(0, 32);
            _statusText = MakeLabel(statusStrip.transform, "StatusText", "", 11,
                FontStyles.Normal, new Color(0.5f, 0.9f, 0.5f), TextAlignmentOptions.MidlineLeft,
                new Vector2(8, 0));
            StretchFull(_statusText.GetComponent<RectTransform>());

            // Scrollable editor area (above status strip)
            var (editorScroll, editorContent) = MakeScrollList(rightBg.transform, "EditorScroll",
                new Vector2(0, 32), new Vector2(0, 32));

            BuildEditorForm(editorContent.transform);

            // Action buttons row (pinned above status strip)
            var actionRow = MakePanel(rightBg.transform, "ActionRow", new Color(0.07f, 0.07f, 0.10f, 1f));
            var arRT = actionRow.GetComponent<RectTransform>();
            arRT.anchorMin = new Vector2(0, 0);
            arRT.anchorMax = new Vector2(1, 0);
            arRT.pivot = new Vector2(0.5f, 0f);
            arRT.offsetMin = new Vector2(0, 32);
            arRT.offsetMax = new Vector2(0, 78);
            var arHlg = actionRow.AddComponent<HorizontalLayoutGroup>();
            arHlg.childAlignment = TextAnchor.MiddleCenter;
            arHlg.spacing = 10;
            arHlg.padding = new RectOffset(8, 8, 4, 4);
            arHlg.childForceExpandWidth = false;

            AddLayoutButton(actionRow.transform, "SaveBtn", "💾 Save",
                new Color(0.18f, 0.52f, 0.22f), 130, OnSaveClicked);
            AddLayoutButton(actionRow.transform, "ExportBtn", "📤 Export",
                new Color(0.30f, 0.42f, 0.62f), 130, OnExportClicked);
            AddLayoutButton(actionRow.transform, "ApplyBtn", "✓ Apply to Lobby",
                new Color(0.55f, 0.35f, 0.08f), 160, OnApplyClicked);
            AddLayoutButton(actionRow.transform, "DeleteBtn", "🗑 Delete",
                new Color(0.50f, 0.12f, 0.12f), 100, OnDeleteClicked);

            // ── Import sub-panel (hidden by default) ─────────────────
            BuildImportPanel(dlg);

            // Populate list
            RefreshList();
        }

        private void BuildEditorForm(Transform parent)
        {
            // Use a VerticalLayoutGroup inside the scroll content
            var vlg = parent.GetComponent<VerticalLayoutGroup>();
            if (vlg == null) vlg = parent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.spacing = 6;
            vlg.padding = new RectOffset(10, 10, 10, 10);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var csf = parent.GetComponent<ContentSizeFitter>();
            if (csf == null) csf = parent.gameObject.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Color labelColor = new Color(0.75f, 0.80f, 0.95f);

            _fPersonaName   = AddField(parent, "Persona Slot Name:", labelColor, "", false, 30);
            _fCharName      = AddField(parent, "Character Name:", labelColor, "", false, 30);
            _fClass         = AddField(parent, "Class / Role:", labelColor, "", false, 30);

            // Age + Build on one row
            var row = AddHorizFieldRow(parent, labelColor,
                new[] { ("Age:", 80f), ("Build:", 200f) });
            _fAge   = row[0];
            _fBuild = row[1];

            _fDescription   = AddField(parent, "Description:", labelColor, "", true, 60);
            _fBackground    = AddField(parent, "Background:", labelColor, "", true, 70);
            _fPersonality   = AddField(parent, "Personality / Goals:", labelColor, "", true, 60);
            _fAppearance    = AddField(parent, "Physical Appearance:", labelColor, "", true, 60);

            // HP / MaxHP / Level row
            var statsRow = AddHorizFieldRow(parent, labelColor,
                new[] { ("HP:", 70f), ("Max HP:", 70f), ("Level:", 60f) });
            _fHp    = statsRow[0];
            _fMaxHp = statsRow[1];
            _fLevel = statsRow[2];
        }

        private void BuildImportPanel(Transform dlg)
        {
            _importPanel = MakePanel(dlg, "ImportPanel", new Color(0.08f, 0.08f, 0.14f, 0.98f));
            var ipRT = _importPanel.GetComponent<RectTransform>();
            ipRT.anchorMin = ipRT.anchorMax = ipRT.pivot = new Vector2(0.5f, 0.5f);
            ipRT.sizeDelta = new Vector2(580, 400);
            var ipOutline = _importPanel.AddComponent<Outline>();
            ipOutline.effectColor = new Color(0.4f, 0.7f, 1f, 0.9f);
            ipOutline.effectDistance = new Vector2(2, 2);

            MakeLabel(_importPanel.transform, "ImpTitle", "📥  Import Persona", 16,
                FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft,
                new Vector2(14, 0));
            // We'll anchor the title at top
            var titleGo = _importPanel.transform.Find("ImpTitle");
            var titleRT = titleGo?.GetComponent<RectTransform>();
            if (titleRT != null)
            {
                titleRT.anchorMin = new Vector2(0, 1);
                titleRT.anchorMax = new Vector2(1, 1);
                titleRT.pivot = new Vector2(0.5f, 1f);
                titleRT.offsetMin = new Vector2(0, -44);
                titleRT.offsetMax = new Vector2(0, 0);
            }

            MakeLabel(_importPanel.transform, "ImpHint",
                "Paste the contents of a .persona.json file below:", 12,
                FontStyles.Normal, new Color(0.7f, 0.85f, 1f), TextAlignmentOptions.MidlineLeft,
                new Vector2(14, 0));
            var hintGo = _importPanel.transform.Find("ImpHint");
            var hintRT = hintGo?.GetComponent<RectTransform>();
            if (hintRT != null)
            {
                hintRT.anchorMin = new Vector2(0, 1);
                hintRT.anchorMax = new Vector2(1, 1);
                hintRT.pivot = new Vector2(0.5f, 1f);
                hintRT.offsetMin = new Vector2(10, -72);
                hintRT.offsetMax = new Vector2(-10, -44);
            }

            // Text area
            var taGo = new GameObject("ImportTextArea");
            taGo.transform.SetParent(_importPanel.transform, false);
            var taBg = taGo.AddComponent<Image>();
            taBg.color = new Color(0.05f, 0.05f, 0.08f, 1f);
            var taOutline = taGo.AddComponent<Outline>();
            taOutline.effectColor = new Color(0.3f, 0.5f, 0.8f, 0.6f);
            var taRT = taGo.GetComponent<RectTransform>();
            taRT.anchorMin = new Vector2(0, 0);
            taRT.anchorMax = new Vector2(1, 1);
            taRT.pivot = new Vector2(0.5f, 0.5f);
            taRT.offsetMin = new Vector2(10, 52);
            taRT.offsetMax = new Vector2(-10, -76);

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(taGo.transform, false);
            viewport.AddComponent<RectMask2D>();
            var vpRT = viewport.GetComponent<RectTransform>();
            vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
            vpRT.offsetMin = new Vector2(4, 4); vpRT.offsetMax = new Vector2(-4, -4);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(viewport.transform, false);
            var textComp = textGo.AddComponent<TextMeshProUGUI>();
            textComp.fontSize = 11;
            textComp.color = Color.white;
            var textRT = textGo.GetComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;

            _importTextArea = taGo.AddComponent<TMP_InputField>();
            _importTextArea.textViewport = vpRT;
            _importTextArea.textComponent = textComp;
            _importTextArea.lineType = TMP_InputField.LineType.MultiLineNewline;
            textComp.alignment = TextAlignmentOptions.TopLeft;
            _importTextArea.text = "";

            // Buttons row at bottom
            var btnRowGo = new GameObject("BtnRow");
            btnRowGo.transform.SetParent(_importPanel.transform, false);
            var btnRT = btnRowGo.GetComponent<RectTransform>();
            if (btnRT == null) btnRT = btnRowGo.AddComponent<RectTransform>();
            btnRT.anchorMin = new Vector2(0, 0);
            btnRT.anchorMax = new Vector2(1, 0);
            btnRT.pivot = new Vector2(0.5f, 0f);
            btnRT.offsetMin = new Vector2(10, 8);
            btnRT.offsetMax = new Vector2(-10, 48);
            var btnHlg = btnRowGo.AddComponent<HorizontalLayoutGroup>();
            btnHlg.childAlignment = TextAnchor.MiddleCenter;
            btnHlg.spacing = 12;
            btnHlg.childForceExpandWidth = false;
            btnHlg.childForceExpandHeight = false;

            AddLayoutButton(btnRowGo.transform, "ImpConfirm", "✓ Import",
                new Color(0.18f, 0.52f, 0.22f), 130, OnImportConfirmed);
            AddLayoutButton(btnRowGo.transform, "ImpCancel", "✕ Cancel",
                new Color(0.45f, 0.12f, 0.12f), 110, () => _importPanel.SetActive(false));

            _importPanel.SetActive(false);
        }

        // ───────────────────────────────────────────────────────────
        //  List management
        // ───────────────────────────────────────────────────────────

        private void RefreshList()
        {
            foreach (Transform child in _listContent) Destroy(child.gameObject);
            _listItems.Clear();

            foreach (var p in PersonaManager.All)
            {
                var capture = p;
                var itemGo = new GameObject("Item_" + p.Id);
                itemGo.transform.SetParent(_listContent, false);

                var img = itemGo.AddComponent<Image>();
                img.color = new Color(0.10f, 0.10f, 0.16f, 1f);

                var le = itemGo.AddComponent<LayoutElement>();
                le.preferredHeight = 40;
                le.flexibleWidth = 1;

                var btn = itemGo.AddComponent<Button>();
                var cs = btn.colors;
                cs.highlightedColor = new Color(0.20f, 0.20f, 0.30f);
                cs.pressedColor = new Color(0.15f, 0.35f, 0.55f);
                cs.selectedColor = new Color(0.18f, 0.28f, 0.48f);
                btn.colors = cs;
                btn.onClick.AddListener(() => LoadPersonaIntoEditor(capture));

                var lbl = MakeLabel(itemGo.transform, "Lbl", p.PersonaName, 12,
                    FontStyles.Normal, Color.white, TextAlignmentOptions.MidlineLeft,
                    new Vector2(10, 0));
                StretchFull(lbl.GetComponent<RectTransform>());
                lbl.raycastTarget = false;

                _listItems.Add((p.Id, btn, lbl));
            }
        }

        private void HighlightListItem(string id)
        {
            foreach (var (itemId, btn, lbl) in _listItems)
            {
                var img = btn.GetComponent<Image>();
                img.color = itemId == id
                    ? new Color(0.18f, 0.28f, 0.48f)
                    : new Color(0.10f, 0.10f, 0.16f, 1f);
            }
        }

        // ───────────────────────────────────────────────────────────
        //  Editor load / apply
        // ───────────────────────────────────────────────────────────

        private void LoadPersonaIntoEditor(PersonaData p)
        {
            _current = p;
            HighlightListItem(p.Id);

            _fPersonaName.text  = p.PersonaName;
            _fCharName.text     = p.CharacterName;
            _fClass.text        = p.CharacterClass;
            _fAge.text          = p.Age;
            _fBuild.text        = p.Build;
            _fDescription.text  = p.Description;
            _fBackground.text   = p.Background;
            _fPersonality.text  = p.Personality;
            _fAppearance.text   = p.PhysicalAppearance;
            _fHp.text           = p.DefaultHp.ToString();
            _fMaxHp.text        = p.DefaultMaxHp.ToString();
            _fLevel.text        = p.DefaultLevel.ToString();
        }

        private PersonaData CollectEditorValues()
        {
            if (_current == null) _current = new PersonaData();
            _current.PersonaName       = _fPersonaName.text.Trim();
            _current.CharacterName     = _fCharName.text.Trim();
            _current.CharacterClass    = _fClass.text.Trim();
            _current.Age               = _fAge.text.Trim();
            _current.Build             = _fBuild.text.Trim();
            _current.Description       = _fDescription.text.Trim();
            _current.Background        = _fBackground.text.Trim();
            _current.Personality       = _fPersonality.text.Trim();
            _current.PhysicalAppearance = _fAppearance.text.Trim();
            long.TryParse(_fHp.text,    out long hp);    _current.DefaultHp    = hp > 0 ? hp : 100;
            long.TryParse(_fMaxHp.text, out long maxHp); _current.DefaultMaxHp = maxHp > 0 ? maxHp : 100;
            int.TryParse(_fLevel.text,  out int lvl);    _current.DefaultLevel = lvl > 0 ? lvl : 1;
            return _current;
        }

        // ───────────────────────────────────────────────────────────
        //  Button handlers
        // ───────────────────────────────────────────────────────────

        private void OnNewClicked()
        {
            var p = new PersonaData { PersonaName = "New Persona" };
            PersonaManager.AddOrUpdate(p);
            RefreshList();
            LoadPersonaIntoEditor(p);
        }

        private void OnSaveClicked()
        {
            if (_current == null) { ShowStatus("Select or create a persona first.", Color.yellow); return; }
            CollectEditorValues();
            if (string.IsNullOrWhiteSpace(_current.PersonaName))
            { ShowStatus("Persona Slot Name cannot be empty.", new Color(1f, 0.4f, 0.4f)); return; }
            PersonaManager.AddOrUpdate(_current);
            RefreshList();
            HighlightListItem(_current.Id);
            ShowStatus("Saved!", new Color(0.4f, 1f, 0.5f));
        }

        private void OnExportClicked()
        {
            if (_current == null) { ShowStatus("Select a persona first.", Color.yellow); return; }
            CollectEditorValues();
            string path = PersonaManager.Export(_current);
            if (path != null) ShowStatus($"Exported to: {path}", new Color(0.5f, 0.9f, 1f), 6f);
            else ShowStatus("Export failed.", new Color(1f, 0.4f, 0.4f));
        }

        private void OnApplyClicked()
        {
            if (_current == null) { ShowStatus("Select a persona first.", Color.yellow); return; }
            CollectEditorValues();
            _onApply?.Invoke(_current);
            Hide();
        }

        private void OnDeleteClicked()
        {
            if (_current == null) { ShowStatus("Nothing selected.", Color.yellow); return; }
            PersonaManager.Delete(_current.Id);
            _current = null;
            RefreshList();
            ClearEditorFields();
            ShowStatus("Deleted.", new Color(1f, 0.6f, 0.4f));
        }

        private void OnImportClicked()
        {
            _importTextArea.text = "";
            _importPanel.SetActive(true);
        }

        private void OnImportConfirmed()
        {
            string json = _importTextArea.text.Trim();
            if (string.IsNullOrEmpty(json))
            { ShowStatus("Paste JSON into the text area first.", Color.yellow); return; }

            var persona = PersonaManager.ImportFromJson(json);
            if (persona == null)
            { ShowStatus("Invalid JSON — could not parse persona.", new Color(1f, 0.4f, 0.4f)); return; }

            PersonaManager.AddOrUpdate(persona);
            RefreshList();
            LoadPersonaIntoEditor(persona);
            _importPanel.SetActive(false);
            ShowStatus($"Imported: {persona.PersonaName}", new Color(0.4f, 1f, 0.5f));
        }

        // ───────────────────────────────────────────────────────────
        //  Helpers
        // ───────────────────────────────────────────────────────────

        private void ClearEditorFields()
        {
            _fPersonaName.text = ""; _fCharName.text = ""; _fClass.text = "";
            _fAge.text = ""; _fBuild.text = ""; _fDescription.text = "";
            _fBackground.text = ""; _fPersonality.text = ""; _fAppearance.text = "";
            _fHp.text = "100"; _fMaxHp.text = "100"; _fLevel.text = "1";
        }

        private void ShowStatus(string msg, Color color, float duration = 3f)
        {
            if (_statusText == null) return;
            _statusText.text = msg;
            _statusText.color = color;
            _statusClearAt = Time.realtimeSinceStartup + duration;
        }

        // ──────────────────────────────────────────────────────────
        //  UI builder helpers
        // ──────────────────────────────────────────────────────────

        private GameObject MakePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            go.AddComponent<RectTransform>();
            return go;
        }

        private TMP_Text MakeLabel(Transform parent, string name, string text, float size,
            FontStyles style, Color color, TextAlignmentOptions align, Vector2 offset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lbl = go.AddComponent<TextMeshProUGUI>();
            lbl.text = text; lbl.fontSize = size; lbl.fontStyle = style;
            lbl.color = color; lbl.alignment = align;
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = offset;
            return lbl;
        }

        private GameObject MakeHorizRow(Transform parent, string name, Color color,
            Vector2 pos, Vector2 size)
        {
            var go = MakePanel(parent, name, color);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.spacing = 6; hlg.padding = new RectOffset(6, 6, 4, 4);
            hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
            return go;
        }

        private void MakeSmallButton(Transform parent, string name, string label,
            Vector2 pos, Vector2 size, Color color, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            var btn = go.AddComponent<Button>();
            var cs = btn.colors;
            cs.highlightedColor = color * 1.2f; cs.pressedColor = color * 0.8f;
            btn.colors = cs;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var lbl = new GameObject("Lbl");
            lbl.transform.SetParent(go.transform, false);
            var t = lbl.AddComponent<TextMeshProUGUI>();
            t.text = label; t.fontSize = 12; t.fontStyle = FontStyles.Bold;
            t.color = Color.white; t.alignment = TextAlignmentOptions.Center;
            var tRT = lbl.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero; tRT.offsetMax = Vector2.zero;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;

            // If inside a layout group, swap to LayoutElement so it sizes correctly
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = size.x; le.preferredHeight = size.y;
        }

        private void AddLayoutButton(Transform parent, string name, string label,
            Color color, float width, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            var btn = go.AddComponent<Button>();
            var cs = btn.colors;
            cs.highlightedColor = color * 1.2f; cs.pressedColor = color * 0.8f;
            btn.colors = cs;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.preferredHeight = 34;

            var lbl = new GameObject("Lbl");
            lbl.transform.SetParent(go.transform, false);
            var t = lbl.AddComponent<TextMeshProUGUI>();
            t.text = label; t.fontSize = 13; t.fontStyle = FontStyles.Bold;
            t.color = Color.white; t.alignment = TextAlignmentOptions.Center;
            var tRT = lbl.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero; tRT.offsetMax = Vector2.zero;
        }

        private (GameObject scroll, GameObject content) MakeScrollList(Transform parent,
            string name, Vector2 offsetMin, Vector2 offsetMax)
        {
            var scrollGo = new GameObject(name);
            scrollGo.transform.SetParent(parent, false);
            scrollGo.AddComponent<Image>().color = Color.clear;
            scrollGo.AddComponent<RectMask2D>();
            var scrollRT = scrollGo.GetComponent<RectTransform>();
            scrollRT.anchorMin = Vector2.zero; scrollRT.anchorMax = Vector2.one;
            scrollRT.offsetMin = offsetMin; scrollRT.offsetMax = offsetMax;

            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 20f;

            var content = new GameObject("Content");
            content.transform.SetParent(scrollGo.transform, false);
            var contentRT = content.GetComponent<RectTransform>();
            if (contentRT == null) contentRT = content.AddComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0, 1);
            contentRT.anchorMax = new Vector2(1, 1);
            contentRT.pivot = new Vector2(0.5f, 1f);
            contentRT.offsetMin = Vector2.zero;
            contentRT.offsetMax = Vector2.zero;

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.spacing = 3; vlg.padding = new RectOffset(4, 4, 4, 4);
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;

            var csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = contentRT;

            return (scrollGo, content);
        }

        /// <summary>Add a labeled single-line or multi-line input to a VerticalLayoutGroup parent.</summary>
        private TMP_InputField AddField(Transform parent, string labelText, Color labelColor,
            string defaultVal, bool multiline, float fieldHeight)
        {
            // Label
            var lblGo = new GameObject("Lbl_" + labelText);
            lblGo.transform.SetParent(parent, false);
            var lbl = lblGo.AddComponent<TextMeshProUGUI>();
            lbl.text = labelText; lbl.fontSize = 11; lbl.color = labelColor;
            lbl.alignment = TextAlignmentOptions.MidlineLeft;
            var lblLE = lblGo.AddComponent<LayoutElement>();
            lblLE.preferredHeight = 18;

            // Input
            var fieldGo = new GameObject("Field_" + labelText);
            fieldGo.transform.SetParent(parent, false);
            fieldGo.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.10f, 1f);
            var fo = fieldGo.AddComponent<Outline>();
            fo.effectColor = new Color(0.3f, 0.5f, 0.8f, 0.5f);

            var fieldLE = fieldGo.AddComponent<LayoutElement>();
            fieldLE.preferredHeight = fieldHeight;

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(fieldGo.transform, false);
            viewport.AddComponent<RectMask2D>();
            var vpRT = viewport.GetComponent<RectTransform>();
            vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
            vpRT.offsetMin = new Vector2(5, 2); vpRT.offsetMax = new Vector2(-5, -2);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(viewport.transform, false);
            var textComp = textGo.AddComponent<TextMeshProUGUI>();
            textComp.fontSize = 12; textComp.color = Color.white;
            if (multiline) textComp.alignment = TextAlignmentOptions.TopLeft;
            var textRT = textGo.GetComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;

            var inputField = fieldGo.AddComponent<TMP_InputField>();
            inputField.textViewport = vpRT;
            inputField.textComponent = textComp;
            inputField.text = defaultVal;
            if (multiline)
                inputField.lineType = TMP_InputField.LineType.MultiLineNewline;

            return inputField;
        }

        /// <summary>Add a horizontal row of labelled fields (label+input pairs) to a VLG parent.</summary>
        private TMP_InputField[] AddHorizFieldRow(Transform parent, Color labelColor,
            (string label, float width)[] fields)
        {
            var rowGo = new GameObject("HorizRow");
            rowGo.transform.SetParent(parent, false);
            var rowImg = rowGo.AddComponent<Image>();
            rowImg.color = Color.clear;
            var rowHlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowHlg.childAlignment = TextAnchor.MiddleLeft;
            rowHlg.spacing = 10; rowHlg.padding = new RectOffset(0, 0, 0, 0);
            rowHlg.childForceExpandWidth = false; rowHlg.childForceExpandHeight = false;
            var rowLE = rowGo.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 52;

            var results = new TMP_InputField[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var (lText, fWidth) = fields[i];

                var cellGo = new GameObject("Cell_" + lText);
                cellGo.transform.SetParent(rowGo.transform, false);
                cellGo.AddComponent<Image>().color = Color.clear;
                var cellVlg = cellGo.AddComponent<VerticalLayoutGroup>();
                cellVlg.childAlignment = TextAnchor.UpperLeft;
                cellVlg.spacing = 2;
                cellVlg.childForceExpandWidth = true; cellVlg.childForceExpandHeight = false;
                var cellLE = cellGo.AddComponent<LayoutElement>();
                cellLE.preferredWidth = fWidth; cellLE.preferredHeight = 52;

                var lblGo = new GameObject("Lbl");
                lblGo.transform.SetParent(cellGo.transform, false);
                var lbl = lblGo.AddComponent<TextMeshProUGUI>();
                lbl.text = lText; lbl.fontSize = 11; lbl.color = labelColor;
                lbl.alignment = TextAlignmentOptions.MidlineLeft;
                var lblLE2 = lblGo.AddComponent<LayoutElement>();
                lblLE2.preferredHeight = 18;

                var fieldGo = new GameObject("Field");
                fieldGo.transform.SetParent(cellGo.transform, false);
                fieldGo.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.10f, 1f);
                var fo = fieldGo.AddComponent<Outline>();
                fo.effectColor = new Color(0.3f, 0.5f, 0.8f, 0.5f);
                var fieldLE = fieldGo.AddComponent<LayoutElement>();
                fieldLE.preferredHeight = 28;

                var viewport = new GameObject("Viewport");
                viewport.transform.SetParent(fieldGo.transform, false);
                viewport.AddComponent<RectMask2D>();
                var vpRT = viewport.GetComponent<RectTransform>();
                vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
                vpRT.offsetMin = new Vector2(4, 2); vpRT.offsetMax = new Vector2(-4, -2);

                var textGo = new GameObject("Text");
                textGo.transform.SetParent(viewport.transform, false);
                var textComp = textGo.AddComponent<TextMeshProUGUI>();
                textComp.fontSize = 12; textComp.color = Color.white;
                var textRT = textGo.GetComponent<RectTransform>();
                textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
                textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;

                var inputField = fieldGo.AddComponent<TMP_InputField>();
                inputField.textViewport = vpRT;
                inputField.textComponent = textComp;

                results[i] = inputField;
            }
            return results;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private static void AnchorTopStretch(RectTransform rt, float width, float height)
        {
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(0, height);
        }
    }
}
