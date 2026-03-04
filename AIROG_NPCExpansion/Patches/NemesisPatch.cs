using HarmonyLib;
using UnityEngine;
using System.Threading.Tasks;
using AIROG_NPCExpansion;

namespace AIROG_NPCExpansion.Patches
{
    [HarmonyPatch(typeof(GameplayManager), "ProcessInteractionInfoNoTryStr")]
    public static class NemesisPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameplayManager __instance, InteractionInfo interactionInfo, Task<GameEventResult> __result)
        {
            // Check Config (Loose Coupling with GenContext)
            if (!IsNemesisSystemEnabled()) return;

            // Check if player is dead
            if (__instance.playerCharacter == null || __instance.playerCharacter.pcGameEntity.GetHealth() > 0) return;

            // Check if this interaction was an enemy attack
            if (interactionInfo == null || interactionInfo.interacterInfo == null) return;

            if (interactionInfo.interacterInfo.interacterType == InteracterInfo.InteracterType.ENEMY_ATTACKS_PLAYER)
            {
                var killer = interactionInfo.interacterInfo.enemyAttacker;
                if (killer != null)
                {
                    Debug.Log($"[NemesisPatch] Player killed by {killer.GetPrettyName()}!");
                    NemesisManager.PromoteKiller(killer);
                }
            }
        }

        private static bool IsNemesisSystemEnabled()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "gen_context_config.json");
                if (System.IO.File.Exists(path))
                {
                    string json = System.IO.File.ReadAllText(path);
                    if (json.Contains("\"NemesisSystem\": false")) return false; 
                    // Simple string check is faster/safer than full Json deserialize if we don't want deps
                    // But Newtonsoft is available.
                    // Let's assume default is TRUE if key missing or file missing.
                    return true;
                }
            }
            catch { }
            return true; // Default ON
        }
    }
}
