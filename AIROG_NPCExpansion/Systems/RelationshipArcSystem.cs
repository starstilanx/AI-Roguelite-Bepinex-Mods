using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Tracks the narrative arc of the player's relationship with each NPC.
    /// Arc stages are derived from Affinity and unlock special interactions.
    /// Milestone events are recorded in NPCData.ArcMilestones for AI context.
    /// </summary>
    public static class RelationshipArcSystem
    {
        // ─── Arc Stage ─────────────────────────────────────────────────────────────

        public static string GetArcStage(NPCData data) => GetArcStageForValue(data.Affinity);

        private static string GetArcStageForValue(int affinity)
        {
            if (affinity >= 90) return "Sworn Companion";
            if (affinity >= 75) return "Confidant";
            if (affinity >= 60) return "Ally";
            if (affinity >= 40) return "Friend";
            if (affinity >= 20) return "Acquaintance";
            if (affinity > -20) return "Stranger";
            if (affinity > -40) return "Disliked";
            if (affinity > -60) return "Enemy";
            return "Nemesis";
        }

        // ─── Milestone Recording ───────────────────────────────────────────────────

        public static void RecordMilestone(string npcUuid, NPCData data, string milestone)
        {
            if (data.ArcMilestones == null) data.ArcMilestones = new System.Collections.Generic.List<string>();
            string entry = $"[T{ScenarioUpdater.GlobalTurn}] {milestone}";
            if (data.ArcMilestones.Contains(entry)) return;

            data.ArcMilestones.Insert(0, entry);
            while (data.ArcMilestones.Count > 10) data.ArcMilestones.RemoveAt(data.ArcMilestones.Count - 1);
            NPCData.Save(npcUuid, data);
            // Milestones are narrative beats the AI should know about immediately, not at
            // the next autosave — GenContext reads them from the lore bundle on disk.
            NPCData.FlushSessionLore();
            Debug.Log($"[ArcSystem] Milestone recorded for {data.Name}: {milestone}");
        }

        // ─── Arc Advancement Notification ─────────────────────────────────────────

        public static void CheckArcAdvancement(GameCharacter npc, NPCData data, GameplayManager manager, int oldAffinity)
        {
            string oldStage = GetArcStageForValue(oldAffinity);
            string newStage = GetArcStage(data);
            if (oldStage == newStage) return;

            bool positive = data.Affinity > oldAffinity;
            string color = positive ? "#90ff90" : "#ff9090";
            _ = manager.gameLogView.LogTextCompat(
                $"<color={color}>[RELATIONSHIP] {npc.GetPrettyName()}: {oldStage} → {newStage}</color>");

            RecordMilestone(npc.uuid, data, $"Relationship became {newStage}");
        }

        // ─── Arc-Unlocked Actions ──────────────────────────────────────────────────

        public static List<StrToAction> GetAvailableArcActions(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            var actions = new List<StrToAction>();
            if (data == null || !NPCData.HasProfile(npc, data)) return actions;

            // Ally (60+): Ask Secret
            if (data.Affinity >= 60)
            {
                bool allRevealed = data.Secrets != null &&
                                   data.Secrets.Count > 0 &&
                                   data.Secrets.TrueForAll(s => s.IsRevealed);
                if (!allRevealed)
                {
                    actions.Add(new StrToAction("<color=#c890ff>Ask Secret</color>", async () =>
                    {
                        await NPCSecretSystem.TryRevealSecret(npc, data, manager);
                    }));
                }
            }

            // Confidant (75+): Teach Me
            if (data.Affinity >= 75)
            {
                actions.Add(new StrToAction("<color=#90e0ff>Teach Me</color>", async () =>
                {
                    await NPCTeachingSystem.TeachPlayer(npc, data, manager);
                }));
            }

            return actions;
        }

        // Arc milestones reach the AI through AIROG_GenContext's NPCProvider, which reads
        // NPCData.ArcMilestones from npcexpansion_lore.json and injects the three most recent.
    }
}
