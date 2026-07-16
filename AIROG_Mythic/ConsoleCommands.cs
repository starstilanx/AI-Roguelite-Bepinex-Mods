using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Mythic
{
    /// <summary>
    /// Console commands:
    ///   MYTHIC                       — status
    ///   MYTHIC LOG                   — recent director/oracle log
    ///   MYTHIC CF &lt;1-9&gt;              — set the Chaos Factor
    ///   MYTHIC EVENT                 — force-fire a director event
    ///   MYTHIC ASK &lt;odds&gt; &lt;question&gt; — Fate Chart oracle; ruling is injected as fact
    /// </summary>
    [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
    public static class ConsoleCommands
    {
        [HarmonyPrefix]
        public static bool Prefix(string txt, GameplayManager __instance)
        {
            string raw = (txt ?? "").Trim();
            string upper = raw.ToUpperInvariant();
            if (upper != "MYTHIC" && !upper.StartsWith("MYTHIC ")) return true;

            try
            {
                if (upper == "MYTHIC") { ShowStatus(); return false; }
                if (upper == "MYTHIC LOG") { ShowLog(); return false; }
                if (upper == "MYTHIC EVENT")
                {
                    string log = RandomEventEngine.FireEvent(__instance, forced: true);
                    Modal(log + "\n\nIt will color the next few AI generations.");
                    return false;
                }
                if (upper.StartsWith("MYTHIC CF"))
                {
                    string arg = raw.Substring("MYTHIC CF".Length).Trim();
                    if (int.TryParse(arg, out int cf) && cf >= 1 && cf <= 9)
                    {
                        int old = MythicData.State.ChaosFactor;
                        MythicData.State.ChaosFactor = cf;
                        MythicData.LogLine($"CF set by hand: {old} -> {cf}");
                        Modal($"Chaos Factor: {old} -> {cf} (effective {ChaosEngine.EffectiveCF()})");
                    }
                    else Modal("Usage: MYTHIC CF <1-9>");
                    return false;
                }
                if (upper.StartsWith("MYTHIC ASK"))
                {
                    Ask(raw.Substring("MYTHIC ASK".Length).Trim(), __instance);
                    return false;
                }

                ShowStatus();
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Mythic] Console command failed: " + ex);
                return false;
            }
        }

        // ── MYTHIC ASK ──────────────────────────────────────────────────────────

        private static void Ask(string args, GameplayManager manager)
        {
            string[] parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            int odds = parts.Length > 0 ? OracleTables.ParseOdds(parts[0]) : -1;
            string question = parts.Length > 1 ? parts[1].Trim() : "";

            if (odds < 0 || question.Length == 0)
            {
                Modal("Usage: MYTHIC ASK <odds> <question>\n\nOdds: impossible, noway, veryunlikely, unlikely, " +
                      "5050, somewhat, likely, verylikely, nearsure, sure, hasto\n\n" +
                      "Example: MYTHIC ASK likely Is the merchant still in the city?");
                return;
            }

            var st = MythicData.State;
            int cf = ChaosEngine.EffectiveCF();
            int t = OracleTables.Threshold(odds, cf);
            int roll = UnityEngine.Random.Range(1, 101);
            string result = OracleTables.ResolveFate(odds, cf, roll, out bool yes, out bool exceptional);

            st.PendingRulings.Add(new OracleRuling
            {
                Question = question,
                Result = result,
                ExpiresTurn = st.CurrentTurn + 2
            });
            while (st.PendingRulings.Count > 3) st.PendingRulings.RemoveAt(0);
            st.TotalAsks++;
            MythicData.LogLine($"ASK [{OracleTables.OddsNames[odds]}, CF {cf}] \"{question}\" -> roll {roll} vs {t} = {result}");

            var sb = new StringBuilder();
            sb.AppendLine($"ORACLE — \"{question}\"");
            sb.AppendLine($"Odds: {OracleTables.OddsNames[odds]} | CF {cf} | YES on 1-{t}");
            sb.AppendLine($"Roll: {roll}  ->  {result}");
            int ey = OracleTables.ExceptionalYesMax(t);
            int en = OracleTables.ExceptionalNoMin(t);
            sb.AppendLine($"(Exceptional: YES <= {ey}{(en <= 100 ? $", NO >= {en}" : "")})");

            // The ASK roll itself can also stir the world (doubles <= CF).
            if (OracleTables.IsEventTrigger(roll, cf))
            {
                string evLog = RandomEventEngine.FireEvent(manager, forced: false, triggerRoll: roll);
                sb.AppendLine();
                sb.AppendLine("The roll stirred something else: " + evLog);
            }

            sb.AppendLine();
            sb.AppendLine("The ruling is now established fact — the AI will honor it over the next couple of turns.");
            Modal(sb.ToString());
        }

        // ── Status / log ────────────────────────────────────────────────────────

        private static void ShowStatus()
        {
            var st = MythicData.State;
            int eff = ChaosEngine.EffectiveCF();
            var sb = new StringBuilder();
            sb.AppendLine($"MYTHIC DIRECTOR — Chaos Factor {st.ChaosFactor}/9" +
                          (eff != st.ChaosFactor ? $" (effective {eff}, variation: {MythicPlugin.CfgChaosVariation.Value})" : "") +
                          $" — register {ChaosEngine.RegisterTier(eff)}");
            sb.AppendLine($"Scene {st.SceneNumber}: {st.ScenePlaceName ?? "?"} — control score " +
                          $"{(st.SceneControlScore >= 0 ? "+" : "")}{st.SceneControlScore}, kill credits {st.SceneKillCredits}, " +
                          $"events this scene {st.SceneEventCount}");
            sb.AppendLine($"Turn {st.CurrentTurn} — {st.TotalEvents} director events, {st.TotalAsks} oracle questions all-time");

            var live = st.PendingEvents.Where(e => st.CurrentTurn <= e.ExpiresTurn).ToList();
            if (live.Count > 0)
            {
                sb.AppendLine("Pending events (being woven into upcoming narration):");
                foreach (var ev in live)
                    sb.AppendLine($"  [{ev.Focus}] '{ev.Meaning}' — expires T{ev.ExpiresTurn}");
            }
            var rulings = st.PendingRulings.Where(r => st.CurrentTurn <= r.ExpiresTurn).ToList();
            foreach (var r in rulings)
                sb.AppendLine($"Pending ruling: \"{r.Question}\" -> {r.Result}");

            sb.AppendLine();
            sb.AppendLine("Commands: MYTHIC LOG | MYTHIC CF <1-9> | MYTHIC EVENT | MYTHIC ASK <odds> <question>");
            Modal(sb.ToString());
        }

        private static void ShowLog()
        {
            var log = MythicData.State.EventLog;
            string body = log.Count == 0
                ? "Nothing yet — the world is holding its breath."
                : string.Join("\n", log.Skip(Math.Max(0, log.Count - 15)));
            Modal(body);
        }

        private static void Modal(string text)
        {
            MessageModal.I.ShowModal(text, false, true);
        }
    }
}
