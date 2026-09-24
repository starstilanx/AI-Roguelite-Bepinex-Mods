using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_NPCExpansion
{
    public partial class NPCExamineUI
    {
        private string _newMemory = "";

        private static readonly Regex MilestoneTurn = new Regex(@"^\[T(\d+)\]\s*(.*)$");

        // ─── Shared bits ──────────────────────────────────────────────────────────

        private static void Bullet(Transform parent, string text, Color dot, float size = 13)
        {
            var row = DK.HStack(parent, 10, null, "Bullet", TextAnchor.UpperLeft);
            var d = DK.Text(row, "·", size + 2, dot, FontStyles.Bold, wrap: false);
            DK.LE(d, prefW: 8);
            var t = DK.Text(row, DK.Esc(text), size, DK.Body);
            DK.LE(t, flexW: 1, prefW: 0);
        }

        private static void Empty(Transform parent, string text) => DK.Text(parent, text, 13, DK.Faint, FontStyles.Italic);

        private static RectTransform CardHeader(Transform card, string label, out RectTransform right)
        {
            var row = DK.HStack(card, 10, null, "CardHeader", TextAnchor.MiddleLeft);
            DK.Label(row, label);
            DK.Spacer(row);
            right = row;
            return row;
        }

        private void GenerationHintCard(Transform parent, bool hasProfile)
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            var card = DK.CardBox(parent, 10, 18, 16);
            DK.Label(card, "Generation hint");
            var row = DK.HStack(card, 10, null, "HintRow");
            DK.Input(row, data.GenerationInstructions, "E.g. secretly a retired assassin...", false, 44, null, onEndEdit: s =>
            {
                if (_npc == null) return;
                var d = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
                if ((d.GenerationInstructions ?? "") == (s ?? "")) return;
                d.GenerationInstructions = s;
                NPCData.Save(_npc.uuid, d);
            });
            bool busy = _busy.Contains("generate");
            var b = DK.Button(row, busy ? "Generating..." : (hasProfile ? "Regenerate profile" : "Generate profile"), DK.BtnStyle.Primary, DoGenerate);
            b.SetInteractable(!busy);
            DK.Text(card, hasProfile
                ? "Rewrites personality, background, traits and abilities. Memories, affinity, secrets and quests are kept. Unsaved edits on this page are replaced."
                : "Builds a full profile from the current scene. Your hint steers it.", 12, DK.Faint);
        }

        // ─── Overview ─────────────────────────────────────────────────────────────

        private void BuildOverview()
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());

            if (data.IsDeceased || !IsAlive)
            {
                var dead = DK.CardBox(_content, 8, 20, 18, DK.NegChip, DK.Hex("#5a3428"));
                DK.Label(dead, "Deceased", DK.CloseFg);
                DK.Text(dead, string.IsNullOrEmpty(data.DeathInfo) ? "They have died." : DK.Esc(data.DeathInfo), 15, DK.TextBright);
                if (!string.IsNullOrEmpty(data.Epitaph))
                    DK.Text(dead, "“" + DK.Esc(data.Epitaph) + "”", 14, DK.Body, FontStyles.Italic);
            }

            if (!NPCData.HasProfile(_npc, data) && IsAlive)
                GenerationHintCard(_content, false);

            // Goal
            var goal = DK.HStack(_content, 20, new RectOffset(20, 20, 18, 18), "Goal", TextAnchor.MiddleLeft);
            DK.Fill(goal, DK.Card, 12, false);
            DK.Border(goal, DK.Line, 12);
            var gcol = DK.VStack(goal, 6, null, "GoalText");
            DK.LE(gcol, flexW: 1, prefW: 0);
            DK.Label(gcol, "Current goal");
            if (string.IsNullOrEmpty(data.CurrentGoal)) Empty(gcol, "No goal yet. One forms as they live between your turns.");
            else
            {
                DK.Text(gcol, DK.Esc(data.CurrentGoal), 22, DK.TextBright, FontStyles.Bold, display: true);
                if (!string.IsNullOrEmpty(data.GoalProgress))
                    DK.Text(gcol, "Progress: " + DK.Esc(data.GoalProgress), 13, DK.Muted);
            }
            DK.Button(goal, "Open Mind", DK.BtnStyle.Ghost, () => SwitchTab(Tab.Mind));

            var cols = DK.Columns(_content, 16, 1, 1);

            var thoughts = DK.CardBox(cols[0], 12);
            DK.Label(thoughts, "Recent thoughts");
            if (data.RecentThoughts == null || data.RecentThoughts.Count == 0) Empty(thoughts, "Nothing on their mind yet.");
            else foreach (var th in data.RecentThoughts.Take(4))
                DK.Text(thoughts, "“" + DK.Esc(th) + "”", 14, DK.Body, FontStyles.Italic);

            var traits = DK.CardBox(cols[1], 14);
            ChipGroup(traits, "Nature", data.Tags, DK.ChipBg, DK.Body);
            ChipGroup(traits, "Disposition", data.InteractionTraits, DK.ChipBg, DK.Body);
            ChipGroup(traits, "Reputation (earned by behaviour)", data.ReputationTags, DK.WarmChip, DK.AccentText);

            var hist = DK.CardBox(_content, 4);
            CardHeader(hist, "Recent interactions with you", out var right);
            DK.Button(right, "Full history", DK.BtnStyle.Link, () => SwitchTab(Tab.Bonds), 32, -1, 13).rt.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(0, 0, 0, 0);
            HistoryRows(hist, data, 5);
        }

        private static void ChipGroup(Transform parent, string label, List<string> items, Color bg, Color fg)
        {
            var g = DK.VStack(parent, 8, null, "ChipGroup");
            DK.Label(g, label);
            if (items == null || items.Count == 0) { Empty(g, "None yet"); return; }
            var flow = DK.Flow(g);
            foreach (var s in items.Where(x => !string.IsNullOrWhiteSpace(x)))
                DK.Chip(flow, DK.Esc(s.Trim()), bg, fg);
        }

        private static void HistoryRows(Transform parent, NPCData data, int max)
        {
            if (data.InteractionHistory == null || data.InteractionHistory.Count == 0) { Empty(parent, "No interactions recorded yet."); return; }
            foreach (var h in data.InteractionHistory.Take(max))
            {
                DK.Rule(parent, DK.ChipBg);
                var row = DK.HStack(parent, 14, new RectOffset(0, 0, 8, 8), "HistoryRow");
                var t = DK.Text(row, DK.Esc(h), 14, DK.Body);
                DK.LE(t, flexW: 1, prefW: 0);
            }
        }

        // ─── Profile ──────────────────────────────────────────────────────────────

        private void BuildProfile()
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            bool hasNative = _npc.importantData != null && !string.IsNullOrEmpty(_npc.importantData.personality);

            var top = DK.HStack(_content, 12, null, "ProfileTop");
            if (hasNative) DK.Chip(top, "Synced with native character sheet", DK.PosChip, DK.PosText, 12, FontStyles.Bold);
            else DK.Chip(top, "Stored by NPC Expansion", DK.ChipBg, DK.Body, 12, FontStyles.Bold);
            DK.Spacer(top);

            var seg = DK.HStack(top, 0, new RectOffset(3, 3, 3, 3), "Mode");
            DK.Fill(seg, DK.Tile, 10, false);
            DK.Border(seg, DK.InputLine, 10);
            var bView = DK.Button(seg, "View", DK.BtnStyle.Link, () => { if (_editing) { _editing = false; BuildContent(); BuildFooter(); } }, 36, 72, 13, 8);
            var bEdit = DK.Button(seg, "Edit", DK.BtnStyle.Link, () => { if (!_editing) { _editing = true; BuildContent(); } }, 36, 72, 13, 8);
            if (_editing) { bEdit.bg.color = DK.Accent; bEdit.label.color = DK.OnAccent; bView.label.color = DK.Muted; }
            else { bView.bg.color = DK.InputLine; bView.label.color = DK.TextBright; bEdit.label.color = DK.Muted; }

            GenerationHintCard(_content, NPCData.HasProfile(_npc, data));

            var r1 = DK.Columns(_content, 16, 1, 1);
            FieldCard(r1[0], "personality", "Personality", true, 150);
            FieldCard(r1[1], "background", "Background / situation", true, 150);
            FieldCard(_content, "visual", "Visual description", true, 96);
            var r2 = DK.Columns(_content, 16, 1, 1);
            FieldCard(r2[0], "firstMessage", "First words", false, 96, italic: true);
            FieldCard(r2[1], "greetings", "Alternate greetings (one per line)", false, 96, italic: true);
            var r3 = DK.Columns(_content, 16, 1, 1);
            FieldCard(r3[0], "nature", "Nature tags (comma separated)", true, 72);
            FieldCard(r3[1], "disposition", "Disposition (one per line)", true, 72);

            // Advanced card fields
            var adv = DK.VStack(_content, 0, null, "Advanced");
            DK.Fill(adv, DK.Card, 12, false);
            DK.Border(adv, DK.Line, 12);
            var head = DK.Button(adv, (_advancedOpen ? "v   " : ">   ") + "Advanced card fields", DK.BtnStyle.Link,
                () => { _advancedOpen = !_advancedOpen; BuildContent(); }, 52, -1, 14, 12);
            head.label.color = DK.Ink;
            head.label.alignment = TextAlignmentOptions.MidlineLeft;
            var hl = head.rt.GetComponent<HorizontalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleLeft;
            hl.padding = new RectOffset(18, 18, 0, 0);
            DK.LE(head.label, flexW: 1);
            DK.Text(head.rt, "Message examples, system prompt, creator notes, post-history, extensions", 12, DK.Faint, wrap: false);

            if (_advancedOpen)
            {
                var body = DK.VStack(adv, 16, new RectOffset(18, 18, 4, 18), "AdvBody");
                var a1 = DK.Columns(body, 16, 1, 1);
                FieldCard(a1[0], "system", "System prompt", true, 110, flat: true);
                FieldCard(a1[1], "notes", "Creator notes", true, 110, flat: true);
                var a2 = DK.Columns(body, 16, 1, 1);
                FieldCard(a2[0], "examples", "Message examples", false, 110, flat: true);
                FieldCard(a2[1], "posthist", "Post-history instructions", false, 110, flat: true);
                FieldCard(body, "extensions", "Extensions (JSON object of strings)", false, 110, flat: true);
            }
        }

        /// <summary>A profile field: label row with badges, then an input (edit mode) or the text (view mode).</summary>
        private void FieldCard(Transform parent, string key, string label, bool inPrompt, float editHeight, bool italic = false, bool flat = false)
        {
            string value = _draft != null && _draft.text.TryGetValue(key, out var v) ? v : "";
            bool dirty = IsFieldDirty(key);

            var card = flat ? DK.VStack(parent, 8, null, "Field_" + key) : DK.VStack(parent, 10, new RectOffset(18, 18, 16, 16), "Field_" + key);
            Image border = null;
            if (!flat)
            {
                DK.Fill(card, DK.Card, 12, false);
                border = DK.Border(card, dirty ? DK.EditedLine : DK.Line, 12);
            }

            var head = DK.HStack(card, 8, null, "Head");
            DK.Label(head, label);
            if (inPrompt)
            {
                var c = DK.Chip(head, "IN PROMPT", DK.InfoChip, DK.Info, 10, FontStyles.Bold);
                c.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(6, 6, 2, 2);
                c.gameObject.AddComponent<DossierTooltip>().text = "Sent to the AI through GenContext";
                c.GetComponent<Image>().raycastTarget = true;
            }
            var edited = DK.Chip(head, "EDITED", DK.WarmChip, DK.AccentText, 10, FontStyles.Bold);
            edited.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(6, 6, 2, 2);
            edited.gameObject.SetActive(dirty);
            DK.Spacer(head);
            var count = DK.Text(head, $"{value.Length} chars", 11, DK.Dim, wrap: false);

            _fieldWidgets[key] = new FieldWidgets { editedChip = edited.gameObject, border = border, count = count };

            if (_editing)
            {
                DK.Input(card, value, "Empty", true, editHeight, s => OnFieldChanged(key, s));
            }
            else if (string.IsNullOrWhiteSpace(value)) Empty(card, "Empty");
            else DK.Text(card, DK.Esc(value), 14, DK.Body, italic ? FontStyles.Italic : FontStyles.Normal);
        }

        // ─── Mind ─────────────────────────────────────────────────────────────────

        private void BuildMind()
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            var cols = DK.Columns(_content, 16, 3, 2);

            var mem = DK.CardBox(cols[0], 12);
            CardHeader(mem, "Long-term memories", out var right);
            int wait = Math.Max(0, 10 - (ScenarioUpdater.GlobalTurn - data.MemorySynthesisTurn));
            DK.Text(right, wait == 0 ? "Synthesis due next turn" : $"Next synthesis in {wait} turn{(wait == 1 ? "" : "s")}", 12, DK.Faint, wrap: false);

            var list = _draft?.memories ?? new List<string>();
            if (list.Count == 0) Empty(mem, "No lasting memories yet.");
            // Oldest-first storage; show newest first.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                int index = i;
                var row = DK.HStack(mem, 12, new RectOffset(12, 6, 10, 10), "Memory", TextAnchor.UpperLeft);
                DK.Fill(row, DK.CardInner, 10, false);
                var t = DK.Text(row, DK.Esc(list[i]), 14, DK.Body);
                DK.LE(t, flexW: 1, prefW: 0);
                DK.IconButton(row, "X", DK.BtnStyle.Link, () =>
                {
                    if (_draft == null || index >= _draft.memories.Count) return;
                    _draft.memories.RemoveAt(index);
                    _savedFlash = false;
                    BuildTabBar(); BuildContent(); BuildFooter();
                }, 32, 12, "Forget this memory").label.color = DK.Faint;
            }

            var add = DK.HStack(mem, 8, null, "AddMemory");
            DK.Input(add, _newMemory, "Plant a memory...", false, 44, s => _newMemory = s);
            DK.Button(add, "Add", DK.BtnStyle.Secondary, () =>
            {
                string m = (_newMemory ?? "").Trim();
                if (m.Length == 0 || _draft == null) return;
                _draft.memories.Add(m);
                _newMemory = "";
                _savedFlash = false;
                BuildTabBar(); BuildContent(); BuildFooter();
            });
            DK.Text(mem, "The three most recent memories reach the AI. Adding or forgetting one counts as an unsaved change.", 12, DK.Faint);

            var goal = DK.CardBox(cols[1], 8);
            DK.Label(goal, "Current goal");
            if (string.IsNullOrEmpty(data.CurrentGoal)) Empty(goal, "No goal yet.");
            else
            {
                DK.Text(goal, DK.Esc(data.CurrentGoal), 15, DK.TextBright, FontStyles.Bold);
                if (!string.IsNullOrEmpty(data.GoalProgress)) DK.Text(goal, DK.Esc(data.GoalProgress), 13, DK.Muted);
            }

            var th = DK.CardBox(cols[1], 10);
            DK.Label(th, "Recent thoughts");
            if (data.RecentThoughts == null || data.RecentThoughts.Count == 0) Empty(th, "Nothing on their mind yet.");
            else foreach (var s in data.RecentThoughts)
                DK.Text(th, "“" + DK.Esc(s) + "”", 13, DK.Body, FontStyles.Italic);
        }

        // ─── Bonds ────────────────────────────────────────────────────────────────

        private void BuildBonds()
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            int aff = data.Affinity;

            var arc = DK.CardBox(_content, 16);
            DK.Label(arc, "Relationship arc");
            var stages = DK.HStack(arc, 8, null, "Stages", TextAnchor.UpperLeft, expandWidth: true);
            foreach (var s in ArcStages)
            {
                bool reached = aff >= s.at;
                var col = DK.VStack(stages, 8, null, "Stage");
                DK.LE(col, prefW: 0, flexW: 1);
                DK.Bar(col, reached ? 1f : 0f, DK.Accent, 6);
                var r = DK.HStack(col, 4, null, "StageHead", TextAnchor.LowerLeft);
                DK.Text(r, s.name, 13, reached ? DK.TextBright : DK.Faint, FontStyles.Bold, wrap: false);
                DK.Spacer(r);
                DK.Text(r, s.at + "+", 11, DK.Faint, wrap: false);
                DK.Text(col, s.unlock, 12, DK.Faint);
            }
            if (aff <= -20)
                DK.Text(arc, $"Currently <b>{RelationshipArcSystem.GetArcStage(data)}</b>. They hold you in contempt.", 13, DK.Neg);

            var ms = DK.VStack(arc, 0, null, "Milestones");
            if (data.ArcMilestones == null || data.ArcMilestones.Count == 0) Empty(ms, "No milestones yet.");
            else foreach (var m in data.ArcMilestones)
            {
                var match = MilestoneTurn.Match(m ?? "");
                string text = match.Success ? match.Groups[2].Value : m;
                string turn = match.Success ? "Turn " + match.Groups[1].Value : "";
                DK.Rule(ms, DK.ChipBg);
                var row = DK.HStack(ms, 14, new RectOffset(0, 0, 10, 10), "Milestone");
                DK.Text(row, "*", 14, DK.Accent, FontStyles.Bold, wrap: false);
                var t = DK.Text(row, DK.Esc(text), 14, DK.Body);
                DK.LE(t, flexW: 1, prefW: 0);
                DK.Text(row, turn, 12, DK.Faint, wrap: false);
            }

            var cols = DK.Columns(_content, 16, 1, 1);

            var net = DK.CardBox(cols[0], 12);
            DK.Label(net, "How they feel about others");
            var entries = (data.NpcAffinities ?? new Dictionary<string, int>())
                .Select(kv => (name: ResolveName(kv.Key), value: kv.Value))
                .Where(e => !string.IsNullOrEmpty(e.name) && e.value != 0)
                .OrderByDescending(e => Math.Abs(e.value))
                .Take(8)
                .ToList();
            if (entries.Count == 0) Empty(net, "No strong feelings about anyone else yet.");
            foreach (var e in entries)
            {
                var block = DK.VStack(net, 6, null, "Bond");
                var r = DK.HStack(block, 8, null, "BondHead");
                var n = DK.Text(r, DK.Esc(e.name), 13, DK.Ink, FontStyles.Bold, wrap: false);
                DK.LE(n, flexW: 1, prefW: 0);
                DK.Text(r, (e.value > 0 ? "+" : "") + e.value, 13, e.value >= 0 ? DK.Pos : DK.Neg, FontStyles.Bold, wrap: false);
                DK.CenterBar(block, e.value, 6);
            }

            var hist = DK.CardBox(cols[1], 4);
            DK.Label(hist, "Interaction history");
            HistoryRows(hist, data, 20);
        }

        private static string ResolveName(string uuid)
        {
            try
            {
                if (SS.I?.uuidToGameEntityMap != null && SS.I.uuidToGameEntityMap.TryGetValue(uuid, out var ent) && ent != null)
                    return ent.GetPrettyName();
            }
            catch { }
            return NPCData.LoreCache.TryGetValue(uuid, out var d) ? d?.Name : null;
        }

        // ─── Secrets ──────────────────────────────────────────────────────────────

        private void BuildSecrets()
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            int aff = data.Affinity;
            var secrets = data.Secrets ?? new List<NPCData.NPCSecret>();
            int hidden = secrets.Count(s => !s.IsRevealed);
            bool canAsk = aff >= 60 && NPCData.HasProfile(_npc, data) && IsAlive;
            bool busy = _busy.Contains("secret");

            var top = DK.HStack(_content, 12, null, "SecretsTop");
            string summary = secrets.Count == 0
                ? "No secrets uncovered yet. Asking may reveal whether they hide anything at all."
                : $"{secrets.Count - hidden} of {secrets.Count} revealed. Hidden secrets surface on their own at 80 affinity.";
            var st = DK.Text(top, summary, 14, DK.Muted);
            DK.LE(st, flexW: 1, prefW: 0);
            if (secrets.Count == 0 || hidden > 0)
            {
                var b = DK.Button(top, busy ? "Asking..." : "Ask about a secret", canAsk ? DK.BtnStyle.Primary : DK.BtnStyle.Locked, DoAskSecret,
                    tooltip: canAsk ? null : (aff < 60 ? "Needs Ally (60 affinity)" : "Needs a profile first"));
                b.SetInteractable(canAsk && !busy);
            }

            if (secrets.Count > 0)
            {
                var cols = DK.Columns(_content, 16, 1, 1);
                for (int i = 0; i < secrets.Count; i++)
                {
                    var s = secrets[i];
                    var parent = cols[i % 2];
                    if (s.IsRevealed)
                    {
                        var card = DK.CardBox(parent, 10);
                        var h = DK.HStack(card, 8, null, "Head");
                        DK.Label(h, s.Category ?? "Secret", DK.Accent);
                        DK.Spacer(h);
                        DK.Text(h, "Revealed", 12, DK.Faint, wrap: false);
                        DK.Text(card, DK.Esc(s.Text), 14, DK.Ink);
                        if (s.Category == "Ability")
                            DK.Text(card, "They may teach you this at Confidant (75).", 12, DK.Info);
                    }
                    else
                    {
                        var card = DK.CardBox(parent, 10, 20, 18, DK.Hex("#18191e"), DK.LineStrong);
                        var h = DK.HStack(card, 8, null, "Head");
                        DK.Label(h, s.Category ?? "Secret");
                        DK.Spacer(h);
                        DK.Text(h, "Hidden", 12, DK.Faint, wrap: false);
                        DK.Text(card, "Something they keep to themselves.", 14, DK.Faint, FontStyles.Italic);
                    }
                }
            }

            var cols2 = DK.Columns(_content, 16, 1, 1);
            var facts = DK.CardBox(cols2[0], 10);
            DK.Label(facts, "Rumours and facts they carry");
            if (data.KnownFacts == null || data.KnownFacts.Count == 0) Empty(facts, "They have not picked up any rumours.");
            else foreach (var f in data.KnownFacts.AsEnumerable().Reverse().Take(8)) Bullet(facts, f, DK.Accent);

            var taught = DK.CardBox(cols2[1], 10);
            DK.Label(taught, "Taught to you");
            var skill = NPCTeachingSystem.PlayerTaughtSkills?.FirstOrDefault(x => x.TeacherName == _npc.GetPrettyName());
            if (skill == null) Empty(taught, aff >= 75 ? "Ready to teach. Use Teach me on the left." : "Nothing yet. Confidants (75) can teach you one technique.");
            else
            {
                DK.Text(taught, DK.Esc(skill.SkillName), 15, DK.TextBright, FontStyles.Bold);
                if (!string.IsNullOrEmpty(skill.Description)) DK.Text(taught, DK.Esc(skill.Description), 13, DK.Muted);
                DK.Text(taught, $"Learned on turn {skill.TurnLearned}", 12, DK.Faint);
            }
        }

        // ─── Combat ───────────────────────────────────────────────────────────────

        private static readonly SS.PlayerAttribute[] AttrOrder =
        {
            SS.PlayerAttribute.Strength, SS.PlayerAttribute.Dexterity, SS.PlayerAttribute.Intellect,
            SS.PlayerAttribute.Cunning, SS.PlayerAttribute.Charisma
        };

        private void BuildCombat()
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            var attrs = _draft?.attrs ?? new Dictionary<SS.PlayerAttribute, long>();

            var keys = AttrOrder.Where(attrs.ContainsKey).Concat(attrs.Keys.Where(k => !AttrOrder.Contains(k))).ToList();
            long maxVal = Math.Max(20, attrs.Count > 0 ? attrs.Values.Max() : 20);

            if (!_editing)
            {
                var hint = DK.HStack(_content, 8, null, "AttrHint");
                var ht = DK.Text(hint, "Attributes can be adjusted in Edit mode.", 12, DK.Faint);
                DK.LE(ht, flexW: 1, prefW: 0);
                DK.Button(hint, "Edit", DK.BtnStyle.Link, () => { _editing = true; BuildContent(); }, 28);
            }

            var tiles = DK.HStack(_content, 12, null, "Attributes", TextAnchor.UpperLeft, expandWidth: true);
            foreach (var k in keys)
            {
                var key = k;
                var tile = DK.CardBox(tiles, 8, 16, 14);
                DK.LE(tile, prefW: 0, flexW: 1);
                bool dirty = _orig != null && (!_orig.attrs.TryGetValue(key, out var o) || o != attrs[key]);
                var name = DK.Text(tile, key.ToString(), 12, dirty ? DK.AccentText : DK.Muted, wrap: false);
                var vr = DK.HStack(tile, 8, null, "Value");
                if (_editing)
                    DK.IconButton(vr, "-", DK.BtnStyle.Ghost, () => { _draft.attrs[key] = Math.Max(1, _draft.attrs[key] - 1); _savedFlash = false; BuildContent(); BuildFooter(); }, 32, 16);
                var v = DK.Text(vr, attrs[key].ToString(), 26, DK.Ink, FontStyles.Bold, wrap: false);
                DK.LE(v, flexW: 1, prefW: 0);
                if (_editing)
                    DK.IconButton(vr, "+", DK.BtnStyle.Ghost, () => { _draft.attrs[key] = _draft.attrs[key] + 1; _savedFlash = false; BuildContent(); BuildFooter(); }, 32, 16);
                DK.Bar(tile, (float)attrs[key] / maxVal, DK.Accent, 4);
            }
            if (keys.Count == 0) Empty(_content, "No attributes generated yet.");

            var cols = DK.Columns(_content, 16, 3, 2);

            var ab = DK.CardBox(cols[0], 10);
            DK.Label(ab, "Abilities");
            if (data.DetailedAbilities == null || data.DetailedAbilities.Count == 0) Empty(ab, "No abilities generated yet.");
            else foreach (var a in data.DetailedAbilities)
            {
                var box = DK.VStack(ab, 4, new RectOffset(14, 14, 12, 12), "Ability");
                DK.Fill(box, DK.CardInner, 10, false);
                DK.Text(box, DK.Esc(a.Name), 14, DK.TextBright, FontStyles.Bold);
                if (!string.IsNullOrEmpty(a.Description) && a.Description != "No description provided.")
                    DK.Text(box, DK.Esc(a.Description), 13, DK.Muted);
            }

            var sk = DK.CardBox(cols[1], 6);
            DK.Label(sk, "Skills");
            if (data.Skills == null || data.Skills.Count == 0) Empty(sk, "No skills generated yet.");
            else foreach (var s in data.Skills.Values.Where(x => x != null))
            {
                DK.Rule(sk, DK.ChipBg);
                var r = DK.HStack(sk, 8, new RectOffset(0, 0, 6, 6), "Skill");
                string nm;
                try { nm = s.GetReadableSkillName(); } catch { nm = "Skill"; }
                var t = DK.Text(r, DK.Esc(nm), 14, DK.Body);
                DK.LE(t, flexW: 1, prefW: 0);
                DK.Text(r, "Lv " + s.level, 14, DK.Ink, FontStyles.Bold, wrap: false);
            }

            var eq = DK.CardBox(cols[1], 6);
            CardHeader(eq, "Equipped", out var right);
            if (IsAlive && !_npc.IsEnemyType())
                DK.Button(right, "Manage", DK.BtnStyle.Link, OpenEquipment, 28, -1, 13).rt.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(0, 0, 0, 0);
            var equipped = data.EquippedUuids ?? new Dictionary<string, string>();
            if (equipped.Count == 0) Empty(eq, "Nothing equipped.");
            foreach (var kv in equipped)
            {
                var item = _npc.items?.Find(i => i != null && i.uuid == kv.Value);
                DK.Rule(eq, DK.ChipBg);
                var r = DK.HStack(eq, 10, new RectOffset(0, 0, 6, 6), "Slot");
                DK.Text(r, SlotName(kv.Key), 13, DK.Faint, wrap: false);
                DK.Spacer(r);
                DK.Text(r, item != null ? DK.Esc(item.GetPrettyName()) : "Missing item", 13, item != null ? DK.Ink : DK.Dim, wrap: false, align: TextAlignmentOptions.TopRight);
            }
        }

        private static string SlotName(string slot)
        {
            switch (slot)
            {
                case "WEAPON1": return "Main hand";
                case "WEAPON2": return "Off hand";
                case "TORSO": return "Body";
                case "PANTS": return "Legs";
                case "NECKLACE": return "Neck";
                default: return slot.Length > 1 ? slot.Substring(0, 1) + slot.Substring(1).ToLowerInvariant() : slot;
            }
        }

        // ─── Quests ───────────────────────────────────────────────────────────────

        private void BuildQuests()
        {
            var data = Data ?? NPCData.CreateDefault(_npc.GetPrettyName());
            var quests = QuestsForNpc();
            bool canRequest = IsAlive && !_npc.IsEnemyType() && NPCData.HasProfile(_npc, data);
            bool busy = _busy.Contains("quest");

            var top = DK.HStack(_content, 10, null, "QuestTop");
            var st = DK.Text(top, quests.Count == 0 ? "They have not given you any quests." : $"{quests.Count(q => q.Status == QuestStatus.Active)} active, {quests.Count(q => q.Status != QuestStatus.Active)} finished", 14, DK.Muted);
            DK.LE(st, flexW: 1, prefW: 0);
            DK.Button(top, "Open quest log", DK.BtnStyle.Ghost, () => { var m = _manager; CloseNow(); QuestUI.Open(m); });
            var ask = DK.Button(top, busy ? "Asking..." : "Ask for a quest", canRequest ? DK.BtnStyle.Primary : DK.BtnStyle.Locked, DoRequestQuest,
                tooltip: canRequest ? null : "Needs a living, friendly NPC with a profile");
            ask.SetInteractable(canRequest && !busy);

            foreach (var q in quests)
            {
                var card = DK.HStack(_content, 20, new RectOffset(20, 20, 18, 18), "Quest", TextAnchor.UpperLeft);
                DK.Fill(card, DK.Card, 12, false);
                DK.Border(card, DK.Line, 12);

                var left = DK.VStack(card, 8, null, "QuestText");
                DK.LE(left, flexW: 1, prefW: 0);
                var badges = DK.HStack(left, 10, null, "Badges");
                switch (q.Status)
                {
                    case QuestStatus.Active: DK.Chip(badges, "ACTIVE", DK.WarmChip, DK.AccentText, 11, FontStyles.Bold); break;
                    case QuestStatus.Completed: DK.Chip(badges, "COMPLETED", DK.PosChip, DK.PosText, 11, FontStyles.Bold); break;
                    default: DK.Chip(badges, "FAILED", DK.NegChip, DK.Neg, 11, FontStyles.Bold); break;
                }
                if (!string.IsNullOrEmpty(q.ChainId))
                    DK.Text(badges, q.IsChainFinal ? $"Chain step {q.ChainStep + 1} (final)" : $"Chain step {q.ChainStep + 1}", 12, DK.Info, wrap: false);
                if (q.Status == QuestStatus.Active && q.TurnDeadline > 0)
                    DK.Text(badges, $"Due by turn {q.TurnDeadline}", 12, q.TurnDeadline - ScenarioUpdater.GlobalTurn <= 3 ? DK.Neg : DK.Faint, wrap: false);
                DK.Spacer(badges);

                DK.Text(left, DK.Esc(q.ObjectiveText), 20, DK.TextBright, FontStyles.Bold, display: true);
                if (!string.IsNullOrEmpty(q.CompletionCondition)) DK.Text(left, "Done when: " + DK.Esc(q.CompletionCondition), 13, DK.Muted);
                if (!string.IsNullOrEmpty(q.RewardText)) DK.Text(left, DK.Esc(q.RewardText), 13, DK.Faint, FontStyles.Italic);
                if (!string.IsNullOrEmpty(q.CompletionNotes)) DK.Text(left, DK.Esc(q.CompletionNotes), 13, DK.Body);

                var right = DK.VStack(card, 4, null, "Reward", TextAnchor.UpperRight);
                DK.LE(right, prefW: 140, minW: 140);
                DK.Text(right, "REWARD", 11, DK.Faint, FontStyles.Bold, wrap: false, align: TextAlignmentOptions.TopRight);
                DK.Text(right, q.RewardGold > 0 ? $"{q.RewardGold} gold" : "No gold", 18, DK.Accent, FontStyles.Bold, wrap: false, align: TextAlignmentOptions.TopRight);
                DK.Text(right, $"+{q.RewardAffinity} affinity", 12, DK.PosText, wrap: false, align: TextAlignmentOptions.TopRight);
            }

            if (quests.Count > 0)
                DK.Text(_content, "Finishing a quest can lead to a follow-up; the last step of a chain counts toward your relationship arc.", 13, DK.Faint);
        }

        // ─── Autonomy ─────────────────────────────────────────────────────────────

        private void BuildAutonomy()
        {
            var data = Data;
            if (data == null)
            {
                Empty(_content, "Generate a profile first; autonomy settings live in the NPC's profile.");
                return;
            }

            DK.Text(_content, $"What {DK.Esc(_npc.GetPrettyName())} may do on their own between your turns. Changes apply immediately.", 14, DK.Muted);

            AutonomyRow("Auto-equip gear", "Swaps in better weapons and armour from their own inventory.",
                () => data.AllowAutoEquip, v => data.AllowAutoEquip = v, false);
            AutonomyRow("Self-preservation", "Heals or retreats when badly hurt instead of fighting to the end.",
                () => data.AllowSelfPreservation, v => data.AllowSelfPreservation = v, false);
            AutonomyRow("Economic activity", "Sells surplus and trades with other merchants over time.",
                () => data.AllowEconomicActivity, v => data.AllowEconomicActivity = v, false);
            AutonomyRow("World interaction", "Picks up, uses and moves objects in their location.",
                () => data.AllowWorldInteraction, v => data.AllowWorldInteraction = v, false);
            AutonomyRow("Nemesis", "Marks them as a recurring antagonist. Normally set by the game when an enemy defeats you.",
                () => data.IsNemesis, v => data.IsNemesis = v, true);
        }

        private void AutonomyRow(string title, string desc, Func<bool> get, Action<bool> set, bool danger)
        {
            bool on = get();
            var row = DK.HStack(_content, 18, new RectOffset(20, 20, 16, 16), "Autonomy", TextAnchor.MiddleLeft);
            DK.Fill(row, DK.Card, 12, false);
            var border = DK.Border(row, on && danger ? DK.Hex("#6b3a2c") : DK.Line, 12);

            var text = DK.VStack(row, 4, null, "Text");
            DK.LE(text, flexW: 1, prefW: 0);
            DK.Text(text, title, 15, DK.TextBright, FontStyles.Bold);
            DK.Text(text, desc, 13, DK.Muted);

            var state = DK.Text(row, on ? "On" : "Off", 12, on ? (danger ? DK.Neg : DK.PosText) : DK.Faint, FontStyles.Bold, wrap: false, align: TextAlignmentOptions.MidlineRight);
            DK.LE(state, prefW: 30);

            DK.Switch sw = null;
            sw = DK.Toggle(row, on, danger, () =>
            {
                var npc = _npc;
                if (npc == null) return;
                bool next = !get();
                set(next);
                var d = NPCData.Load(npc.uuid);
                if (d != null)
                {
                    NPCData.Save(npc.uuid, d);
                    NPCData.FlushSessionLore();
                }
                sw.Set(next);
                state.text = next ? "On" : "Off";
                state.color = next ? (danger ? DK.Neg : DK.PosText) : DK.Faint;
                border.color = next && danger ? DK.Hex("#6b3a2c") : DK.Line;
                if (danger) BuildHeader(); // Nemesis chip
            });
        }
    }
}
