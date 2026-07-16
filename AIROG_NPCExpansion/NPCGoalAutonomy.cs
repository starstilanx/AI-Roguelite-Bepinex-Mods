using System.Collections.Generic;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    internal static class NPCGoalAutonomy
    {
        public static void PursueGoal(GameCharacter npc, NPCData data, GameplayManager manager)
        {
             // 10% chance to act on goal per turn, OR immediately if we have no thoughts yet (for UI)
             bool forceThink = (data.RecentThoughts == null || data.RecentThoughts.Count == 0);
             if (forceThink || UnityEngine.Random.value < 0.1f)
             {
                 string progress = string.IsNullOrEmpty(data.GoalProgress) ? "starting" : data.GoalProgress;
                 string thought = $"Thinking about goal: {data.CurrentGoal} ({progress}).";
                 Debug.Log($"[NPCAutonomy] {npc.GetPrettyName()}: {thought}");

                 if (data.RecentThoughts == null) data.RecentThoughts = new List<string>();

                 // Fix duplicate spam: Don't add if it's identical to the last one
                 if (data.RecentThoughts.Count == 0 || data.RecentThoughts[0] != thought)
                 {
                     data.RecentThoughts.Insert(0, thought);
                     if (data.RecentThoughts.Count > 5) data.RecentThoughts.RemoveAt(data.RecentThoughts.Count - 1);
                 }

                 NPCData.Save(npc.uuid, data);
             }
        }

        public static void PerformAbility(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            // 5% chance to perform an ability if one exists
            if (UnityEngine.Random.value > 0.05f) return;

            // Use DetailedAbilities directly to avoid the allocation overhead of the Abilities getter shim
            if (data.DetailedAbilities.Count == 0) return;

            var abil = data.DetailedAbilities[UnityEngine.Random.Range(0, data.DetailedAbilities.Count)];

            string logMsg = $"{npc.GetPrettyName()} uses {abil.Name}! ({abil.Description})";
            _ = manager.gameLogView.LogTextCompat(GameLogView.AiDecision(logMsg));
            Debug.Log($"[NPCAutonomy] {logMsg}");
        }
    }
}
