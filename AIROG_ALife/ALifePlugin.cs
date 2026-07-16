using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AIROG_ALife
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency("com.airog.gencontext", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.airog.worldexpansion", BepInDependency.DependencyFlags.SoftDependency)]
    public class ALifePlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.alife";
        public const string PLUGIN_NAME = "AIROG A-Life";
        public const string PLUGIN_VERSION = "2.0.0";

        public static ALifePlugin Instance { get; private set; }

        public static ConfigEntry<bool> CfgEnableSim;
        public static ConfigEntry<int> CfgMaxSquads;
        public static ConfigEntry<bool> CfgMaterialize;
        public static ConfigEntry<bool> CfgNpcMigration;
        public static ConfigEntry<double> CfgMigrationChance;
        // v2.0
        public static ConfigEntry<bool> CfgPersistentSquads;
        public static ConfigEntry<bool> CfgEnableFeuds;
        public static ConfigEntry<bool> CfgEnableLifecycle;

        private void Awake()
        {
            Instance = this;

            CfgEnableSim = Config.Bind("General", "EnableSimulation", true,
                "Master switch for the offline A-Life simulation.");
            CfgMaxSquads = Config.Bind("General", "MaxSquads", 0,
                "Hard cap on live virtual squads. 0 = automatic (scales with world size and wars).");
            CfgMaterialize = Config.Bind("General", "MaterializeSquads", true,
                "Spawn real characters when the player arrives where a virtual squad is present.");
            CfgNpcMigration = Config.Bind("General", "NamedNpcMigration", true,
                "Allow real named NPCs to occasionally travel to neighboring locations off-screen.");
            CfgMigrationChance = Config.Bind("General", "NpcMigrationChancePerTurn", 0.08,
                "Per-turn chance that one off-screen named NPC relocates to an adjacent location.");
            CfgPersistentSquads = Config.Bind("v2", "PersistentSquads", true,
                "Squads survive being met: members stay real, move offline, and remember the player. " +
                "Off = v1.0 behavior (one-way handoff to the game on first meeting).");
            CfgEnableFeuds = Config.Bind("v2", "BloodFeuds", true,
                "Squads that survive battles swear feuds and hunt each other across the map.");
            CfgEnableLifecycle = Config.Bind("v2", "SquadLifecycle", true,
                "Squads gain veterancy, recruit, merge when mauled, split when large, and desert broken factions.");

            Logger.LogInfo($"[ALife] {PLUGIN_GUID} v{PLUGIN_VERSION} loaded.");

            Harmony.CreateAndPatchAll(typeof(ALifeSimulation.Patch_TurnHappened));
            Harmony.CreateAndPatchAll(typeof(ALifeMaterializer.Patch_ApplyLocationChange));
            Harmony.CreateAndPatchAll(typeof(ALifeLegend.Patch_SetAsCorpse));
            Harmony.CreateAndPatchAll(typeof(Patch_SaveLoad));
            Harmony.CreateAndPatchAll(typeof(Patch_ConsoleCommands));

            // Register with GenContext if present (soft dependency — inert without it)
            try
            {
                AIROG_GenContext.ContextManager.RegisterProvider(new ALifeProvider());
                Logger.LogInfo("[ALife] ALifeProvider registered with GenContext.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[ALife] GenContext not available — aftermath prompt injection disabled. ({ex.Message})");
            }
        }

        // ---- Save / load lifecycle ----

        public static class Patch_SaveLoad
        {
            [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
            [HarmonyPostfix]
            public static void Postfix_WriteSaveFile(GameplayManager manager, bool clean)
            {
                ALifeData.SaveToCurrentDir();
            }

            [HarmonyPatch(typeof(GameplayManager), "LoadGame")]
            [HarmonyPostfix]
            public static void Postfix_LoadGame(GameplayManager __instance)
            {
                if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                    ALifeData.Load(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
            }

            [HarmonyPatch(typeof(MainMenu), "NewGame")]
            [HarmonyPostfix]
            public static void Postfix_NewGame(MainMenu __instance)
            {
                ALifeData.Reset();
            }
        }

        // ---- Console commands ----

        [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
        public static class Patch_ConsoleCommands
        {
            [HarmonyPrefix]
            public static bool Prefix(string txt, GameplayManager __instance)
            {
                string cmd = txt.ToUpperInvariant().Trim();

                if (cmd == "ALIFE_STATUS")
                {
                    var st = ALifeData.State;
                    var sb = new StringBuilder();
                    sb.AppendLine($"A-Life turn {st.CurrentTurn} — {st.Squads.Count} squads, {st.RecentEvents.Count} logged events, legend {st.PlayerLegend}");
                    foreach (var s in st.Squads.OrderBy(q => q.CurrentPlaceName))
                        sb.AppendLine($"  [{s.Archetype}] {s.Name} — {s.Size}x lv{s.AvgLevel}, morale {s.Morale}, " +
                                      $"led by {s.Leader?.FullName ?? "?"}, at {s.CurrentPlaceName}, {s.Activity}" +
                                      (s.IsEmbodied ? " [real]" : ""));
                    MessageModal.I.ShowModal(sb.ToString(), false, true);
                    return false;
                }

                if (cmd == "ALIFE_EVENTS")
                {
                    var st = ALifeData.State;
                    var recent = st.RecentEvents.Skip(Math.Max(0, st.RecentEvents.Count - 15)).ToList();
                    string body = recent.Count == 0
                        ? "No events yet."
                        : string.Join("\n", recent.Select(e => $"T{e.Turn} [{e.Type}] {e.PlaceName}: {e.Description}"));
                    MessageModal.I.ShowModal(body, false, true);
                    return false;
                }

                if (cmd == "ALIFE_DOSSIER")
                {
                    var sb = new StringBuilder();
                    foreach (var s in ALifeData.State.Squads.OrderByDescending(q => q.Leader?.Kills ?? 0))
                    {
                        sb.AppendLine($"── {ALifeSimulation.Cap(s.Name)} [{s.Archetype}] — {s.Size}x lv{s.AvgLevel}, XP {s.XP}, morale {s.Morale}" +
                                      (s.IsEmbodied ? $" (embodied: {s.MemberUuids.Count} real)" : ""));
                        if (s.Leader != null)
                            sb.AppendLine($"   Leader: {s.Leader.FullName} ({s.Leader.Role}) — {s.Leader.Kills} kills, {s.Leader.Victories}W/{s.Leader.Defeats}L");
                        sb.AppendLine($"   Regard: fear {s.FearOfPlayer}, awe {s.AweOfPlayer}{(s.MetPlayer ? ", has met the player" : "")}");
                        foreach (var f in s.Feuds)
                            sb.AppendLine($"   Feud: vs {f.EnemySquadName} (heat {f.Heat}) — {f.Reason}");
                        foreach (var line in s.Chronicle.Skip(Math.Max(0, s.Chronicle.Count - 3)))
                            sb.AppendLine($"   • {line}");
                    }
                    if (sb.Length == 0) sb.Append("No squads alive.");
                    MessageModal.I.ShowModal(sb.ToString(), false, true);
                    return false;
                }

                if (cmd == "ALIFE_LEGEND")
                {
                    var st = ALifeData.State;
                    var sb = new StringBuilder();
                    sb.AppendLine($"Player legend: {st.PlayerLegend} ({ALifeLegend.LegendTier() ?? "unknown to the wandering bands"})");
                    if (st.DreadMap.Count > 0)
                    {
                        sb.AppendLine("Dread zones (squads route around these):");
                        foreach (var kv in st.DreadMap.OrderByDescending(k => k.Value).Take(8))
                            sb.AppendLine($"  {(st.DreadNames.TryGetValue(kv.Key, out var n) ? n : kv.Key)}: dread {kv.Value}");
                    }
                    var afraid = st.Squads.Where(s => s.FearOfPlayer >= ALifeLegend.FEAR_WARY)
                        .OrderByDescending(s => s.FearOfPlayer).ToList();
                    if (afraid.Count > 0)
                    {
                        sb.AppendLine("Bands that fear the player:");
                        foreach (var s in afraid.Take(8))
                            sb.AppendLine($"  {s.Name}: fear {s.FearOfPlayer}" + (s.FearOfPlayer >= ALifeLegend.FEAR_FLEE ? " (will flee)" : " (wary)"));
                    }
                    var awed = st.Squads.Where(s => s.AweOfPlayer >= 20).OrderByDescending(s => s.AweOfPlayer).ToList();
                    if (awed.Count > 0)
                    {
                        sb.AppendLine("Bands that respect the player:");
                        foreach (var s in awed.Take(8))
                            sb.AppendLine($"  {s.Name}: awe {s.AweOfPlayer}");
                    }
                    MessageModal.I.ShowModal(sb.ToString(), false, true);
                    return false;
                }

                if (cmd == "ALIFE_TICK")
                {
                    // Force ~3 turns of simulation
                    ALifeSimulation.OnTurn(__instance, 3);
                    MessageModal.I.ShowModal($"Forced A-Life tick. Squads: {ALifeData.State.Squads.Count}", false, true);
                    return false;
                }

                if (cmd == "ALIFE_BATTLE_TEST")
                {
                    var squads = ALifeData.State.Squads;
                    if (squads.Count >= 2)
                    {
                        // Teleport the second squad onto the first and force a battle
                        squads[1].CurrentPlaceUuid = squads[0].CurrentPlaceUuid;
                        squads[1].CurrentPlaceName = squads[0].CurrentPlaceName;
                        ALifeSimulation.ResolveBattle(squads[0], squads[1]);
                        ALifeData.SaveToCurrentDir();
                        MessageModal.I.ShowModal("Battle forced — see ALIFE_EVENTS / ALIFE_DOSSIER.", false, true);
                    }
                    else
                        MessageModal.I.ShowModal("Need at least 2 squads (try ALIFE_TICK a few times).", false, true);
                    return false;
                }

                if (cmd == "ALIFE_SPAWN_HERE")
                {
                    // Debug: park a hostile pack at the player's location and materialize it
                    Place top = __instance.currentPlace?.GetTopLvlPlace();
                    if (top != null)
                    {
                        var squad = new VirtualSquad
                        {
                            Id = "sq_dbg_" + ALifeData.State.NextSquadNum++,
                            Archetype = SquadArchetype.HUNTERS,
                            Size = 3,
                            AvgLevel = Math.Max(1, top.GetAreaLvlAfterScaling()),
                            CurrentPlaceUuid = top.uuid,
                            CurrentPlaceName = top.GetPrettyName(),
                            HomePlaceUuid = top.uuid,
                            SpawnedTurn = ALifeData.State.CurrentTurn,
                            Activity = "prowling for prey"
                        };
                        squad.Name = ALifeNames.SquadName(squad.Archetype, null, top.GetPrettyName());
                        squad.Leader = ALifeNames.MakeLeader(squad);
                        ALifeData.State.Squads.Add(squad);
                        ALifeMaterializer.Materialize(__instance, squad, __instance.currentPlace);
                    }
                    return false;
                }

                return true;
            }
        }
    }
}
