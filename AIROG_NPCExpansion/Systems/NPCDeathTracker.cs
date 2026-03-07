using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Tracks the deaths of lore-generated NPCs, triggers nearby NPC reactions,
    /// generates AI epitaphs, and persists a memorial to disk.
    /// </summary>
    public static class NPCDeathTracker
    {
        private const string MEMORIAL_FILE = "npcexpansion_memorial.json";

        public static void OnNpcDied(GameCharacter npc, string killerName, int globalTurn, GameplayManager manager, NPCData data = null)
        {
            try
            {
                if (data == null) data = NPCData.Load(npc.uuid);
                if (data == null || string.IsNullOrEmpty(data.Personality)) return; // Only track lore'd NPCs
                if (data.IsDeceased) return; // Already recorded

                data.IsDeceased = true;
                data.DeathInfo = $"Killed by {killerName} on turn {globalTurn}. Last goal: {(string.IsNullOrEmpty(data.CurrentGoal) ? "unknown" : data.CurrentGoal)}";
                NPCData.Save(npc.uuid, data);
                SaveMemorial();

                Debug.Log($"[DeathTracker] {npc.GetPrettyName()} has fallen. {data.DeathInfo}");
                _ = manager.gameLogView.LogTextCompat(
                    GameLogView.AiDecision($"[FALLEN] {npc.GetPrettyName()} has been slain. {data.DeathInfo}"));

                // Nearby NPC reactions
                TriggerBystanterReactions(npc, data, manager);

                // Generate epitaph async
                _ = GenerateEpitaphAsync(npc, data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeathTracker] OnNpcDied failed: {ex.Message}");
            }
        }

        private static void TriggerBystanterReactions(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            List<GameCharacter> nearby = null;
            try { nearby = manager.GetCharsForNpcConvoSelectorDropdown(); }
            catch { return; }
            if (nearby == null) return;

            foreach (var bystander in nearby)
            {
                if (bystander == null || bystander.uuid == npc.uuid) continue;
                if (bystander.corpseState != GameCharacter.CorpseState.NONE) continue;

                var bData = NPCData.Load(bystander.uuid);
                if (bData == null) continue;

                int bond = bData.GetAffinity(npc.uuid);

                if (bond > 20)
                {
                    bData.ChangeAffinity(-15, $"Lost {npc.GetPrettyName()}, a valued friend.");
                    if (bData.LongTermMemories == null) bData.LongTermMemories = new System.Collections.Generic.List<string>();
                    bData.LongTermMemories.Add($"Witnessed the death of {npc.GetPrettyName()}. They will not be forgotten.");
                    while (bData.LongTermMemories.Count > 10) bData.LongTermMemories.RemoveAt(0);
                    NPCData.Save(bystander.uuid, bData);
                    NPCExpansionPlugin.SyncAffinityToGame(bystander.uuid, bData);
                    _ = manager.gameLogView.LogTextCompat(
                        $"<color=#ffaaaa>[{bystander.GetPrettyName()}]</color> mourns the loss of {npc.GetPrettyName()}.");
                }
                else if (bond < -20)
                {
                    _ = manager.gameLogView.LogTextCompat(
                        $"<color=#aaffaa>[{bystander.GetPrettyName()}]</color> seems satisfied by {npc.GetPrettyName()}'s fate.");
                }
            }
        }

        private static async Task GenerateEpitaphAsync(GameCharacter npc, NPCData data)
        {
            try
            {
                string prompt = $"Write a brief epitaph (under 15 words) for {npc.GetPrettyName()}: {data.Personality}. " +
                                $"Their last goal: {data.CurrentGoal}. Poetic, bittersweet. No quotes.";

                string epitaph = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (!string.IsNullOrEmpty(epitaph))
                {
                    data.Epitaph = epitaph.Trim().Trim('"');
                    NPCData.Save(npc.uuid, data);
                    SaveMemorial();
                    Debug.Log($"[DeathTracker] Epitaph for {npc.GetPrettyName()}: {data.Epitaph}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeathTracker] Epitaph generation failed: {ex.Message}");
            }
        }

        public static void SaveMemorial()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;

                var fallen = NPCData.LoreCache
                    .Where(kvp => kvp.Value != null && kvp.Value.IsDeceased)
                    .ToDictionary(kvp => kvp.Key, kvp => new
                    {
                        Name = kvp.Value.Name,
                        DeathInfo = kvp.Value.DeathInfo,
                        Epitaph = kvp.Value.Epitaph,
                        Personality = kvp.Value.Personality,
                        LastGoal = kvp.Value.CurrentGoal,
                        IsNemesis = kvp.Value.IsNemesis
                    });

                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                if (!Directory.Exists(saveDir)) return;
                File.WriteAllText(Path.Combine(saveDir, MEMORIAL_FILE),
                    JsonConvert.SerializeObject(fallen, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeathTracker] SaveMemorial failed: {ex.Message}");
            }
        }
    }
}
