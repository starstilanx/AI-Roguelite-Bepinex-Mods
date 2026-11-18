using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using AIROG_Schedules.Logic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AIROG_Schedules
{
    /// <summary>
    /// HUD button + the Schedules panel. Built procedurally on MainLayouts, like Chronicle's.
    ///
    /// The panel is modal: a full-size raycast blocker sits under it, so no click or scroll
    /// reaches the game while it is open. Entries are edited in the game's own NTextPromptModal;
    /// the panel steps aside while that modal is up (so there is never a question of which of
    /// the two is drawn on top) and comes back when it closes.
    /// </summary>
    public static class SchedulesUI
    {
        private const string PanelName = "SchedulesPanel";
        private const string BlockerName = "SchedulesBlocker";
        private const string ButtonName = "SchedulesBtn";

        private static GameObject _panel;
        private static GameObject _blocker;
        private static GameObject _button;
        private static Transform _listContent;
        private static TextMeshProUGUI _clockLabel;
        private static TextMeshProUGUI _todayLabel;
        private static TextMeshProUGUI _weekLabel;
        private static TextMeshProUGUI _statusLabel;
        private static GameObject _todayGroup;
        private static GameObject _weekGroup;

        private static bool _suspendedForEditor;
        private static int _confirmDeleteIndex = -1;
        private static string _shownSignature;

        private static readonly Color PanelBg = new Color(0.06f, 0.07f, 0.10f, 0.97f);
        private static readonly Color BarBg = new Color(0.10f, 0.12f, 0.18f, 1f);
        private static readonly Color RowBg = new Color(0.12f, 0.13f, 0.18f, 0.85f);
        private static readonly Color RowActiveBg = new Color(0.10f, 0.27f, 0.16f, 0.90f);
        private static readonly Color RowOffBg = new Color(0.09f, 0.09f, 0.11f, 0.60f);
        private static readonly Color BtnBg = new Color(0.20f, 0.25f, 0.38f, 1f);
        private static readonly Color BtnDangerBg = new Color(0.50f, 0.14f, 0.14f, 1f);
        private static readonly Color TextMain = new Color(0.90f, 0.92f, 0.97f);
        private static readonly Color TextDim = new Color(0.62f, 0.66f, 0.75f);
        private static readonly Color Accent = new Color(0.72f, 0.86f, 1.0f);

        // ---- hooks -----------------------------------------------------------------------------

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts
        {
            [HarmonyPostfix]
            public static void Postfix(MainLayouts __instance)
            {
                // Runs inside GameplayManager.StartGame: must not throw.
                try { Create(__instance); }
                catch (Exception ex) { SchedulesPlugin.LogWarning("Could not build the Schedules UI: " + ex); }
            }
        }

        /// <summary>
        /// The game routes Esc through GameplayManager.EscKey, which closes whichever of its own
        /// windows is open and otherwise opens the settings menu. Without this the same key press
        /// that closes our panel would also pop that menu. Game modals keep priority: if one of
        /// them is showing, the game handles the key as usual.
        /// </summary>
        [HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.EscKey))]
        public static class Patch_EscKey
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                try
                {
                    if (!IsOpen) return true;
                    if (MessageModal.I != null && MessageModal.I.IsShowing()) return true;
                    if (ModalWindow.I != null && ModalWindow.I.IsShowing()) return true;
                    if (NTextPromptModal.I != null && NTextPromptModal.I.IsShowing()) return true;
                    Close();
                    return false;
                }
                catch (Exception ex)
                {
                    SchedulesPlugin.LogWarningOnce("esc", "Esc handling failed: " + ex);
                    return true;
                }
            }
        }

        // ---- lifecycle -------------------------------------------------------------------------

        public static bool IsOpen
        {
            get { return _panel != null && _panel.activeSelf; }
        }

        public static void Create(MainLayouts layout)
        {
            if (layout == null) return;

            if (layout.mainHolder != null && layout.mainHolder.Find(PanelName) == null)
                CreatePanel(layout.mainHolder);

            if (layout.buttonsHolderHolder != null && layout.buttonsHolderHolder.Find(ButtonName) == null)
                InjectButton(layout.buttonsHolderHolder);

            ApplySwitch();
        }

        /// <summary>
        /// Shows or hides the HUD button for the current save and closes the panel when the mod is
        /// off. UI only: this is also the ModToggles.Reloaded handler, which can run mid-load.
        /// </summary>
        public static void ApplySwitch()
        {
            try
            {
                bool on = SchedulesPlugin.IsActive();
                if (_button != null && _button.activeSelf != on) _button.SetActive(on);
                if (!on) Close();
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarningOnce("switch", "Could not apply the Schedules switch: " + ex);
            }
        }

        public static void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public static void Open()
        {
            if (_panel == null || _blocker == null) return;
            if (!SchedulesPlugin.IsActive()) return;

            _suspendedForEditor = false;
            _confirmDeleteIndex = -1;
            Show();
            SetStatus(ScheduleStore.LoadProblem ?? "", ScheduleStore.LoadProblem != null);
        }

        public static void Close()
        {
            _suspendedForEditor = false;
            _confirmDeleteIndex = -1;
            if (_panel != null) _panel.SetActive(false);
            if (_blocker != null) _blocker.SetActive(false);
        }

        private static void Show()
        {
            _blocker.SetActive(true);
            _panel.SetActive(true);
            BringToFront();
            ReleaseGameInput();
            RefreshAll();
        }

        /// <summary>
        /// In grid and pano layouts the game re-parents its HUD button row and text input into the
        /// same holder and raises them to the top; keep the blocker and panel above them.
        /// </summary>
        internal static void BringToFront()
        {
            if (_panel == null || _blocker == null) return;
            Transform parent = _panel.transform.parent;
            if (parent == null) return;
            if (_panel.transform.GetSiblingIndex() == parent.childCount - 1
                && _blocker.transform.GetSiblingIndex() == parent.childCount - 2) return;
            _blocker.transform.SetAsLastSibling();
            _panel.transform.SetAsLastSibling();
        }

        /// <summary>Takes keyboard focus away from the game's text fields so typing cannot start a turn behind the panel.</summary>
        internal static void ReleaseGameInput()
        {
            EventSystem es = EventSystem.current;
            if (es == null || _panel == null) return;
            GameObject selected = es.currentSelectedGameObject;
            if (selected == null || selected.transform.IsChildOf(_panel.transform)) return;
            var input = selected.GetComponent<TMP_InputField>();
            if (input == null) return;
            input.DeactivateInputField();
            es.SetSelectedGameObject(null);
        }

        // ---- construction ----------------------------------------------------------------------

        private static void InjectButton(Transform buttonsHolder)
        {
            // Same shape and size as Chronicle's "Chron" button so the row's layout group treats it alike.
            _button = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
            _button.transform.SetParent(buttonsHolder, false);

            var img = _button.GetComponent<Image>();
            Sprite sprite = LoadSprite("SchedulesButton.png");
            if (sprite != null)
            {
                img.sprite = sprite;
                img.color = Color.white;
                img.preserveAspect = true;
            }
            else
            {
                img.color = new Color(0.10f, 0.14f, 0.24f, 0.9f);
                var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                label.transform.SetParent(_button.transform, false);
                var txt = label.GetComponent<TextMeshProUGUI>();
                txt.text = "Sched";
                txt.fontSize = 11;
                txt.fontStyle = FontStyles.Bold;
                txt.color = Accent;
                txt.alignment = TextAlignmentOptions.Center;
                txt.enableWordWrapping = false;
                txt.raycastTarget = false;
                StretchFull(label.GetComponent<RectTransform>());
            }

            var le = _button.AddComponent<LayoutElement>();
            le.preferredWidth = 60; le.minWidth = 60;
            le.preferredHeight = 60; le.minHeight = 60;

            var btn = _button.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(Toggle);
            _button.transform.SetAsLastSibling();
        }

        private static Sprite LoadSprite(string filename)
        {
            try
            {
                // Optional icon; the text label is the normal look.
                string path = System.IO.Path.Combine(Application.streamingAssetsPath, "Schedules", filename);
                if (!System.IO.File.Exists(path)) return null;
                var tex = new Texture2D(2, 2);
                if (tex.LoadImage(System.IO.File.ReadAllBytes(path)))
                    return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Could not load the button icon: " + ex.Message);
            }
            return null;
        }

        private static void CreatePanel(Transform mainHolder)
        {
            // Blocker first, so it sits directly under the panel.
            _blocker = MakeRect(BlockerName, mainHolder, typeof(Image), typeof(Button), typeof(InputSink));
            StretchFull(_blocker.GetComponent<RectTransform>());
            var blockerImg = _blocker.GetComponent<Image>();
            blockerImg.color = new Color(0f, 0f, 0f, 0.45f);
            var blockerBtn = _blocker.GetComponent<Button>();
            blockerBtn.transition = Selectable.Transition.None;
            blockerBtn.targetGraphic = blockerImg;
            blockerBtn.onClick.AddListener(Close);

            _panel = MakeRect(PanelName, mainHolder, typeof(Image), typeof(InputSink));
            SetAnchors(_panel.GetComponent<RectTransform>(), 0.10f, 0.06f, 0.90f, 0.94f);
            _panel.GetComponent<Image>().color = PanelBg; // raycast target: swallows clicks on empty panel areas

            // Title bar
            GameObject titleBar = MakeRect("TitleBar", _panel.transform, typeof(Image));
            SetAnchors(titleBar.GetComponent<RectTransform>(), 0f, 0.94f, 1f, 1f);
            titleBar.GetComponent<Image>().color = BarBg;
            var title = MakeText("Title", titleBar.transform, "Schedules", 14, FontStyles.Bold, Accent, TextAlignmentOptions.Left);
            SetAnchors(title.rectTransform, 0.02f, 0f, 0.5f, 1f);
            var hint = MakeText("Hint", titleBar.transform, "Esc or X closes", 10, FontStyles.Italic, TextDim, TextAlignmentOptions.Right);
            SetAnchors(hint.rectTransform, 0.5f, 0f, 0.93f, 1f);
            GameObject closeBtn = MakeButton("CloseBtn", titleBar.transform, "X", BtnDangerBg, Close, 13);
            SetAnchors(closeBtn.GetComponent<RectTransform>(), 0.945f, 0.12f, 0.99f, 0.88f);

            // Clock bar: what the mod thinks "now" is, and the weekday control
            GameObject clockBar = MakeRect("ClockBar", _panel.transform, typeof(Image));
            SetAnchors(clockBar.GetComponent<RectTransform>(), 0f, 0.855f, 1f, 0.94f);
            clockBar.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.13f, 1f);

            GameObject clockClip = MakeRect("ClockClip", clockBar.transform, typeof(RectMask2D));
            SetAnchors(clockClip.GetComponent<RectTransform>(), 0.02f, 0f, 0.63f, 1f);
            _clockLabel = MakeText("Clock", clockClip.transform, "", 12, FontStyles.Normal, TextMain, TextAlignmentOptions.Left);
            StretchFull(_clockLabel.rectTransform);

            _todayGroup = MakeRect("Today", clockBar.transform);
            SetAnchors(_todayGroup.GetComponent<RectTransform>(), 0.64f, 0f, 0.99f, 1f);
            var todayCaption = MakeText("Caption", _todayGroup.transform, "Today is", 11, FontStyles.Normal, TextDim, TextAlignmentOptions.Right);
            SetAnchors(todayCaption.rectTransform, 0f, 0f, 0.24f, 1f);
            GameObject prevDay = MakeButton("PrevDay", _todayGroup.transform, "<", BtnBg, () => ShiftToday(-1), 13);
            SetAnchors(prevDay.GetComponent<RectTransform>(), 0.27f, 0.2f, 0.36f, 0.8f);
            _todayLabel = MakeText("Weekday", _todayGroup.transform, "", 12, FontStyles.Bold, Accent, TextAlignmentOptions.Center);
            SetAnchors(_todayLabel.rectTransform, 0.37f, 0f, 0.89f, 1f);
            GameObject nextDay = MakeButton("NextDay", _todayGroup.transform, ">", BtnBg, () => ShiftToday(1), 13);
            SetAnchors(nextDay.GetComponent<RectTransform>(), 0.90f, 0.2f, 0.99f, 0.8f);

            // Toolbar
            GameObject toolbar = MakeRect("Toolbar", _panel.transform, typeof(Image));
            SetAnchors(toolbar.GetComponent<RectTransform>(), 0f, 0.785f, 1f, 0.855f);
            toolbar.GetComponent<Image>().color = BarBg;
            GameObject addBtn = MakeButton("AddBtn", toolbar.transform, "Add entry", new Color(0.16f, 0.38f, 0.24f, 1f), () => EditEntry(-1), 12);
            SetAnchors(addBtn.GetComponent<RectTransform>(), 0.02f, 0.15f, 0.14f, 0.85f);
            GameObject exportBtn = MakeButton("ExportBtn", toolbar.transform, "Export", BtnBg, () => Announce(ScheduleStore.Export()), 12);
            SetAnchors(exportBtn.GetComponent<RectTransform>(), 0.15f, 0.15f, 0.23f, 0.85f);
            GameObject importBtn = MakeButton("ImportBtn", toolbar.transform, "Import", BtnBg, () => { Announce(ScheduleStore.Import()); RefreshAll(); }, 12);
            SetAnchors(importBtn.GetComponent<RectTransform>(), 0.24f, 0.15f, 0.32f, 0.85f);
            GameObject folderBtn = MakeButton("FolderBtn", toolbar.transform, "Folder", BtnBg, OpenSaveFolder, 12);
            SetAnchors(folderBtn.GetComponent<RectTransform>(), 0.33f, 0.15f, 0.41f, 0.85f);

            _weekGroup = MakeRect("Week", toolbar.transform);
            SetAnchors(_weekGroup.GetComponent<RectTransform>(), 0.42f, 0f, 0.62f, 1f);
            _weekLabel = MakeText("WeekLabel", _weekGroup.transform, "", 11, FontStyles.Normal, TextDim, TextAlignmentOptions.Center);
            SetAnchors(_weekLabel.rectTransform, 0f, 0f, 0.62f, 1f);
            GameObject weekMinus = MakeButton("WeekMinus", _weekGroup.transform, "-", BtnBg, () => ShiftWeekLength(-1), 13);
            SetAnchors(weekMinus.GetComponent<RectTransform>(), 0.64f, 0.2f, 0.80f, 0.8f);
            GameObject weekPlus = MakeButton("WeekPlus", _weekGroup.transform, "+", BtnBg, () => ShiftWeekLength(1), 13);
            SetAnchors(weekPlus.GetComponent<RectTransform>(), 0.83f, 0.2f, 0.99f, 0.8f);

            GameObject statusClip = MakeRect("StatusClip", toolbar.transform, typeof(RectMask2D));
            SetAnchors(statusClip.GetComponent<RectTransform>(), 0.63f, 0f, 0.985f, 1f);
            _statusLabel = MakeText("Status", statusClip.transform, "", 10, FontStyles.Italic, TextDim, TextAlignmentOptions.Right);
            _statusLabel.enableWordWrapping = true;
            StretchFull(_statusLabel.rectTransform);

            // Entry list
            _listContent = MakeScrollList("List", _panel.transform, new Vector2(0f, 0.05f), new Vector2(1f, 0.785f));

            // Footer
            GameObject footer = MakeRect("Footer", _panel.transform, typeof(Image));
            SetAnchors(footer.GetComponent<RectTransform>(), 0f, 0f, 1f, 0.05f);
            footer.GetComponent<Image>().color = BarBg;
            var foot = MakeText("FooterText", footer.transform,
                "Schedules tell the storyteller what is normal. They never move a character by themselves.",
                10, FontStyles.Italic, TextDim, TextAlignmentOptions.Center);
            StretchFull(foot.rectTransform);

            _panel.AddComponent<SchedulesPanelBehaviour>();
            _shownSignature = null;
            _suspendedForEditor = false;
            _panel.SetActive(false);
            _blocker.SetActive(false);
        }

        // ---- refresh ---------------------------------------------------------------------------

        /// <summary>Changes whenever something the panel shows has changed (data, clock, place, statuses).</summary>
        internal static string Signature(NowSnapshot snap)
        {
            var sb = new StringBuilder();
            sb.Append(ScheduleStore.Revision).Append('|');
            if (snap.Clock != null)
                sb.Append(snap.Clock.DayIndex).Append(':').Append(snap.Clock.MinuteOfDay).Append(':')
                  .Append(snap.Clock.WeekOffset).Append(':').Append(snap.Clock.WeekLength).Append(':').Append(snap.Clock.WeekdayName);
            sb.Append('|');
            foreach (string p in snap.PlaceChain) sb.Append(p).Append('>');
            sb.Append('|').Append(_confirmDeleteIndex);
            return sb.ToString();
        }

        /// <summary>Called by the panel's Update: redraw only when something changed.</summary>
        internal static void RefreshIfStale()
        {
            if (!IsOpen) return;
            NowSnapshot snap = GameProbe.Capture();
            if (Signature(snap) != _shownSignature) RefreshAll(snap);
        }

        public static void RefreshAll()
        {
            RefreshAll(GameProbe.Capture());
        }

        private static void RefreshAll(NowSnapshot snap)
        {
            if (_panel == null || _listContent == null) return;
            try
            {
                _shownSignature = Signature(snap);
                ClockInfo clock = snap.Clock ?? FallbackClock();
                RefreshHeader(snap, clock);
                RefreshList(snap, clock);
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Schedules panel refresh failed: " + ex);
            }
        }

        /// <summary>Day names and day length to edit against when the save has no clock.</summary>
        internal static ClockInfo FallbackClock()
        {
            int length = ScheduleStore.Data.weekLength > 0 ? ScheduleStore.Data.weekLength : SchedulesPlugin.DefaultWeekLength();
            return ClockMath.FromElapsedSeconds(0, 60, 24, length, ScheduleStore.Data.weekdayOffset, SchedulesPlugin.ConfiguredWeekdayNames());
        }

        private static void RefreshHeader(NowSnapshot snap, ClockInfo clock)
        {
            bool hasClock = snap.Clock != null;
            if (_clockLabel != null)
            {
                if (hasClock)
                {
                    string where = snap.PlaceChain.Count > 0 && snap.PlaceChain[0].Length > 0 ? Plain(snap.PlaceChain[0]) : "(nowhere yet)";
                    if (snap.PlaceChain.Count > 1)
                        where += " <color=#9aa3b5>(inside: " + Plain(string.Join(", ", snap.PlaceChain.GetRange(1, snap.PlaceChain.Count - 1).ToArray())) + ")</color>";
                    string source = snap.WeekSource == WeekSource.GameCalendar
                        ? "weekday and week length follow the game calendar"
                        : "this calendar has no day names: weekday = (day + offset) mod week length";
                    _clockLabel.text = "Now: <b>" + Plain(clock.WeekdayName) + " " + clock.TimeText + "</b>   <size=10><color=#9aa3b5>"
                                       + source + "</color></size>\n<size=11>You are at: " + where + "</size>";
                }
                else
                {
                    _clockLabel.text = "<color=#ffb070>" + Plain(snap.NoClockReason ?? "No in-game clock.") + "</color>";
                }
            }

            if (_todayGroup != null) _todayGroup.SetActive(hasClock);
            if (_todayLabel != null && hasClock) _todayLabel.text = Plain(clock.WeekdayName);

            bool derived = snap.WeekSource == WeekSource.Derived;
            if (_weekGroup != null) _weekGroup.SetActive(derived);
            if (_weekLabel != null && derived) _weekLabel.text = "Week: " + clock.WeekLength + (clock.WeekLength == 1 ? " day" : " days");
        }

        private static void RefreshList(NowSnapshot snap, ClockInfo clock)
        {
            for (int i = _listContent.childCount - 1; i >= 0; i--)
            {
                // Destroy only takes effect at the end of the frame; switching the old row off
                // first keeps the layout group from counting old and new rows together until then.
                GameObject old = _listContent.GetChild(i).gameObject;
                old.SetActive(false);
                UnityEngine.Object.Destroy(old);
            }

            List<ScheduleEntry> entries = ScheduleStore.Data.entries;
            if (_confirmDeleteIndex >= entries.Count) _confirmDeleteIndex = -1;

            if (entries.Count == 0)
            {
                RenderEmptyState();
                return;
            }

            bool hasClock = snap.Clock != null;
            int soon = SchedulesPlugin.SoonWindow();
            for (int i = 0; i < entries.Count; i++)
                RenderRow(i, entries[i], ScheduleEngine.Evaluate(entries[i], clock, soon), snap, clock, hasClock);
        }

        private static void RenderEmptyState()
        {
            GameObject row = MakeRect("Empty", _listContent, typeof(LayoutElement));
            row.GetComponent<LayoutElement>().minHeight = 250f;
            string text =
                "<b>No schedules yet.</b>\n\n" +
                "A schedule tells the storyteller what normally happens at a place at a certain time of day, " +
                "and where people normally are. It is background for the AI, never a script: the mod does not " +
                "move anybody, and the story may break a schedule when it has a reason.\n\n" +
                "<b>Example</b>\n" +
                "Title: Debate club     Place: Debate Club Room     Days: Monday, Wednesday\n" +
                "Time: 15:30 to 17:00     Kind: Event     Characters: Aiko, Ren\n\n" +
                "Visit the room on a Monday at 16:00 and the AI knows the club is in session and who should be there. " +
                "Meet Aiko in the cafeteria at that hour and the AI knows she would normally be at the club.\n\n" +
                "Press <b>Add entry</b> to make the first one. The place is filled in with where you are standing.";
            if (MpSession.IsClient)
                text += "\n\n<color=#ffb070>You are a co-op guest: prompts are built on the host's game, so the host's schedules are the ones that apply.</color>";
            var label = MakeText("Text", row.transform, text, 12, FontStyles.Normal, TextMain, TextAlignmentOptions.TopLeft);
            label.enableWordWrapping = true;
            SetAnchors(label.rectTransform, 0.03f, 0f, 0.97f, 0.96f);
        }

        private static void RenderRow(int index, ScheduleEntry e, EntryState st, NowSnapshot snap, ClockInfo clock, bool hasClock)
        {
            bool hasDetails = !string.IsNullOrEmpty(e.details);
            bool activeNow = hasClock && e.enabled && st.Runnable && st.Status == EntryStatus.Active;

            GameObject row = MakeRect("Entry_" + index, _listContent, typeof(LayoutElement), typeof(Image));
            var le = row.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = hasDetails ? 58f : 44f;
            row.GetComponent<Image>().color = !e.enabled ? RowOffBg : (activeNow ? RowActiveBg : RowBg);

            // Status tag
            string tag;
            if (!e.enabled) tag = "<color=#8a8f9c>OFF</color>";
            else if (!hasClock) tag = "";
            else if (!st.Runnable) tag = "<color=#ff8080>CANNOT RUN: " + Plain(st.Problem) + "</color>";
            else if (st.Status == EntryStatus.Active)
            {
                bool here = snap.PlaceChain.Count > 0 && PlaceMatcher.MatchDepth(e.place, snap.PlaceChain) >= 0;
                tag = "<color=#7dff9a><b>ON NOW" + (here ? ", HERE" : "") + "</b></color>";
            }
            else if (st.Status == EntryStatus.StartingSoon) tag = "<color=#ffe08a>starts in " + st.MinutesUntilStart + " min</color>";
            else if (st.Status == EntryStatus.JustEnded) tag = "<color=#b0b6c4>ended " + st.MinutesSinceEnd + " min ago</color>";
            else tag = "";
            if (e.enabled && hasClock && st.Runnable && st.UnknownDays.Count > 0)
                tag += "  <color=#ffb070>unknown day: " + Plain(string.Join(", ", st.UnknownDays.ToArray())) + "</color>";

            bool routine = ScheduleKinds.IsRoutine(e.kind);
            string titleColor = e.enabled ? "#e6ebf7" : "#8a8f9c";
            var sb = new StringBuilder();
            sb.Append("<color=").Append(titleColor).Append("><b>").Append(Plain(string.IsNullOrEmpty(e.title) ? "(untitled)" : e.title)).Append("</b></color>")
              .Append("  <color=#8fa3c8>").Append(routine ? "routine" : "event").Append("</color>");
            if (tag.Length > 0) sb.Append("   ").Append(tag);
            sb.Append('\n');
            sb.Append("<color=#aab3c5>")
              .Append(PlaceMatcher.IsAnywhere(e.place) ? "anywhere" : Plain(e.place))
              .Append("  |  ").Append(Plain(DaySpec.Describe(e.days, clock.WeekdayNames)))
              .Append("  |  ").Append(Plain(e.start)).Append(" - ").Append(Plain(e.end));
            if (st.Runnable && !st.AllDay && st.EndMinute < st.StartMinute) sb.Append(" (past midnight)");
            if (st.Runnable && st.AllDay) sb.Append(" (all day)");
            if (e.npcs.Count > 0) sb.Append("  |  ").Append(Plain(string.Join(", ", e.npcs.ToArray())));
            sb.Append("</color>");
            if (hasDetails)
                sb.Append("\n<color=#7f8798><i>").Append(Plain(BlockBuilder.Clip(BlockBuilder.Clean(e.details), 150))).Append("</i></color>");

            GameObject clip = MakeRect("TextClip", row.transform, typeof(RectMask2D));
            SetAnchors(clip.GetComponent<RectTransform>(), 0.012f, 0f, 0.655f, 1f);
            var label = MakeText("Text", clip.transform, sb.ToString(), 11, FontStyles.Normal, TextMain, TextAlignmentOptions.Left);
            StretchFull(label.rectTransform);

            // Row buttons
            int captured = index;
            GameObject onOff = MakeButton("OnOff", row.transform, e.enabled ? "On" : "Off",
                e.enabled ? new Color(0.16f, 0.38f, 0.24f, 1f) : new Color(0.25f, 0.25f, 0.28f, 1f),
                () => { _confirmDeleteIndex = -1; ScheduleStore.SetEnabled(captured, !ScheduleStore.Data.entries[captured].enabled); RefreshAll(); }, 11);
            SetAnchors(onOff.GetComponent<RectTransform>(), 0.665f, 0.2f, 0.725f, 0.8f);

            GameObject edit = MakeButton("Edit", row.transform, "Edit", BtnBg, () => EditEntry(captured), 11);
            SetAnchors(edit.GetComponent<RectTransform>(), 0.735f, 0.2f, 0.805f, 0.8f);

            GameObject copy = MakeButton("Duplicate", row.transform, "Duplicate", BtnBg,
                () => { _confirmDeleteIndex = -1; ScheduleStore.Duplicate(captured); RefreshAll(); }, 11);
            SetAnchors(copy.GetComponent<RectTransform>(), 0.815f, 0.2f, 0.905f, 0.8f);

            bool confirming = _confirmDeleteIndex == index;
            GameObject del = MakeButton("Delete", row.transform, confirming ? "Sure?" : "Delete", confirming ? new Color(0.80f, 0.15f, 0.15f, 1f) : BtnDangerBg,
                () => DeleteEntry(captured), 11);
            SetAnchors(del.GetComponent<RectTransform>(), 0.915f, 0.2f, 0.988f, 0.8f);
        }

        // ---- actions ---------------------------------------------------------------------------

        private static void DeleteEntry(int index)
        {
            // Two clicks: the first arms the button, the second deletes.
            if (_confirmDeleteIndex != index)
            {
                _confirmDeleteIndex = index;
                RefreshAll();
                return;
            }
            _confirmDeleteIndex = -1;
            ScheduleStore.RemoveAt(index);
            RefreshAll();
        }

        private static void EditEntry(int index)
        {
            try
            {
                _confirmDeleteIndex = -1;
                NowSnapshot snap = GameProbe.Capture();
                ClockInfo clock = snap.Clock ?? FallbackClock();
                List<ScheduleEntry> entries = ScheduleStore.Data.entries;
                bool isNew = index < 0 || index >= entries.Count;
                ScheduleEntry source = isNew ? SchedulesEditor.NewEntry(snap, clock) : entries[index].Clone();

                // Step aside while the game's modal is up; come back when it closes either way.
                _suspendedForEditor = true;
                _panel.SetActive(false);
                _blocker.SetActive(false);

                bool shown = SchedulesEditor.Open(source, isNew, clock,
                    built =>
                    {
                        if (isNew) ScheduleStore.Add(built);
                        else ScheduleStore.Replace(index, built);
                    },
                    ResumeAfterEditor);

                if (!shown)
                {
                    ResumeAfterEditor();
                    SetStatus("The game's edit window is not available right now.", true);
                }
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Could not open the schedule editor: " + ex);
                ResumeAfterEditor();
                SetStatus("Could not open the editor: " + ex.Message, true);
            }
        }

        private static void ResumeAfterEditor()
        {
            if (!_suspendedForEditor) return;
            _suspendedForEditor = false;
            if (_panel == null || _blocker == null) return;
            if (!SchedulesPlugin.IsActive()) return;
            Show();
        }

        private static void ShiftToday(int delta)
        {
            NowSnapshot snap = GameProbe.Capture();
            if (snap.Clock == null) return;
            SetStatus(GameProbe.SetToday(snap.Clock.WeekdayIndex + delta), false);
            RefreshAll();
        }

        private static void ShiftWeekLength(int delta)
        {
            NowSnapshot snap = GameProbe.Capture();
            if (snap.Clock == null || snap.WeekSource != WeekSource.Derived) return;
            int length = Mathf.Clamp(snap.Clock.WeekLength + delta, 1, 100);
            ScheduleStore.SetWeekLength(length);
            SetStatus("Week length for this save: " + length + (length == 1 ? " day." : " days."), false);
            RefreshAll();
        }

        /// <summary>Opens the save folder in the file manager: that is where the export file is written and read.</summary>
        private static void OpenSaveFolder()
        {
            string dir = ScheduleStore.CurrentSaveDir();
            if (dir == null || !System.IO.Directory.Exists(dir))
            {
                SetStatus("This game has no save folder yet. Save once first.", true);
                return;
            }
            Application.OpenURL(dir);
            SetStatus("Opened this save's folder.", false);
        }

        /// <summary>Export / import results: a toast, plus the panel's own status line.</summary>
        private static void Announce(string message)
        {
            SetStatus(message, message != null && message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0);
            try { if (Toast.I != null && !string.IsNullOrEmpty(message)) Toast.I.ShowToast(message); }
            catch { }
        }

        private static void SetStatus(string message, bool isError)
        {
            if (_statusLabel == null) return;
            _statusLabel.color = isError ? new Color(1f, 0.55f, 0.55f) : TextDim;
            _statusLabel.text = Plain(message ?? "");
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static readonly Regex NoParseTag = new Regex(@"</?\s*noparse\s*>", RegexOptions.IgnoreCase);

        /// <summary>Player text shown verbatim: no rich-text tags of theirs can restyle or break the panel.</summary>
        private static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return "<noparse>" + NoParseTag.Replace(text, "") + "</noparse>";
        }

        private static Transform MakeScrollList(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            // The scroll object's Image is the raycast target that makes wheel and drag reach the ScrollRect.
            GameObject scrollObj = MakeRect(name, parent, typeof(ScrollRect), typeof(Image));
            scrollObj.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.03f);
            SetAnchors(scrollObj.GetComponent<RectTransform>(), anchorMin.x, anchorMin.y, anchorMax.x, anchorMax.y);

            GameObject viewport = MakeRect("Viewport", scrollObj.transform, typeof(RectMask2D));
            StretchFull(viewport.GetComponent<RectTransform>());

            GameObject content = MakeRect("Content", viewport.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.spacing = 3f;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
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
            sr.scrollSensitivity = 25f;
            sr.movementType = ScrollRect.MovementType.Clamped;
            return content.transform;
        }

        private static GameObject MakeRect(string name, Transform parent, params Type[] components)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            foreach (Type c in components) obj.AddComponent(c);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private static TextMeshProUGUI MakeText(string name, Transform parent, string text, int fontSize,
            FontStyles style, Color color, TextAlignmentOptions align)
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
            tmp.richText = true;
            tmp.raycastTarget = false; // labels never eat a click meant for the button or row behind them
            return tmp;
        }

        private static GameObject MakeButton(string name, Transform parent, string label, Color background, Action onClick, int fontSize)
        {
            GameObject obj = MakeRect(name, parent, typeof(Image), typeof(Button));
            var img = obj.GetComponent<Image>();
            img.color = background;
            var btn = obj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() =>
            {
                try { onClick(); }
                catch (Exception ex) { SchedulesPlugin.LogWarning("Schedules panel button '" + name + "' failed: " + ex); }
            });
            var txt = MakeText("Label", obj.transform, label, fontSize, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            txt.richText = false; // "<" and ">" are literal labels here
            StretchFull(txt.rectTransform);
            return obj;
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

    /// <summary>Keeps the open panel current and on top. Only runs while the panel is active.</summary>
    public class SchedulesPanelBehaviour : MonoBehaviour
    {
        private float _nextCheck;

        private void Update()
        {
            try
            {
                SchedulesUI.ReleaseGameInput();
                if (Time.unscaledTime < _nextCheck) return;
                _nextCheck = Time.unscaledTime + 0.5f;
                SchedulesUI.BringToFront();
                SchedulesUI.RefreshIfStale();
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarningOnce("panel-update", "Schedules panel update failed: " + ex);
            }
        }
    }

    /// <summary>
    /// Sits on the blocker and on the panel root and swallows pointer events that nothing inside
    /// handled. uGUI hands an unhandled click, drag or wheel event to the nearest ancestor that
    /// has a handler for it; without this that ancestor could be a scroll view or drag surface of
    /// the game. The list's own ScrollRect is deeper in the hierarchy, so it still gets its events.
    /// </summary>
    public class InputSink : MonoBehaviour, IScrollHandler, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public void OnScroll(PointerEventData eventData) { }
        public void OnPointerClick(PointerEventData eventData) { }
        public void OnPointerDown(PointerEventData eventData) { }
        public void OnPointerUp(PointerEventData eventData) { }
        public void OnBeginDrag(PointerEventData eventData) { }
        public void OnDrag(PointerEventData eventData) { }
        public void OnEndDrag(PointerEventData eventData) { }
    }
}
