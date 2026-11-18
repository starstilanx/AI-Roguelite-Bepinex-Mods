using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AIROG_GenContext.DMNotes
{
    public static class DmNotesPanel
    {
        private static GameObject _panelObj;
        private static bool _isOpen = false;

        // Analysis section labels
        private static TextMeshProUGUI _stateLabel;
        private static TextMeshProUGUI _pacingLabel;
        private static TextMeshProUGUI _engagementLabel;

        // List containers (rebuilt on refresh)
        private static Transform _plotListContent;
        private static Transform _prefListContent;
        private static Transform _plannedListContent;
        private static Transform _introducedListContent;

        // Player steering. The input fields are built once and survive refreshes, so a refresh
        // triggered by the AI's notes arriving cannot eat what the player is typing.
        private static TMP_InputField _directionInput;
        private static TextMeshProUGUI _plannedHeader;
        private static GameObject _introducedHeader;
        private static TMP_InputField _threadInput;
        private static TMP_InputField _notBeforeInput;
        private static TMP_InputField _byInput;
        private static TextMeshProUGUI _formHint;
        private static TextMeshProUGUI _submitLabel;
        private static GameObject _cancelEditBtn;
        private static string _editingId; // null = the form adds a new thread

        private static readonly Color HintColor = new Color(0.6f, 0.6f, 0.65f);
        private const string FormHintDefault = "Windows are optional, in turns from when the thread is added.";

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts
        {
            [HarmonyPostfix]
            public static void Postfix(MainLayouts __instance)
            {
                // InitCommonAnchs runs inside the game's startup; a failure here must not stop it.
                try
                {
                    DmNotesManager.DetachIfSaveChanged();
                    // The game clears TurnHappenedEvent when it exits to the main menu, so the
                    // subscription has to be renewed for every game scene.
                    GameplayManager.TurnHappenedEvent -= DmNotesManager.OnTurnHappened;
                    GameplayManager.TurnHappenedEvent += DmNotesManager.OnTurnHappened;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GenContext] DM Notes scene setup failed: {ex}");
                }

                try
                {
                    Create(__instance);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GenContext] DM Notes panel could not be built: {ex}");
                    // Create hides the panel as its last step; one that failed half-way would
                    // be left showing over the game with no way to close it.
                    if (_panelObj != null) UnityEngine.Object.Destroy(_panelObj);
                    _panelObj = null;
                }
            }
        }

        public static void Create(MainLayouts layout)
        {
            if (layout == null || layout.mainHolder == null) return;
            if (layout.mainHolder.Find("DmNotesPanel") != null) return;

            // A new scene's panel starts closed. Left over as "open" from the previous scene, this
            // flag made the first click on the DM button do nothing.
            _isOpen = false;
            _editingId = null;

            // ---- Root panel ----
            _panelObj = new GameObject("DmNotesPanel", typeof(RectTransform), typeof(Image));
            _panelObj.transform.SetParent(layout.mainHolder, false);

            var root = _panelObj.GetComponent<RectTransform>();
            // Anchor: top-right corner area, 260px wide, tall
            root.anchorMin = new Vector2(0.738f, 0.014f);  // x≈756/1024, y≈8/559
            root.anchorMax = new Vector2(0.992f, 0.986f);  // x≈1016/1024, y≈551/559
            root.offsetMin = root.offsetMax = Vector2.zero;

            var bg = _panelObj.GetComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.10f, 0.92f);

            // ---- Header ----
            GameObject header = MakeRect("Header", _panelObj.transform);
            var headerRect = header.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 0.95f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.offsetMin = headerRect.offsetMax = Vector2.zero;
            header.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.18f, 1f);

            var titleObj = MakeText("Title", header.transform, "DM Director", 14, FontStyles.Bold,
                new Color(0.95f, 0.85f, 0.5f), TextAlignmentOptions.Left);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.04f, 0f);
            titleRect.anchorMax = new Vector2(0.78f, 1f);
            titleRect.offsetMin = titleRect.offsetMax = Vector2.zero;

            // Close button
            GameObject closeBtn = MakeRect("CloseBtn", header.transform, typeof(Image), typeof(Button));
            closeBtn.GetComponent<Image>().color = Color.clear;
            closeBtn.GetComponent<Button>().onClick.AddListener(Toggle);
            var closeRect = closeBtn.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.82f, 0.1f);
            closeRect.anchorMax = new Vector2(0.98f, 0.9f);
            closeRect.offsetMin = closeRect.offsetMax = Vector2.zero;
            var xTxt = MakeText("X", closeBtn.transform, "×", 16, FontStyles.Bold,
                new Color(0.9f, 0.3f, 0.3f), TextAlignmentOptions.Center);
            StretchFull(xTxt.GetComponent<RectTransform>());

            // ---- Body: one scrolling column, so no section can overflow into the next ----
            Transform body = MakeScrollList("Body", _panelObj.transform,
                new Vector2(0f, 0f), new Vector2(1f, 0.95f));

            // ---- Direction ----
            MakeLine("DHeader", body, "▸ Direction", 13, FontStyles.Bold, new Color(1f, 0.8f, 0.8f));
            MakeLine("DHint", body, "Where should the story head? The director steers there over several turns without saying so. Empty = no steering.",
                10, FontStyles.Italic, HintColor);
            _directionInput = MakeInput("DirectionInput", body, "e.g. Draw me toward the capital and its court intrigue",
                52f, DmNotesManager.MaxDirectionChars, multiline: true);
            _directionInput.onValueChanged.AddListener(text => DmNotesManager.SetDirection(text, save: false));
            _directionInput.onEndEdit.AddListener(text => DmNotesManager.SetDirection(text, save: true));

            // ---- Planned threads ----
            _plannedHeader = MakeLine("TPHeader", body, "▸ Planned Threads", 13, FontStyles.Bold, new Color(0.8f, 1f, 0.85f));
            MakeLine("TPHint", body, "Ideas for later. The AI brings each one in when the moment fits, then it moves to Introduced.",
                10, FontStyles.Italic, HintColor);
            _plannedListContent = MakeVBox("PlannedList", body, 3f).transform;

            GameObject form = MakeVBox("ThreadForm", body, 2f);
            _threadInput = MakeInput("ThreadInput", form.transform, "A plot thread to introduce later...",
                30f, DmNotesManager.MaxThreadChars, multiline: true);
            _threadInput.onSubmit.AddListener(_ => SubmitThread());

            GameObject windowRow = MakeHRow("WindowRow", form.transform);
            MakeLabel("L1", windowRow.transform, "Not before");
            _notBeforeInput = MakeTurnsInput("NotBeforeInput", windowRow.transform);
            MakeLabel("L2", windowRow.transform, "by");
            _byInput = MakeTurnsInput("ByInput", windowRow.transform);
            MakeLabel("L3", windowRow.transform, "turns");

            GameObject submitRow = MakeHRow("SubmitRow", form.transform);
            _formHint = MakeFlexText("FormHint", submitRow.transform, FormHintDefault, 10, FontStyles.Italic, HintColor);
            _cancelEditBtn = MakeButton("CancelEdit", submitRow.transform, "Cancel", new Color(0.3f, 0.3f, 0.35f, 0.9f), 40f, ClearForm).gameObject;
            _cancelEditBtn.SetActive(false);
            Button submitBtn = MakeButton("Submit", submitRow.transform, "Add", new Color(0.15f, 0.4f, 0.2f, 0.9f), 34f, SubmitThread);
            _submitLabel = submitBtn.GetComponentInChildren<TextMeshProUGUI>();

            _introducedHeader = MakeLine("TIHeader", body, "Introduced", 11, FontStyles.Bold, new Color(0.65f, 0.75f, 0.68f)).gameObject;
            _introducedListContent = MakeVBox("IntroducedList", body, 2f).transform;

            // ---- Analysis ----
            MakeLine("AHeader", body, "▸ Analysis", 13, FontStyles.Bold, new Color(0.7f, 0.9f, 0.7f));
            _stateLabel = MakeLine("StateLabel", body, "Engagement: —", 12, FontStyles.Normal, Color.white);
            _pacingLabel = MakeLine("PacingLabel", body, "Pacing: —", 12, FontStyles.Normal, Color.white);
            _engagementLabel = MakeLine("EngageLabel", body, "", 11, FontStyles.Normal, new Color(0.75f, 0.75f, 0.75f));

            // ---- Plot threads the AI is tracking ----
            MakeLine("PHeader", body, "▸ Plot Threads", 13, FontStyles.Bold, new Color(0.7f, 0.85f, 1f));
            _plotListContent = MakeVBox("PlotList", body, 2f).transform;

            // ---- Preferences ----
            MakeLine("PrHeader", body, "▸ Player Profile", 13, FontStyles.Bold, new Color(1f, 0.85f, 0.6f));
            _prefListContent = MakeVBox("PrefList", body, 2f).transform;

            // ---- Behaviour component for Update loop ----
            _panelObj.AddComponent<DmNotesPanelBehaviour>();

            _panelObj.SetActive(false);
            RefreshUI();
        }

        private static Transform MakeScrollList(string name, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            GameObject scrollObj = MakeRect(name, parent, typeof(ScrollRect), typeof(Image));
            scrollObj.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.03f);
            SetAnchors(scrollObj.GetComponent<RectTransform>(), anchorMin.x, anchorMin.y, anchorMax.x, anchorMax.y);

            GameObject viewport = MakeRect("Viewport", scrollObj.transform, typeof(RectMask2D));
            StretchFull(viewport.GetComponent<RectTransform>());

            GameObject content = MakeRect("Content", viewport.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 3f;
            vlg.padding = new RectOffset(5, 5, 3, 6);
            var csf = content.GetComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var cRect = content.GetComponent<RectTransform>();
            cRect.anchorMin = new Vector2(0f, 1f);
            cRect.anchorMax = new Vector2(1f, 1f);
            cRect.pivot = new Vector2(0.5f, 1f);
            cRect.offsetMin = cRect.offsetMax = Vector2.zero;

            var sr = scrollObj.GetComponent<ScrollRect>();
            sr.viewport = viewport.GetComponent<RectTransform>();
            sr.content = cRect;
            sr.horizontal = false;
            sr.vertical = true;
            sr.scrollSensitivity = 15f;
            sr.movementType = ScrollRect.MovementType.Clamped;

            return content.transform;
        }

        public static void Toggle()
        {
            if (_panelObj == null) return;
            if (!_isOpen && !DmNotesManager.IsActive) return;
            SetOpen(!_isOpen);
        }

        /// <summary>Closes the panel if it is open (DM Notes was switched off).</summary>
        public static void Close()
        {
            if (_panelObj != null && _isOpen) SetOpen(false);
        }

        private static void SetOpen(bool open)
        {
            _isOpen = open;
            if (!open) ReleaseFocus();
            _panelObj.SetActive(open);
            if (open) RefreshUI();
        }

        // The game treats "an input field is the selected object" as "the player is typing" and
        // ignores its hotkeys. A field hidden while still selected would leave them ignored.
        private static void ReleaseFocus()
        {
            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;
            if (selected != null && selected.transform.IsChildOf(_panelObj.transform))
                es.SetSelectedGameObject(null);
        }

        public static void RefreshUI()
        {
            if (_panelObj == null) return;

            var state = DmNotesManager.CurrentState;
            int turn = state.TurnCounter;

            if (_directionInput != null && !_directionInput.isFocused && _directionInput.text != state.Direction)
                _directionInput.SetTextWithoutNotify(state.Direction);

            // ---- Planned threads ----
            if (_plannedHeader != null)
                _plannedHeader.text = $"▸ Planned Threads  <size=10><color=#9a9aa5>turn {turn}</color></size>";

            if (_editingId != null && !state.PlannedThreads.Exists(t => t.Id == _editingId))
                SetEditing(null); // retired or deleted while being edited; what was typed stays in the form

            if (_plannedListContent != null)
            {
                ClearChildren(_plannedListContent);
                if (state.PlannedThreads.Count == 0)
                    MakeText("Empty", _plannedListContent, "(none planned)", 11, FontStyles.Italic,
                        new Color(0.5f, 0.5f, 0.5f), TextAlignmentOptions.Left);
                List<DmPlannedThread> offered = DmThreadLogic.SelectForPrompt(state, DmNotesManager.MaxThreadsPerPrompt);
                foreach (var thread in state.PlannedThreads)
                    AddPlannedRow(thread, turn, offered.Contains(thread));
            }

            if (_introducedHeader != null) _introducedHeader.SetActive(state.IntroducedThreads.Count > 0);
            if (_introducedListContent != null)
            {
                ClearChildren(_introducedListContent);
                for (int i = state.IntroducedThreads.Count - 1; i >= 0; i--)
                    AddIntroducedRow(state.IntroducedThreads[i]);
            }

            // ---- What the AI wrote ----
            if (_stateLabel != null)
                _stateLabel.text = $"Engagement: <b>{state.PlayerState}</b>";
            if (_pacingLabel != null)
                _pacingLabel.text = $"Pacing: <b>{state.PacingDecision}</b>";
            if (_engagementLabel != null)
            {
                _engagementLabel.text = state.EngagementAnalysis;
                _engagementLabel.gameObject.SetActive(!string.IsNullOrEmpty(state.EngagementAnalysis));
            }

            RefreshList(_plotListContent, state.PlotThreads, isPlot: true);
            RefreshList(_prefListContent, state.PreferenceNotes, isPlot: false);
        }

        private static void RefreshList(Transform content, List<string> items, bool isPlot)
        {
            if (content == null) return;

            ClearChildren(content);

            if (items.Count == 0)
            {
                MakeText("Empty", content, "(none)", 11, FontStyles.Italic,
                    new Color(0.5f, 0.5f, 0.5f), TextAlignmentOptions.Left);
                return;
            }

            for (int i = 0; i < items.Count; i++)
            {
                int idx = i;
                // The row takes its height from the wrapped text. It used to be a fixed 18 units
                // with the text overflowing, so any entry longer than one line ran over the next.
                GameObject row = MakeHRow($"Row_{i}", content);
                MakeFlexText("T", row.transform, items[i], 11, FontStyles.Normal, Color.white);
                MakeButton("Del", row.transform, "×", new Color(0.5f, 0.1f, 0.1f, 0.7f), 16f, () =>
                {
                    if (isPlot) DmNotesManager.RemovePlotThread(idx);
                    else DmNotesManager.RemovePreference(idx);
                    RefreshUI();
                });
            }
        }

        // ---- Planned threads ----

        private static void AddPlannedRow(DmPlannedThread thread, int turn, bool offered)
        {
            string id = thread.Id;
            GameObject row = MakeCard($"Thread_{id}", _plannedListContent);

            var text = MakeLine("Text", row.transform, thread.Text, 11, FontStyles.Normal, Color.white);
            text.richText = false; // player text, shown as typed

            bool urgent = DmThreadLogic.IsUrgent(thread, turn) && DmThreadLogic.WaitTurns(thread, turn) == 0;
            GameObject bar = MakeHRow("Bar", row.transform);
            MakeFlexText("Status", bar.transform, DescribeWindow(thread, turn, offered), 10, FontStyles.Italic,
                urgent ? new Color(1f, 0.6f, 0.4f) : HintColor);
            MakeButton("Done", bar.transform, "Done", new Color(0.15f, 0.4f, 0.2f, 0.9f), 32f, () =>
            {
                DmNotesManager.RetireThread(id);
                RefreshUI();
            });
            MakeButton("Edit", bar.transform, "Edit", new Color(0.2f, 0.3f, 0.5f, 0.9f), 28f, () => BeginEdit(id));
            MakeButton("Del", bar.transform, "×", new Color(0.5f, 0.1f, 0.1f, 0.7f), 16f, () =>
            {
                DmNotesManager.DeleteThread(id);
                RefreshUI();
            });
        }

        private static void AddIntroducedRow(DmPlannedThread thread)
        {
            string id = thread.Id;
            GameObject row = MakeCard($"Introduced_{id}", _introducedListContent);

            var text = MakeLine("Text", row.transform, thread.Text, 10, FontStyles.Normal, new Color(0.7f, 0.7f, 0.72f));
            text.richText = false;

            GameObject bar = MakeHRow("Bar", row.transform);
            string how = thread.RetiredByPlayer ? "marked done" : "introduced";
            MakeFlexText("Status", bar.transform, $"{id} · {how} on turn {thread.IntroducedTurn}", 10, FontStyles.Italic, HintColor);
            // For when the AI reported a thread it had not really brought in yet.
            MakeButton("Undo", bar.transform, "Undo", new Color(0.3f, 0.3f, 0.35f, 0.9f), 32f, () =>
            {
                DmNotesManager.RestoreThread(id);
                RefreshUI();
            });
            MakeButton("Del", bar.transform, "×", new Color(0.5f, 0.1f, 0.1f, 0.7f), 16f, () =>
            {
                DmNotesManager.DeleteThread(id);
                RefreshUI();
            });
        }

        private static string DescribeWindow(DmPlannedThread thread, int turn, bool offered)
        {
            int wait = DmThreadLogic.WaitTurns(thread, turn);
            if (wait > 0) return $"{thread.Id} · held back {wait} more turn{(wait == 1 ? "" : "s")}";

            string when;
            if (!DmThreadLogic.HasDeadline(thread)) when = "any time now";
            else
            {
                int left = DmThreadLogic.TurnsLeft(thread, turn);
                when = left <= 0 ? "OVERDUE" : left == 1 ? "due this turn" : $"due within {left} turns";
            }
            // Only the first few are shown to the AI each turn (MaxPlannedThreadsPerPrompt).
            return offered ? $"{thread.Id} · {when}" : $"{thread.Id} · {when}, queued";
        }

        private static void BeginEdit(string id)
        {
            DmPlannedThread thread = DmNotesManager.CurrentState.PlannedThreads.Find(t => t.Id == id);
            if (thread == null || _threadInput == null) return;

            _threadInput.SetTextWithoutNotify(thread.Text);
            _notBeforeInput.SetTextWithoutNotify(thread.NotBeforeTurns > 0 ? thread.NotBeforeTurns.ToString() : "");
            _byInput.SetTextWithoutNotify(thread.ByTurns > 0 ? thread.ByTurns.ToString() : "");
            SetEditing(id);
            SetFormHint($"Editing {id}. Its windows still count from when it was added.");
        }

        private static void SetEditing(string id)
        {
            _editingId = id;
            if (_submitLabel != null) _submitLabel.text = id != null ? "Save" : "Add";
            if (_cancelEditBtn != null) _cancelEditBtn.SetActive(id != null);
            if (id == null) SetFormHint(FormHintDefault);
        }

        private static void ClearForm()
        {
            if (_threadInput != null) _threadInput.SetTextWithoutNotify("");
            if (_notBeforeInput != null) _notBeforeInput.SetTextWithoutNotify("");
            if (_byInput != null) _byInput.SetTextWithoutNotify("");
            SetEditing(null);
        }

        private static void SubmitThread()
        {
            if (_threadInput == null) return;

            string text = _threadInput.text;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetFormHint("Type the thread first.");
                return;
            }

            int notBefore = ReadTurns(_notBeforeInput);
            int by = ReadTurns(_byInput);
            if (_editingId != null)
            {
                if (!DmNotesManager.UpdateThread(_editingId, text, notBefore, by))
                {
                    SetEditing(null);
                    SetFormHint("That thread is no longer pending. Add adds it as a new one.");
                    return;
                }
            }
            else if (DmNotesManager.AddThread(text, notBefore, by) == null)
            {
                SetFormHint($"The list is full ({DmNotesManager.MaxPendingThreads}). Remove one first.");
                return;
            }

            ClearForm();
            RefreshUI();
        }

        private static int ReadTurns(TMP_InputField input)
        {
            int turns;
            return input != null && int.TryParse(input.text, out turns) && turns > 0 ? turns : 0;
        }

        private static void SetFormHint(string text)
        {
            if (_formHint != null) _formHint.text = text;
        }

        // ---- Helpers ----

        private static GameObject MakeRect(string name, Transform parent, params Type[] components)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            foreach (var c in components) obj.AddComponent(c);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private static GameObject MakeText(string name, Transform parent, string text,
            int fontSize, FontStyles style, Color color, TextAlignmentOptions align)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            var tmp = obj.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.alignment = align;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableWordWrapping = false;
            return obj;
        }

        /// <summary>A wrapping line of text for a vertical list: full width, as tall as its text.</summary>
        private static TextMeshProUGUI MakeLine(string name, Transform parent, string text,
            int fontSize, FontStyles style, Color color)
        {
            var tmp = MakeText(name, parent, text, fontSize, style, color, TextAlignmentOptions.TopLeft)
                .GetComponent<TextMeshProUGUI>();
            tmp.enableWordWrapping = true;
            return tmp;
        }

        /// <summary>Wrapping text for a horizontal row: takes whatever width the fixed-size children leave.</summary>
        private static TextMeshProUGUI MakeFlexText(string name, Transform parent, string text,
            int fontSize, FontStyles style, Color color)
        {
            var tmp = MakeLine(name, parent, text, fontSize, style, color);
            var le = tmp.gameObject.AddComponent<LayoutElement>();
            // Width comes from the row, not from how long the text would be on one line.
            le.minWidth = 0f;
            le.preferredWidth = 0f;
            le.flexibleWidth = 1f;
            return tmp;
        }

        /// <summary>A short caption sitting beside an 18-unit-high input in a horizontal row.</summary>
        private static void MakeLabel(string name, Transform parent, string text)
        {
            GameObject obj = MakeText(name, parent, text, 10, FontStyles.Normal, HintColor, TextAlignmentOptions.MidlineLeft);
            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = 18f;
        }

        private static GameObject MakeVBox(string name, Transform parent, float spacing)
        {
            GameObject box = MakeRect(name, parent, typeof(VerticalLayoutGroup));
            var vlg = box.GetComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = spacing;
            return box;
        }

        private static GameObject MakeHRow(string name, Transform parent)
        {
            GameObject row = MakeRect(name, parent, typeof(HorizontalLayoutGroup));
            var hlg = row.GetComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.spacing = 3f;
            return row;
        }

        /// <summary>A vertical box with a faint background: one thread.</summary>
        private static GameObject MakeCard(string name, Transform parent)
        {
            GameObject card = MakeVBox(name, parent, 1f);
            card.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(3, 3, 2, 2);
            card.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);
            return card;
        }

        private static Button MakeButton(string name, Transform parent, string label, Color color,
            float width, UnityAction onClick)
        {
            GameObject obj = MakeRect(name, parent, typeof(Image), typeof(Button), typeof(LayoutElement));
            var img = obj.GetComponent<Image>();
            img.color = color;
            var le = obj.GetComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = width;
            le.minHeight = le.preferredHeight = 16f;

            var txt = MakeText("L", obj.transform, label, 10, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            txt.GetComponent<TextMeshProUGUI>().raycastTarget = false;
            StretchFull(txt.GetComponent<RectTransform>());

            var btn = obj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            return btn;
        }

        /// <summary>
        /// A text box. It is a real TMP_InputField on the object that gets selected, which is what
        /// the game checks (Utils.IsInputFieldFocused) before acting on its own hotkeys - so typing
        /// here does not open the inventory or the map behind the panel.
        /// </summary>
        private static TMP_InputField MakeInput(string name, Transform parent, string placeholder,
            float height, int charLimit, bool multiline)
        {
            GameObject obj = MakeRect(name, parent, typeof(Image), typeof(LayoutElement));
            var img = obj.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.5f);
            var le = obj.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;

            GameObject area = MakeRect("TextArea", obj.transform, typeof(RectMask2D));
            var areaRect = area.GetComponent<RectTransform>();
            StretchFull(areaRect);
            areaRect.offsetMin = new Vector2(4f, 2f);
            areaRect.offsetMax = new Vector2(-4f, -2f);

            TextAlignmentOptions align = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            var ph = MakeText("Placeholder", area.transform, placeholder, 11, FontStyles.Italic,
                new Color(0.5f, 0.5f, 0.55f), align).GetComponent<TextMeshProUGUI>();
            ph.enableWordWrapping = multiline;
            StretchFull(ph.rectTransform);
            var txt = MakeText("Text", area.transform, "", 11, FontStyles.Normal, Color.white, align)
                .GetComponent<TextMeshProUGUI>();
            txt.enableWordWrapping = multiline;
            txt.richText = false;
            StretchFull(txt.rectTransform);

            // Inactive while it is wired up, so TMP_InputField.OnEnable finds its parts in place.
            obj.SetActive(false);
            var input = obj.AddComponent<TMP_InputField>();
            input.targetGraphic = img;
            input.textViewport = areaRect;
            input.textComponent = txt;
            input.placeholder = ph;
            input.fontAsset = txt.font;
            input.pointSize = 11;
            input.richText = false;
            input.characterLimit = charLimit;
            // Enter finishes the edit instead of adding a line; the text still wraps.
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineSubmit : TMP_InputField.LineType.SingleLine;
            input.restoreOriginalTextOnEscape = false;
            var nav = input.navigation;
            nav.mode = Navigation.Mode.None;
            input.navigation = nav;
            obj.SetActive(true);
            return input;
        }

        private static TMP_InputField MakeTurnsInput(string name, Transform parent)
        {
            TMP_InputField input = MakeInput(name, parent, "-", 18f, 4, multiline: false);
            input.contentType = TMP_InputField.ContentType.IntegerNumber;
            var le = input.GetComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = 36f;
            return input;
        }

        private static void ClearChildren(Transform content)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                GameObject child = content.GetChild(i).gameObject;
                // Destroy only takes effect at the end of the frame; hidden, the old rows are
                // already out of the layout that the new rows are about to join.
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        private static void StretchFull(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        private static void SetAnchors(RectTransform r, float minX, float minY, float maxX, float maxY)
        {
            r.anchorMin = new Vector2(minX, minY);
            r.anchorMax = new Vector2(maxX, maxY);
            r.offsetMin = r.offsetMax = Vector2.zero;
        }
    }

    public class DmNotesPanelBehaviour : MonoBehaviour
    {
        private void Update()
        {
            if (DmNotesManager.ConsumeUiDirty())
                DmNotesPanel.RefreshUI();
        }
    }
}
