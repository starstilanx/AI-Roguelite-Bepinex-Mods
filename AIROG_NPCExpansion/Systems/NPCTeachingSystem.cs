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
    /// Allows Confidant+ NPCs to teach the player a unique skill based on their expertise.
    /// Taught skills are stored per-save and injected into the AI prompt as known techniques.
    /// Each NPC can only teach once. The NPC loses a little goodwill — sharing is costly.
    /// </summary>
    public static class NPCTeachingSystem
    {
        private const string PLAYER_SKILLS_FILE = "npcexpansion_taught_skills.json";

        public static List<TaughtSkill> PlayerTaughtSkills = new List<TaughtSkill>();

        [Serializable]
        public class TaughtSkill
        {
            public string SkillName;
            public string Description;
            public string TeacherName;
            public int TurnLearned;
        }

        // ─── Teach Flow ────────────────────────────────────────────────────────────

        public static async Task TeachPlayer(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            try
            {
                if (PlayerTaughtSkills.Any(s => s.TeacherName == npc.GetPrettyName()))
                {
                    _ = manager.gameLogView.LogTextCompat(
                        $"<color=#aaaaff>{npc.GetPrettyName()} has already taught you everything they can.</color>");
                    return;
                }

                // Build expertise hints from abilities → tags → personality (first non-empty wins)
                string expertiseHints;
                if (data.DetailedAbilities.Count > 0)
                    expertiseHints = string.Join(", ", data.DetailedAbilities.Select(a => a.Name));
                else if (data.Tags != null && data.Tags.Count > 0)
                    expertiseHints = string.Join(", ", data.Tags);
                else
                    expertiseHints = NPCData.GetPersonality(npc, data);

                string prompt = $"NPC Teacher: {npc.GetPrettyName()}\n" +
                                $"Personality: {NPCData.GetPersonality(npc, data)}\n" +
                                $"Expertise: {expertiseHints}\n\n" +
                                $"This NPC is teaching the player one unique skill or technique drawn from their expertise.\n" +
                                $"Respond ONLY in this exact format:\n" +
                                $"SKILL: [skill name, 2-5 words]\n" +
                                $"DESCRIPTION: [what it does in the game world, under 30 words]";

                string result = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(result)) return;

                string skillName = "";
                string skillDesc = "";
                foreach (var line in result.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("SKILL:")) skillName = t.Substring(6).Trim();
                    else if (t.StartsWith("DESCRIPTION:")) skillDesc = t.Substring(12).Trim();
                }

                if (string.IsNullOrEmpty(skillName)) return;

                PlayerTaughtSkills.Add(new TaughtSkill
                {
                    SkillName = skillName,
                    Description = skillDesc,
                    TeacherName = npc.GetPrettyName(),
                    TurnLearned = ScenarioUpdater.GlobalTurn
                });

                SavePlayerSkills();

                // NPC goodwill cost — they've shared something precious
                data.ChangeAffinity(-8, "Shared deep knowledge with the player.");
                NPCData.Save(npc.uuid, data);
                NPCExpansionPlugin.SyncAffinityToGame(npc.uuid, data);

                // RecordMilestone flushes the lore bundle, so the affinity cost above lands
                // on disk in the same pass and GenContext sees both on the next prompt.
                RelationshipArcSystem.RecordMilestone(npc.uuid, data, $"Taught player: {skillName}");

                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#90e0ff>[SKILL LEARNED] {npc.GetPrettyName()} teaches you: {skillName}</color>");
                if (!string.IsNullOrEmpty(skillDesc))
                    _ = manager.gameLogView.LogTextCompat($"<color=#90e0ff>{skillDesc}</color>");

                Debug.Log($"[TeachingSystem] Player learned '{skillName}' from {npc.GetPrettyName()}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TeachingSystem] TeachPlayer failed: {ex.Message}");
            }
        }

        // Taught techniques reach the AI through AIROG_GenContext's NPCProvider, which reads
        // npcexpansion_taught_skills.json. Until v4.4.0 this mod appended them to the prompt
        // itself via a postfix on PlayableCharacterData.GetPlayerStatusStrToAppendNoSpace,
        // which sidestepped the shared token budget and the provider toggle.

        // ─── Persistence ───────────────────────────────────────────────────────────

        public static void SavePlayerSkills()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
                string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, PLAYER_SKILLS_FILE);
                File.WriteAllText(path, JsonConvert.SerializeObject(PlayerTaughtSkills, Formatting.Indented));
            }
            catch (Exception ex) { Debug.LogWarning($"[TeachingSystem] Save failed: {ex.Message}"); }
        }

        public static void LoadPlayerSkills(string saveDir)
        {
            PlayerTaughtSkills.Clear();
            try
            {
                string path = Path.Combine(saveDir, PLAYER_SKILLS_FILE);
                if (!File.Exists(path)) return;
                var loaded = JsonConvert.DeserializeObject<List<TaughtSkill>>(File.ReadAllText(path));
                if (loaded != null) PlayerTaughtSkills.AddRange(loaded);
                Debug.Log($"[TeachingSystem] Loaded {PlayerTaughtSkills.Count} taught skill(s).");
            }
            catch (Exception ex) { Debug.LogWarning($"[TeachingSystem] Load failed: {ex.Message}"); }
        }
    }
}
