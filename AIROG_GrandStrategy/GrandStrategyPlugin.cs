using System;
using System.IO;
using System.Linq;
using AIROG_WorldExpansion;
using BepInEx;
using HarmonyLib;

namespace AIROG_GrandStrategy
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency(WorldExpansionPlugin.PLUGIN_GUID, BepInDependency.DependencyFlags.HardDependency)]
    public class GrandStrategyPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.grandstrategy";
        public const string PLUGIN_NAME = "Grand Strategy";
        public const string PLUGIN_VERSION = "0.4.0";

        public static GrandStrategyPlugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");

            Harmony.CreateAndPatchAll(typeof(GrandStrategyPlugin));
            Harmony.CreateAndPatchAll(typeof(DominionManager));
            Harmony.CreateAndPatchAll(typeof(DominionUI));
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        [HarmonyPostfix]
        public static void Postfix_WriteSaveFile(GameplayManager manager, bool clean)
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                GrandStrategyData.Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
        }

        [HarmonyPatch(typeof(GameplayManager), "LoadGame")]
        [HarmonyPostfix]
        public static void Postfix_LoadGame(GameplayManager __instance)
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                GrandStrategyData.Load(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
        }

        [HarmonyPatch(typeof(MainMenu), "NewGame")]
        [HarmonyPostfix]
        public static void Postfix_NewGame(MainMenu __instance)
        {
            GrandStrategyData.Reset();
        }

        [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
        [HarmonyPrefix]
        public static bool Prefix_ProcessConsoleCommand(string txt, GameplayManager __instance)
        {
            string cmd = (txt ?? "").Trim();
            string upper = cmd.ToUpperInvariant();

            if (upper == "GS_STATUS")
            {
                __instance.MessageModal().ShowModal(BuildStatus(), false, true);
                return false;
            }
            if (upper == "GS_ORDERS")
            {
                string list = string.Join("\n", OrderSystem.Defs.Select(d =>
                    $"[{d.Cp} CP{(d.Gold > 0 ? $" + {d.Gold}g" : "")}] {d.Usage}"));
                __instance.MessageModal().ShowModal(
                    $"Decrees of the realm (GS_ORDER <TYPE> [target]):\n{list}", false, true);
                return false;
            }
            if (upper.StartsWith("GS_FOUND"))
            {
                string name = cmd.Length > 8 ? cmd.Substring(8).Trim() : "";
                __instance.MessageModal().ShowModal(DominionManager.FoundDominion(__instance, name), false, true);
                return false;
            }
            if (upper.StartsWith("GS_RENAME"))
            {
                var s = GrandStrategyData.State;
                if (!s.Founded) { __instance.MessageModal().ShowModal("No dominion founded yet.", false, true); return false; }
                string name = cmd.Length > 9 ? cmd.Substring(9).Trim() : "";
                if (string.IsNullOrWhiteSpace(name))
                {
                    __instance.MessageModal().ShowModal("Usage: GS_RENAME <new name>", false, true);
                    return false;
                }
                string old = s.DominionName;
                s.DominionName = name.Trim();
                GrandStrategyData.LogDeed($"{old} was renamed {s.DominionName} by decree of its sovereign.");
                GrandStrategyData.SaveToCurrentDir();
                __instance.MessageModal().ShowModal($"{old} shall henceforth be known as {s.DominionName}.", false, true);
                return false;
            }
            if (upper.StartsWith("GS_ORDER"))
            {
                string rest = cmd.Length > 8 ? cmd.Substring(8).Trim() : "";
                string[] parts = rest.Split(new[] { ' ' }, 2);
                string type = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
                string arg  = parts.Length > 1 ? parts[1] : "";
                __instance.MessageModal().ShowModal(OrderSystem.Issue(__instance, type, arg), false, true);
                return false;
            }
            if (upper.StartsWith("GS_TAX"))
            {
                var s = GrandStrategyData.State;
                if (!s.Founded) { __instance.MessageModal().ShowModal("No dominion founded yet.", false, true); return false; }
                string pol = upper.Length > 6 ? upper.Substring(6).Trim() : "";
                if (pol != "LOW" && pol != "NORMAL" && pol != "HIGH")
                {
                    __instance.MessageModal().ShowModal(
                        $"Current tax edict: {s.TaxPolicy}\nGS_TAX <LOW|NORMAL|HIGH>\n" +
                        "  LOW — half income, people grow content (−2 unrest/tick)\n" +
                        "  NORMAL — standard levies\n" +
                        "  HIGH — +50% income, the people seethe (+3 unrest/tick)", false, true);
                    return false;
                }
                s.TaxPolicy = pol;
                GrandStrategyData.LogDeed($"{s.DominionName} decreed {pol.ToLower()} taxation across the realm.");
                GrandStrategyData.SaveToCurrentDir();
                __instance.MessageModal().ShowModal($"Tax edict set to {pol}. The realm will feel it each strategic tick.", false, true);
                return false;
            }
            if (upper.StartsWith("GS_PETITION"))
            {
                var s = GrandStrategyData.State;
                var p = s.PendingPetition;
                string choice = upper.Length > 11 ? upper.Substring(11).Trim() : "";
                if (p == null)
                {
                    __instance.MessageModal().ShowModal("No petition awaits your judgment.", false, true);
                    return false;
                }
                if (choice != "ACCEPT" && choice != "REJECT")
                {
                    __instance.MessageModal().ShowModal(
                        $"A petition awaits the sovereign:\n\n{p.Text}\n\nGS_PETITION ACCEPT or GS_PETITION REJECT", false, true);
                    return false;
                }
                string outcome = CourtSystem.Resolve(s, choice == "ACCEPT");
                if (outcome != null && outcome.StartsWith("!")) outcome = outcome.Substring(1);
                __instance.MessageModal().ShowModal(outcome ?? "No petition awaits your judgment.", false, true);
                return false;
            }
            if (upper == "GS_TICK")
            {
                DominionManager.StrategicTick(__instance);
                __instance.MessageModal().ShowModal("Strategic tick forced.\n\n" + BuildStatus(), false, true);
                return false;
            }
            if (upper == "GS_CP")
            {
                GrandStrategyData.State.CommandPoints = GrandStrategyData.State.MaxCommandPoints;
                GrandStrategyData.State.Treasury += 100;
                __instance.MessageModal().ShowModal("Command points refilled, +100 treasury (test).", false, true);
                return false;
            }
            return true;
        }

        private static string BuildStatus()
        {
            var s = GrandStrategyData.State;
            if (!s.Founded)
                return "No dominion founded. Travel to unclaimed land and use GS_FOUND <name>.";

            var fac = WorldData.GetFactionData(s.FactionUuid);
            string holdings = s.Holdings.Count > 0
                ? string.Join("\n", s.Holdings.Values.Select(h =>
                    $"  {h.Name}{(h.IsCapital ? " ★" : "")}" +
                    (h.Improvements.Count > 0 ? $" ({string.Join(", ", h.Improvements.Select(i => i.ToLower()))})" : "") +
                    (h.Unrest > 0 ? $" — unrest {h.Unrest}" : "")))
                : "  none";
            string wars = string.Join(", ", WorldData.CurrentState.ActiveWars.Values
                .Where(w => w.ActorUuid == s.FactionUuid || w.TargetUuid == s.FactionUuid)
                .Select(w => w.ActorUuid == s.FactionUuid ? w.TargetName : w.ActorName));
            string claims = string.Join(", ", s.CasusBelli
                .Select(u => WorldData.CurrentState.Factions.TryGetValue(u, out var f) ? f.Name : u));
            string victory = string.IsNullOrEmpty(s.ActiveVictory) ? "" : $"\nVICTORY ACHIEVED: {s.ActiveVictory}";

            string wonders = string.Join(", ", s.Wonders
                .Select(k => OrderSystem.WonderDefs.FirstOrDefault(w => w.Key == k)?.Name ?? k));
            if (!string.IsNullOrEmpty(s.WonderInProgress))
            {
                var wd = OrderSystem.WonderDefs.FirstOrDefault(w => w.Key == s.WonderInProgress);
                wonders += (wonders.Length > 0 ? ", " : "") +
                           $"{wd?.Name ?? s.WonderInProgress} (building, {s.WonderTicksLeft} tick(s) left)";
            }
            string vassals  = string.Join(", ", s.VassalNames.Values);
            string petition = s.PendingPetition != null ? "\n⚖ A petition awaits (GS_PETITION)" : "";
            string advisors = s.Advisors.Count > 0
                ? string.Join(", ", s.Advisors.Select(a => $"{a.Name} ({a.Role.ToLower()})"))
                : "none";

            return $"═ {s.DominionName} ═ (founded turn {s.FoundedTurn})\n" +
                   $"Treasury: {s.Treasury}g | Army: {s.ArmyStrength} | CP: {s.CommandPoints}/{s.MaxCommandPoints} | Pop: {fac.Population} | Tax: {s.TaxPolicy}\n" +
                   $"Holdings ({s.Holdings.Count}):\n{holdings}\n" +
                   $"Great works: {(string.IsNullOrEmpty(wonders) ? "none" : wonders)}\n" +
                   $"Vassals: {(string.IsNullOrEmpty(vassals) ? "none" : vassals)}\n" +
                   $"Council: {advisors}\n" +
                   $"At war with: {(string.IsNullOrEmpty(wars) ? "no one" : wars)}\n" +
                   $"Casus belli: {(string.IsNullOrEmpty(claims) ? "none" : claims)}" +
                   petition + victory;
        }
    }
}
