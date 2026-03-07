using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// When the player changes affinity with NPC A, nearby NPCs who care about NPC A
    /// receive a cascading (ripple) affinity change proportional to their bond strength.
    /// </summary>
    public static class SocialRippleSystem
    {
        public static void Process(string targetUuid, string targetName, int delta, GameplayManager manager)
        {
            if (Math.Abs(delta) < 3) return; // Ignore negligible changes

            List<GameCharacter> nearbyNpcs = null;
            try { nearbyNpcs = manager.GetCharsForNpcConvoSelectorDropdown(); }
            catch { return; }
            if (nearbyNpcs == null) return;

            foreach (var bystander in nearbyNpcs)
            {
                if (bystander == null || bystander.uuid == targetUuid) continue;
                if (bystander.corpseState != GameCharacter.CorpseState.NONE) continue;

                var bData = NPCData.Load(bystander.uuid);
                if (bData == null) continue;

                int bystAffinity = bData.GetAffinity(targetUuid);
                if (Math.Abs(bystAffinity) < 20) continue; // Only react if they care

                // Scale ripple: strong bond → ~33% of delta, weak bond → smaller fraction
                float rippleFactor = (Mathf.Abs(bystAffinity) / 100f) * 0.33f;
                int ripple = (int)(delta * rippleFactor);
                if (ripple == 0) continue;

                string reason = delta > 0
                    ? $"Witnessed player show kindness to {targetName}."
                    : $"Witnessed player harm {targetName}.";

                bData.ChangeAffinity(ripple, reason);
                NPCData.Save(bystander.uuid, bData);

                // Sync with game's sentiment system
                NPCExpansionPlugin.SyncAffinityToGame(bystander.uuid, bData);

                string color = ripple > 0 ? "#aaffaa" : "#ffaaaa";
                string verb = ripple > 0
                    ? $"approves of how you treated {targetName}."
                    : $"disapproves of what you did to {targetName}.";
                _ = manager.gameLogView.LogTextCompat(
                    $"<color={color}>[{bystander.GetPrettyName()}]</color> {verb}");
            }
        }
    }
}
