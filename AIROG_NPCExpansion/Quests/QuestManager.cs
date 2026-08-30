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
    /// Manages the full lifecycle of NPC quests: generation, tracking, completion, and failure.
    /// </summary>
    public static class QuestManager
    {
        private const string QUESTS_FILE = "npcexpansion_quests.json";

        public static List<QuestData> AllQuests = new List<QuestData>();

        public static bool HasActiveQuests => AllQuests.Any(q => q.Status == QuestStatus.Active);

        // ─── Quest Generation ──────────────────────────────────────────────────────

        public static async Task<QuestData> GenerateQuest(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            try
            {
                string context = manager.GetContextForQuickActions();
                if (context.Length > 1500) context = context.Substring(context.Length - 1500);

                string prompt = $"NPC '{npc.GetPrettyName()}' is giving the player a quest.\n" +
                                $"NPC personality: {NPCData.GetPersonality(npc, data)}\n" +
                                $"Current goal: {data.CurrentGoal}\n" +
                                $"World context: {context}\n\n" +
                                $"Generate a quest. Respond ONLY in this exact format (no other text):\n" +
                                $"OBJECTIVE: [one-sentence quest description, under 30 words]\n" +
                                $"CONDITION: [what must happen or be done to complete this quest, under 20 words]\n" +
                                $"REWARD: [narrative reward description, under 15 words]\n" +
                                $"GOLD: [integer between 0 and 500]";

                string result = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(result)) return null;

                var quest = new QuestData
                {
                    Id = Guid.NewGuid().ToString(),
                    GiverId = npc.uuid,
                    GiverName = npc.GetPrettyName(),
                    TurnGiven = ScenarioUpdater.GlobalTurn,
                };

                foreach (var line in result.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("OBJECTIVE:"))
                        quest.ObjectiveText = trimmed.Substring(10).Trim();
                    else if (trimmed.StartsWith("CONDITION:"))
                        quest.CompletionCondition = trimmed.Substring(10).Trim();
                    else if (trimmed.StartsWith("REWARD:"))
                        quest.RewardText = trimmed.Substring(7).Trim();
                    else if (trimmed.StartsWith("GOLD:") &&
                             int.TryParse(trimmed.Substring(5).Trim(), out int gold))
                        quest.RewardGold = Mathf.Clamp(gold, 0, 500);
                }

                if (string.IsNullOrEmpty(quest.ObjectiveText)) return null;

                AllQuests.Add(quest);
                SaveQuests();

                // Write quest into the NPC's long-term memory so they know about it
                // and so NPCProvider can inject it even before the next WriteSaveFile call.
                var npcLoreData = NPCData.Load(npc.uuid);
                if (npcLoreData != null)
                {
                    if (npcLoreData.LongTermMemories == null)
                        npcLoreData.LongTermMemories = new List<string>();
                    npcLoreData.LongTermMemories.Add(
                        $"I gave the player a quest: \"{quest.ObjectiveText}\" (they must: {quest.CompletionCondition}).");
                    while (npcLoreData.LongTermMemories.Count > 10) npcLoreData.LongTermMemories.RemoveAt(0);
                    NPCData.Save(npc.uuid, npcLoreData);
                    // Flush session lore so NPCProvider's 5-second cache picks it up
                    NPCData.FlushSessionLore();
                }

                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#ffd700>[QUEST ACCEPTED] {npc.GetPrettyName()}: {quest.ObjectiveText}</color>");

                if (!string.IsNullOrEmpty(quest.RewardText))
                    _ = manager.gameLogView.LogTextCompat(
                        $"<color=#aaaaff>Reward: {quest.RewardText}" +
                        (quest.RewardGold > 0 ? $" (+{quest.RewardGold} gold)" : "") + "</color>");

                Debug.Log($"[QuestManager] Generated quest from {npc.GetPrettyName()}: {quest.ObjectiveText}");
                return quest;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestManager] GenerateQuest failed: {ex.Message}");
                return null;
            }
        }

        // ─── Completion Check (called after each story turn) ───────────────────────

        public static async Task CheckCompletion(string storyText, GameplayManager manager)
        {
            var active = AllQuests.Where(q => q.Status == QuestStatus.Active).ToList();
            if (active.Count == 0) return;

            // Trim story text to avoid token bloat
            if (storyText.Length > 500) storyText = storyText.Substring(0, 500);

            try
            {
                // One batched AI call for all active quests instead of one call per quest.
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Story event that just happened: \"{storyText}\"");
                sb.AppendLine();
                sb.AppendLine("Quest completion conditions:");
                for (int i = 0; i < active.Count; i++)
                    sb.AppendLine($"{i + 1}. \"{active[i].CompletionCondition}\"");
                sb.AppendLine();
                sb.AppendLine("For EACH numbered condition, did the story event fulfill it?");
                sb.AppendLine("Respond with one line per condition, exactly in the form:");
                sb.AppendLine("1: YES or 1: NO");

                string answer = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    sb.ToString(),
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(answer)) return;

                foreach (var line in answer.Split('\n'))
                {
                    var parts = line.Split(new[] { ':' }, 2);
                    if (parts.Length < 2) continue;
                    if (!int.TryParse(parts[0].Trim().TrimStart('#'), out int idx)) continue;
                    if (idx < 1 || idx > active.Count) continue;
                    if (!parts[1].Trim().ToUpperInvariant().StartsWith("YES")) continue;

                    var quest = active[idx - 1];
                    if (quest.Status == QuestStatus.Active) // Guard against duplicate lines
                        await CompleteQuest(quest, manager);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestManager] Completion check error: {ex.Message}");
            }
        }

        // ─── Quest Completion ──────────────────────────────────────────────────────

        private static async Task CompleteQuest(QuestData quest, GameplayManager manager)
        {
            quest.Status = QuestStatus.Completed;

            // Gold reward
            if (quest.RewardGold > 0 && manager.playerCharacter?.pcGameEntity != null)
                manager.playerCharacter.pcGameEntity.numGold += quest.RewardGold;

            // Affinity reward + memory
            if (!string.IsNullOrEmpty(quest.GiverId))
            {
                GameEntity ent = null;
                SS.I?.uuidToGameEntityMap?.TryGetValue(quest.GiverId, out ent);
                if (ent is GameCharacter giver)
                {
                    var data = NPCData.Load(giver.uuid);
                    if (data != null)
                    {
                        data.ChangeAffinity(quest.RewardAffinity, $"Player completed quest: {quest.ObjectiveText}");
                        if (data.LongTermMemories == null) data.LongTermMemories = new System.Collections.Generic.List<string>();
                        data.LongTermMemories.Add($"The player fulfilled my quest: {quest.ObjectiveText}");
                        while (data.LongTermMemories.Count > 10) data.LongTermMemories.RemoveAt(0);
                        NPCData.Save(giver.uuid, data);
                        NPCExpansionPlugin.SyncAffinityToGame(giver.uuid, data);
                    }
                }
            }

            SaveQuests();

            string goldPart = quest.RewardGold > 0 ? $" (+{quest.RewardGold} gold)" : "";
            _ = manager.gameLogView.LogTextCompat(
                $"<color=#ffd700>[QUEST COMPLETE] {quest.GiverName}: {quest.ObjectiveText}{goldPart}</color>");

            // Chain finale: record an arc milestone for completing the full chain
            if (quest.IsChainFinal && !string.IsNullOrEmpty(quest.GiverId))
            {
                var chainData = NPCData.Load(quest.GiverId);
                if (chainData != null)
                    RelationshipArcSystem.RecordMilestone(quest.GiverId, chainData,
                        $"Completed full quest chain: {quest.ObjectiveText}");
            }

            // Try to spawn a follow-up chain quest (40% chance, max 3 steps)
            _ = QuestChainManager.TryOfferChainQuest(quest, manager);

            Debug.Log($"[QuestManager] Quest completed: {quest.ObjectiveText}");
            await Task.CompletedTask;
        }

        // ─── Deadline & Failure ────────────────────────────────────────────────────

        public static void CheckDeadlines(int globalTurn, GameplayManager manager)
        {
            bool anyChanged = false;

            foreach (var quest in AllQuests.Where(q =>
                q.Status == QuestStatus.Active &&
                q.TurnDeadline >= 0 &&
                globalTurn > q.TurnDeadline))
            {
                quest.Status = QuestStatus.Failed;
                anyChanged = true;
                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#ff6666>[QUEST FAILED] {quest.GiverName}: {quest.ObjectiveText}</color>");
                Debug.Log($"[QuestManager] Quest expired: {quest.ObjectiveText}");
            }

            // Also fail quests whose giver NPC is dead
            foreach (var quest in AllQuests.Where(q => q.Status == QuestStatus.Active))
            {
                var data = NPCData.Load(quest.GiverId);
                if (data != null && data.IsDeceased)
                {
                    quest.Status = QuestStatus.Failed;
                    anyChanged = true;
                    _ = manager.gameLogView.LogTextCompat(
                        $"<color=#ff6666>[QUEST FAILED] {quest.GiverName} has died: {quest.ObjectiveText}</color>");
                }
            }

            if (anyChanged) SaveQuests();
        }

        // ─── Persistence ───────────────────────────────────────────────────────────

        public static void SaveQuests()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                if (!Directory.Exists(saveDir)) return;
                File.WriteAllText(Path.Combine(saveDir, QUESTS_FILE),
                    JsonConvert.SerializeObject(AllQuests, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestManager] SaveQuests failed: {ex.Message}");
            }
        }

        public static void LoadQuests(string saveDir)
        {
            AllQuests.Clear();
            try
            {
                string path = Path.Combine(saveDir, QUESTS_FILE);
                if (!File.Exists(path)) return;
                var loaded = JsonConvert.DeserializeObject<List<QuestData>>(File.ReadAllText(path));
                if (loaded != null) AllQuests.AddRange(loaded);
                Debug.Log($"[QuestManager] Loaded {AllQuests.Count} quests.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestManager] LoadQuests failed: {ex.Message}");
            }
        }
    }
}
