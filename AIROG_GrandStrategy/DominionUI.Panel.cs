using System;
using System.Linq;
using AIROG_WorldExpansion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_GrandStrategy
{
    // Panel construction and content population (status, domain orders, council,
    // diplomacy/war, petitions).
    public static partial class DominionUI
    {
        private static void ClearPanel(MapModal modal)
        {
            Transform old = modal.mapViewTrans?.Find(PANEL_NAME);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
        }

        private static void BuildPanel(MapModal modal)
        {
            ClearPanel(modal);
            if (modal.mapViewTrans == null || modal.manager == null) return;
            TMP_FontAsset font = modal.voronoiWorldTitle != null ? modal.voronoiWorldTitle.font : null;

            GameObject panel = new GameObject(PANEL_NAME, typeof(RectTransform));
            panel.layer = modal.mapViewTrans.gameObject.layer;
            panel.transform.SetParent(modal.mapViewTrans, false);
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = new Vector2(0, 0.5f);   // POL legend owns the right edge; we take the left
            prt.anchorMax = new Vector2(0, 0.5f);
            prt.pivot     = new Vector2(0, 0.5f);
            prt.anchoredPosition = new Vector2(14, 0);
            prt.sizeDelta = new Vector2(370, 100);

            panel.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.09f, 0.94f);
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.spacing = 5;
            vlg.padding = new RectOffset(12, 12, 10, 10);
            panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            try { Populate(modal, panel, font); }
            catch (Exception e) { Debug.LogError($"[GrandStrategy] Dominion panel populate failed: {e}"); }
        }

        private static void Populate(MapModal modal, GameObject panel, TMP_FontAsset font)
        {
            GameplayManager manager = modal.manager;
            var s = GrandStrategyData.State;
            Themes.EnsureTheme(s, manager); // legacy saves: pick a theme the first time the panel opens
            var L = GrandStrategyData.L;
            string cs = L.CurrencyShort;

            if (!s.Founded)
            {
                AddText(panel, font, $"<b>{L.Icon} DOMINION</b>", 19, GOLD);
                AddText(panel, font,
                    "You rule no dominion yet. Found one here — your current region becomes the capital (it must be unclaimed).",
                    13, MUTED);
                var frow = AddRow(panel, 30);
                AddButton(frow, font, "FOUND DOMINION HERE", () =>
                {
                    string pname = null;
                    try { pname = SS.I?.hackyManager?.playerCharacter?.name; } catch { }
                    string dname = string.IsNullOrWhiteSpace(pname) ? "New Dominion" : $"Dominion of {pname}";
                    _lastResult = DominionManager.FoundDominion(manager, dname);
                    Click(modal);
                    BuildPanel(modal);
                }, BTN_WARM);
                AddText(panel, font, "<i>(GS_FOUND <name> in the console picks a custom name)</i>", 11, new Color(0.55f, 0.55f, 0.6f));

                // Usurpation: factions whose throne the player could claim right now
                var usurpable = new System.Collections.Generic.List<Faction>();
                try
                {
                    foreach (var f in manager.GetCurrentFactions() ?? new System.Collections.Generic.List<Faction>())
                    {
                        if (f == null || f.GetPrettyName() == "Player") continue;
                        if (WorldData.CurrentState.EliminatedFactions.Contains(f.uuid)) continue;
                        var fd = WorldData.GetFactionData(f.uuid);
                        if (DominionManager.CanUsurp(f, fd, out _)) usurpable.Add(f);
                        if (usurpable.Count >= 3) break;
                    }
                }
                catch { }
                if (usurpable.Count > 0)
                {
                    AddText(panel, font, "Or claim an existing throne:", 13, MUTED);
                    foreach (var f in usurpable)
                    {
                        string fname = f.GetPrettyName();
                        var urow = AddRow(panel, 28);
                        AddButton(urow, font, $"USURP {fname.ToUpperInvariant()}", () =>
                        {
                            _lastResult = DominionManager.UsurpFaction(manager, fname);
                            Click(modal);
                            BuildPanel(modal);
                        }, BTN_WARM);
                    }
                }
                else
                {
                    AddText(panel, font,
                        "<i>Or usurp an existing faction: reach REVERED standing, or strike while their leader lies freshly slain (GS_USURP)</i>",
                        11, new Color(0.55f, 0.55f, 0.6f));
                }
                AddResult(panel, font);
                return;
            }

            var fac = WorldData.GetFactionData(s.FactionUuid);

            // ── Status ──
            AddText(panel, font, $"<b>{L.Icon} {s.DominionName.ToUpperInvariant()}</b>", 19, GOLD);
            AddText(panel, font,
                $"Treasury {s.Treasury}{cs} · Army {s.ArmyStrength} · CP {s.CommandPoints}/{s.MaxCommandPoints} · Pop {fac.Population}",
                14, MUTED);

            int unrestTotal = s.Holdings.Values.Sum(h => h.Unrest);
            AddText(panel, font,
                $"Holdings {s.Holdings.Count} · Vassals {s.VassalNames.Count} · Unrest {unrestTotal}"
                + (string.IsNullOrEmpty(s.ActiveVictory) ? "" : $" · <color=#FFD34D>★ {s.ActiveVictory}</color>"),
                13, MUTED);

            var wars = WorldData.CurrentState.ActiveWars.Values
                .Where(w => w.ActorUuid == s.FactionUuid || w.TargetUuid == s.FactionUuid)
                .Select(w => w.ActorUuid == s.FactionUuid ? w.TargetName : w.ActorName)
                .ToList();
            if (wars.Count > 0)
                AddText(panel, font, $"<color=#FF7766>⚔ At war: {string.Join(", ", wars)}</color>", 13, Color.white);

            var worstHolding = s.Holdings.Values.OrderByDescending(h => h.Unrest).FirstOrDefault();
            if (worstHolding != null && worstHolding.Unrest >= 20)
                AddText(panel, font, $"<color=#FFAA55>⚠ Unrest brewing in {worstHolding.Name} ({worstHolding.Unrest})</color>", 13, Color.white);

            if (s.Advisors.Count > 0)
                AddText(panel, font,
                    $"Council: {string.Join(", ", s.Advisors.Select(a => $"{a.Name} ({L.RoleTitle(a.Role).ToLower()})"))}",
                    12, MUTED);

            if (!string.IsNullOrEmpty(s.WonderInProgress))
            {
                AddText(panel, font,
                    $"Building: {OrderSystem.WonderDisplayName(s.WonderInProgress)} — {s.WonderTicksLeft} tick(s) left",
                    13, new Color(0.75f, 0.85f, 1f));
            }

            // ── Domain orders ──
            AddText(panel, font, $"── {L.DomainNoun.ToUpperInvariant()} ──", 12, DIVIDER);
            var r1 = AddRow(panel);
            AddButton(r1, font, string.IsNullOrEmpty(_pickedAnnexUuid) ? $"ANNEX 25{cs}" : "ANNEX ▸ picked",
                () => DoOrder(modal, "ANNEX", "", _pickedAnnexUuid));
            AddButton(r1, font, _pickMode == PickMode.Annex ? "🎯 CLICK MAP…" : "🎯 pick",
                () =>
                {
                    _pickMode = _pickMode == PickMode.Annex ? PickMode.None : PickMode.Annex;
                    _lastResult = _pickMode == PickMode.Annex
                        ? "Click an unclaimed place on the map to target ANNEX." : "";
                    Click(modal);
                    BuildPanel(modal);
                }, new Color(0.20f, 0.16f, 0.28f, 0.95f));
            AddButton(r1, font, "TRADE",         () => DoOrder(modal, "TRADE", ""));
            AddButton(r1, font, "DISBAND",       () => DoOrder(modal, "DISBAND", ""));
            var r2 = AddRow(panel);
            AddButton(r2, font, "LEVY",          () => DoOrder(modal, "LEVY", ""));
            AddButton(r2, font, $"FESTIVAL 25{cs}",  () => DoOrder(modal, "FESTIVAL", ""));

            // Holding cycle: determines which holding DEVELOP targets (default: capital)
            var holdingList = s.Holdings.Values.ToList();
            string selectedHolding = holdingList.Count > 0
                ? holdingList[_holdingIdx % holdingList.Count].Name
                : s.CapitalName;
            bool multiHolding = holdingList.Count > 1;

            string imp = OrderSystem.Improvements[_impIdx % OrderSystem.Improvements.Length];
            // Syntax passed to ResolveOrder: "IMP" for capital, "IMP holdingName" for others
            string devArg = selectedHolding == s.CapitalName
                ? imp
                : $"{imp} {selectedHolding}";

            var r3 = AddRow(panel);
            AddButton(r3, font, $"{imp} ▸", () => { _impIdx++; Click(modal); BuildPanel(modal); });
            if (multiHolding)
                AddButton(r3, font, $"@ {selectedHolding} ▸",
                    () => { _holdingIdx++; Click(modal); BuildPanel(modal); },
                    new Color(0.12f, 0.20f, 0.16f, 0.95f));
            AddButton(r3, font, $"DEVELOP 30{cs}", () => DoOrder(modal, "DEVELOP", devArg));

            var wd = OrderSystem.WonderDefs[_wonderIdx % OrderSystem.WonderDefs.Count];
            var r4 = AddRow(panel);
            AddButton(r4, font, $"{OrderSystem.WonderDisplayName(wd.Key)} ▸", () => { _wonderIdx++; Click(modal); BuildPanel(modal); });
            AddButton(r4, font, $"PROJECT {wd.Gold}{cs}", () => DoOrder(modal, "PROJECT", wd.Key));

            var r5 = AddRow(panel);
            AddButton(r5, font, $"TAX: {s.TaxPolicy} ▸", () =>
            {
                s.TaxPolicy = s.TaxPolicy == "LOW" ? "NORMAL" : s.TaxPolicy == "NORMAL" ? "HIGH" : "LOW";
                GrandStrategyData.LogDeed($"{s.DominionName} set {s.TaxPolicy.ToLower()} taxation across its {L.DomainNoun}.");
                GrandStrategyData.SaveToCurrentDir();
                Click(modal);
                BuildPanel(modal);
            });
            AddButton(r5, font, $"THEME: {L.Key} ▸", () =>
            {
                // Cycle terminology presets; the world's native currency name is re-applied each time
                int idx = Array.IndexOf(Themes.Keys, L.Key);
                string next = Themes.Keys[(idx + 1 + Themes.Keys.Length) % Themes.Keys.Length];
                Themes.Apply(s, next, manager);
                GrandStrategyData.SaveToCurrentDir();
                Click(modal);
                BuildPanel(modal);
            }, new Color(0.20f, 0.16f, 0.28f, 0.95f));

            // ── Council ──
            var role = OrderSystem.AdvisorRoles[_advisorRoleIdx % OrderSystem.AdvisorRoles.Length];
            var r6 = AddRow(panel);
            AddButton(r6, font, $"{L.RoleTitle(role).ToUpperInvariant()} ▸", () => { _advisorRoleIdx++; Click(modal); BuildPanel(modal); },
                new Color(0.12f, 0.20f, 0.16f, 0.95f));
            AddButton(r6, font, $"COUNCIL 40{cs}", () => DoOrder(modal, "COUNCIL", role));

            // ── Targeted orders (Diplomacy & War) ──
            var targets = EligibleTargets(manager);
            string tName = targets.Count > 0 ? targets[_targetIdx % targets.Count].GetPrettyName() : "";
            AddText(panel, font, "── DIPLOMACY & WAR ──", 12, DIVIDER);
            var tr = AddRow(panel);
            AddButton(tr, font, targets.Count > 0 ? $"TARGET: {tName} ▸" : "TARGET: (no factions)",
                () => { _targetIdx++; Click(modal); BuildPanel(modal); },
                new Color(0.12f, 0.20f, 0.16f, 0.95f));
            // Also show SCOUT inline with target selector
            AddButton(tr, font, $"SCOUT 15{cs}", () => DoOrder(modal, "SCOUT", tName));

            var d1 = AddRow(panel);
            AddButton(d1, font, $"ENVOY 20{cs}",      () => DoOrder(modal, "ENVOY", tName));
            AddButton(d1, font, "FABRICATE 2CP",  () => DoOrder(modal, "FABRICATE", tName));
            AddButton(d1, font, $"PEACE 25{cs}",      () => DoOrder(modal, "PEACE", tName));
            var d1b = AddRow(panel);
            AddButton(d1b, font, $"PACT 15{cs}",       () => DoOrder(modal, "PACT", tName));
            AddButton(d1b, font, $"TRADE_DEAL 20{cs}", () => DoOrder(modal, "TRADE_DEAL", tName));
            var d2 = AddRow(panel);
            AddButton(d2, font, "WAR 2CP",      () => DoOrder(modal, "WAR", tName), BTN_WARM);
            AddButton(d2, font, string.IsNullOrEmpty(_pickedCampaignUuid) ? "CAMPAIGN 2CP" : "CAMPAIGN ▸ picked",
                () => DoOrder(modal, "CAMPAIGN", tName, _pickedCampaignUuid), BTN_WARM);
            AddButton(d2, font, _pickMode == PickMode.Campaign ? "🎯 CLICK MAP…" : "🎯 pick",
                () =>
                {
                    _pickMode = _pickMode == PickMode.Campaign ? PickMode.None : PickMode.Campaign;
                    _lastResult = _pickMode == PickMode.Campaign
                        ? "Click an enemy-held place on the map to target CAMPAIGN." : "";
                    Click(modal);
                    BuildPanel(modal);
                }, new Color(0.20f, 0.16f, 0.28f, 0.95f));
            AddButton(d2, font, "PILLAGE 2CP",  () => DoOrder(modal, "PILLAGE", tName), BTN_WARM);
            var d3 = AddRow(panel);
            AddButton(d3, font, $"INCITE 30{cs}",    () => DoOrder(modal, "INCITE", tName));
            AddButton(d3, font, "SABOTAGE 2CP",  () => DoOrder(modal, "SABOTAGE", tName));
            AddButton(d3, font, "VASSAL 2CP",    () => DoOrder(modal, "VASSAL", tName));

            // ── Petition ──
            if (s.PendingPetition != null)
            {
                AddText(panel, font, $"── {L.PetitionNoun.ToUpperInvariant()} ──", 12, DIVIDER);
                AddText(panel, font, s.PendingPetition.Text, 13, new Color(0.92f, 0.88f, 0.75f));
                var pr = AddRow(panel);
                AddButton(pr, font, "ACCEPT", () => DoPetition(modal, true));
                AddButton(pr, font, "REJECT", () => DoPetition(modal, false));
            }

            AddResult(panel, font);
        }
    }
}
