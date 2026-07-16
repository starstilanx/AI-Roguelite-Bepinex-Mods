using System;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Mythic
{
    /// <summary>
    /// The Chaos Factor engine. A "scene" is one stay at a top-level place; while it
    /// runs, real gameplay outcomes accumulate a signed control score (kills, brushes
    /// with death, quest outcomes). At the scene boundary the CF moves by exactly ±1 —
    /// success and control push it down, failure and loss push it up.
    /// </summary>
    public static class ChaosEngine
    {
        private const int KILL_CREDIT_CAP = 3;   // per scene — no CF farming off trash mobs
        private static bool _questWatermarkSynced; // in-memory: first poll after load only syncs

        // ── Effective CF (chaos variation) ─────────────────────────────────────

        /// <summary>CF as the oracle sees it: Standard = raw, Low = clamped 4–6, None = fixed 5.</summary>
        public static int EffectiveCF()
        {
            int cf = Mathf.Clamp(MythicData.State.ChaosFactor, 1, 9);
            switch ((MythicPlugin.CfgChaosVariation?.Value ?? "Standard").Trim().ToUpperInvariant())
            {
                case "LOW": return Mathf.Clamp(cf, 4, 6);
                case "NONE": return 5;
                default: return cf;
            }
        }

        public static string RegisterTier(int cf)
        {
            if (cf <= 3) return "LOW";
            if (cf <= 6) return "MODERATE";
            return "HIGH";
        }

        /// <summary>The always-on narrative register directive, distilled from the Mythic voice guide.</summary>
        public static string RegisterDirective()
        {
            int cf = EffectiveCF();
            switch (RegisterTier(cf))
            {
                case "LOW":
                    return "[WORLD VOLATILITY: LOW] The world currently has rhythm and order: cause leads to " +
                           "effect, people behave according to their legible motives, and danger announces itself " +
                           "with fair warning. Keep the prose measured and grounded; let the player's plans get " +
                           "traction. Never mention volatility, chaos levels, dice, or oracles.";
                case "HIGH":
                    return "[WORLD VOLATILITY: HIGH] The world is currently unstable: events outpace their causes, " +
                           "people act before they are prompted, problems compound, and timing feels wrong. Keep the " +
                           "prose sharp and urgent; let complications arrive already in motion. Do not soften this " +
                           "into safety. Never mention volatility, chaos levels, dice, or oracles.";
                default:
                    return "[WORLD VOLATILITY: MODERATE] The world is currently active but navigable: most things go " +
                           "as expected, yet other forces have their own agendas and not every action lands as " +
                           "planned. Mix the expected with the occasional unplanned wrinkle. Never mention " +
                           "volatility, chaos levels, dice, or oracles.";
            }
        }

        // ── Scene lifecycle (called from SceneTest's ApplyLocationChange patch) ──

        public static void OnSceneEnd()
        {
            var st = MythicData.State;
            if (string.IsNullOrEmpty(st.ScenePlaceUuid)) return; // no scene yet (fresh game)

            int score = st.SceneControlScore + Math.Min(KILL_CREDIT_CAP, st.SceneKillCredits);
            int oldCf = st.ChaosFactor;
            if (score > 0) st.ChaosFactor = Math.Max(1, st.ChaosFactor - 1);
            else if (score < 0) st.ChaosFactor = Math.Min(9, st.ChaosFactor + 1);

            if (st.ChaosFactor != oldCf || score != 0)
                MythicData.LogLine($"Scene {st.SceneNumber} at {st.ScenePlaceName} ended " +
                                   $"(control {(score >= 0 ? "+" : "")}{score}): CF {oldCf} -> {st.ChaosFactor}");
        }

        public static void OnSceneStart(Place top)
        {
            var st = MythicData.State;
            st.SceneNumber++;
            st.ScenePlaceUuid = top?.uuid;
            st.ScenePlaceName = top?.GetPrettyName() ?? "?";
            st.SceneControlScore = 0;
            st.SceneKillCredits = 0;
            st.SceneNearDeathFlagged = false;
            st.SceneEventCount = 0;
        }

        // ── Per-turn signals (called from RandomEventEngine's turn patch) ───────

        public static void OnTurnSignals(GameplayManager manager)
        {
            NearDeathCheck(manager);
            QuestOutcomeCheck();
        }

        private static void NearDeathCheck(GameplayManager manager)
        {
            try
            {
                var st = MythicData.State;
                if (st.SceneNearDeathFlagged) return;
                var p = manager?.playerCharacter?.pcGameEntV2;
                if (p == null) return;
                long max = p.GetMaxHealth();
                if (max <= 0) return;
                if ((double)p.GetHealth() / max < 0.25)
                {
                    st.SceneNearDeathFlagged = true;
                    st.SceneControlScore -= 1;
                    MythicData.LogLine("Player near death — control -1.");
                }
            }
            catch { /* HP surface changed — skip signal */ }
        }

        private static void QuestOutcomeCheck()
        {
            var st = MythicData.State;
            if (!MythicBridges.QuestCounts(out _, out int completed, out int failed)) return;
            if (!_questWatermarkSynced)
            {
                // First poll after plugin/save load: sync watermarks without scoring history.
                st.SeenQuestsCompleted = completed;
                st.SeenQuestsFailed = failed;
                _questWatermarkSynced = true;
                return;
            }
            if (completed > st.SeenQuestsCompleted)
            {
                st.SceneControlScore += 2 * (completed - st.SeenQuestsCompleted);
                MythicData.LogLine($"Quest completed — control +{2 * (completed - st.SeenQuestsCompleted)}.");
            }
            if (failed > st.SeenQuestsFailed)
            {
                st.SceneControlScore -= 2 * (failed - st.SeenQuestsFailed);
                MythicData.LogLine($"Quest failed — control -{2 * (failed - st.SeenQuestsFailed)}.");
            }
            st.SeenQuestsCompleted = completed;
            st.SeenQuestsFailed = failed;
        }

        /// <summary>Reset in-memory watermark sync on save load (so history isn't re-scored).</summary>
        public static void OnAfterLoad() => _questWatermarkSynced = false;

        // ── Kill / death tracking ───────────────────────────────────────────────

        [HarmonyPatch(typeof(GameCharacter), "SetAsCorpse")]
        public static class Patch_SetAsCorpse
        {
            [HarmonyPostfix]
            public static void Postfix(GameCharacter __instance)
            {
                try { OnDeath(__instance); }
                catch (Exception ex) { Debug.LogWarning("[Mythic] Death tracking failed: " + ex.Message); }
            }
        }

        private static void OnDeath(GameCharacter ch)
        {
            if (ch == null) return;
            GameplayManager manager = SS.I?.hackyManager;
            string playerTop = manager?.currentPlace?.GetTopLvlPlace()?.uuid;
            if (playerTop == null) return;

            // Only witnessed deaths count: same top-level place as the player.
            string chTop = ch.parentPlace?.GetTopLvlPlace()?.uuid;
            if (chTop != playerTop) return;

            var st = MythicData.State;
            switch (ch.characterType)
            {
                case GameCharacter.CharacterType.NORMAL_MOB:
                case GameCharacter.CharacterType.ELITE_MOB:
                case GameCharacter.CharacterType.BOSS:
                    st.SceneKillCredits += 1;   // folded into control score at scene end, capped
                    break;
                case GameCharacter.CharacterType.NPC:
                    st.SceneControlScore -= 1;  // a person died around the player — the world darkens
                    MythicData.LogLine($"{SafeName(ch)} died in the player's presence — control -1.");
                    break;
            }
        }

        private static string SafeName(GameCharacter ch)
        {
            try { return ch.GetPrettyName(); } catch { return "An NPC"; }
        }
    }
}
