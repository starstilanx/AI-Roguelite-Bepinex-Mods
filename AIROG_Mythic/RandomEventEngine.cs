using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Mythic
{
    /// <summary>
    /// The director: each turn rolls d100; doubles whose digit ≤ the effective CF fire
    /// a random event (published Mythic 1e rule — chaos gates disruption). The event's
    /// focus is mapped onto real game state (known NPCs, active quests) and queued as a
    /// GenContext directive that the narrative AI interprets on its next generation.
    /// Zero extra AI calls.
    /// </summary>
    public static class RandomEventEngine
    {
        /// <summary>
        /// True while the turn being processed carries one of the game's own random events
        /// (the native "suggested events" drawn into the story prompt on arrivals and
        /// continue-without-input turns, new in the 11/18/2026 build). The director stands down
        /// for that turn: its events are left out of that prompt, it does not roll at the end of
        /// that turn, and an arrival is not scene-tested on top of a native arrival event.
        /// </summary>
        public static bool NativeEventThisTurn;

        // The game assigns the draw the same way (interacterInfo.randomEventDraw = MaybeDraw(...)),
        // at most once per single-player turn, before that turn's story prompt is built.
        [HarmonyPatch(typeof(RandomEvents), nameof(RandomEvents.MaybeDraw))]
        public static class Patch_NativeRandomEvent
        {
            [HarmonyPostfix]
            public static void Postfix(RandomEvents.Draw __result)
            {
                NativeEventThisTurn = __result != null;
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "InvokeTurnHappened")]
        public static class Patch_TurnHappened
        {
            [HarmonyPostfix]
            public static void Postfix(GameplayManager __instance, int numTurns, long secs)
            {
                bool nativeEvent = NativeEventThisTurn;
                // Cleared by the player's own turn tick only: a 0-turn tick nested inside that turn
                // (a death scene, a task result) must not end the stand-down early.
                if (numTurns > 0) NativeEventThisTurn = false;
                try
                {
                    if (!MythicPlugin.SwitchedOn()) return;
                    OnTurn(__instance, numTurns, nativeEvent);
                    // Written every tick, not only when the game saves: Undo/Retry go through
                    // LoadGame, whose postfix re-reads this file. With the default autosave (on
                    // location change) that file could be many turns old, so every retry rewound
                    // the director and dropped any ruling or event queued since.
                    if (numTurns > 0) MythicData.SaveToCurrentDir();
                }
                catch (Exception ex) { Debug.LogError("[Mythic] Turn tick failed: " + ex); }
            }
        }

        public static void OnTurn(GameplayManager manager, int numTurns, bool nativeEventThisTurn = false)
        {
            var st = MythicData.State;
            if (numTurns > 0) st.CurrentTurn += numTurns;

            ChaosEngine.OnTurnSignals(manager);
            // The game reports enemy attacks, encounter rounds, plot hooks and task results as
            // 0-turn ticks. Counting each as a turn let a three-enemy fight burn the event
            // cooldown and a queued event's whole injection window between two player actions.
            if (numTurns <= 0) return;
            MythicData.ExpirePending();

            if (nativeEventThisTurn) return; // one unplanned development per turn
            if (!(MythicPlugin.CfgEnableRandomEvents?.Value ?? true)) return;
            if (st.CurrentTurn - st.LastEventTurn < (MythicPlugin.CfgEventCooldownTurns?.Value ?? 8)) return;
            if (st.SceneEventCount >= (MythicPlugin.CfgMaxEventsPerScene?.Value ?? 2)) return;

            int roll = UnityEngine.Random.Range(1, 101);
            if (OracleTables.IsEventTrigger(roll, ChaosEngine.EffectiveCF()))
                FireEvent(manager, forced: false, triggerRoll: roll);
        }

        /// <summary>Roll and queue a director event. Returns its log summary.</summary>
        public static string FireEvent(GameplayManager manager, bool forced, int triggerRoll = 0)
        {
            var st = MythicData.State;
            int focusRoll = UnityEngine.Random.Range(1, 101);
            string focus = OracleTables.ResolveFocus(focusRoll, out bool significant);
            string meaning = OracleTables.RollMeaning();
            string directive = BuildDirective(manager, focus, significant, meaning);

            MythicData.QueueEvent(new MythicEvent
            {
                Focus = significant ? focus + " (MAJOR)" : focus,
                Directive = directive,
                Meaning = meaning,
                QueuedTurn = st.CurrentTurn,
                ExpiresTurn = st.CurrentTurn + 3,
                Significant = significant
            });
            st.LastEventTurn = st.CurrentTurn;
            st.SceneEventCount++;
            st.TotalEvents++;

            string log = $"Director event{(forced ? " (forced)" : "")}: {focus}" +
                         $"{(significant ? " [major]" : "")} — '{meaning}'" +
                         (triggerRoll > 0 ? $" (trigger {triggerRoll}, CF {ChaosEngine.EffectiveCF()})" : "");
            MythicData.LogLine(log);
            return log;
        }

        // ── Directive construction ──────────────────────────────────────────────

        private static string BuildDirective(GameplayManager manager, string focus, bool significant, string meaning)
        {
            switch (focus)
            {
                case OracleTables.F_REMOTE:
                    return $"Somewhere beyond the player's reach, something has happened: interpret '{meaning}' " +
                           "as a distant event, and deliver it as news, rumor, a messenger, or a far-off sign. " +
                           "The player cannot act on it directly yet.";

                case OracleTables.F_NPC_ACTION:
                {
                    string who = PickNpcName(manager) ?? "a character already known to the player";
                    return $"{who} acts on their own initiative, for their own reasons: interpret '{meaning}' " +
                           "as what they do. Show it as motivated behavior by a real person, not a plot device.";
                }

                case OracleTables.F_NEW_NPC:
                    return "A new character enters the story now, arriving as if they have always belonged to " +
                           $"this world: interpret '{meaning}' as their nature or what brings them here." +
                           (significant ? " They are significant — tie them to one of the player's active goals or troubles." : "");

                case OracleTables.F_THREAD_TOWARD:
                {
                    string quest = MythicBridges.RandomActiveQuest() ?? "the player's current goal or pursuit";
                    return $"Outside forces have moved \"{quest}\" forward without the player's doing: interpret " +
                           $"'{meaning}' as the concrete {(significant ? "MAJOR " : "")}progress the player now discovers.";
                }

                case OracleTables.F_THREAD_AWAY:
                {
                    string quest = MythicBridges.RandomActiveQuest() ?? "the player's current goal or pursuit";
                    return $"Something has set back \"{quest}\": interpret '{meaning}' as the specific, concrete " +
                           "setback the player now encounters or learns of.";
                }

                case OracleTables.F_THREAD_CLOSE:
                {
                    string quest = MythicBridges.RandomActiveQuest() ?? "one of the player's ongoing pursuits";
                    return $"\"{quest}\" reaches a resolution point: interpret '{meaning}' as how it resolves or " +
                           "transforms — for good or ill. Give the moment the weight of an answered question.";
                }

                case OracleTables.F_PC_NEG:
                    return $"Something works directly against the player now: interpret '{meaning}' as a " +
                           "complication, cost, or threat that arrives already in motion.";

                case OracleTables.F_PC_POS:
                    return $"Something breaks the player's way now: interpret '{meaning}' as an advantage, " +
                           "opportunity, or resource that reveals itself — earned by the story, not gifted.";

                case OracleTables.F_AMBIGUOUS:
                    return $"Something specific but unexplained occurs where the player is: interpret '{meaning}' " +
                           "as a vivid, concrete occurrence with no apparent cause. Do NOT explain it, resolve it, " +
                           "or hint at its meaning — leave it hanging for the player to investigate or ignore.";

                case OracleTables.F_NPC_NEG:
                {
                    string who = PickNpcName(manager) ?? "a character known to the player";
                    return $"Something bad happens to {who}: interpret '{meaning}' as what befalls them, " +
                           "shown from their experience of it.";
                }

                case OracleTables.F_NPC_POS:
                {
                    string who = PickNpcName(manager) ?? "a character known to the player";
                    return $"Something good happens to {who}: interpret '{meaning}' as what benefits them, " +
                           "shown from their experience of it.";
                }

                default:
                    return $"An unplanned development occurs: interpret '{meaning}' in the current context.";
            }
        }

        /// <summary>Random living NPC at the player's current top-level place, or null.</summary>
        private static string PickNpcName(GameplayManager manager)
        {
            try
            {
                Place top = manager?.currentPlace?.GetTopLvlPlace();
                if (top == null || SS.I?.uuidToGameEntityMap == null) return null;
                var playable = manager.scenarioState?.allPlayableCharacters;
                var candidates = new List<GameCharacter>();
                foreach (var ge in SS.I.uuidToGameEntityMap.Values)
                {
                    var gc = ge as GameCharacter;
                    if (gc == null) continue;
                    if (gc.characterType != GameCharacter.CharacterType.NPC) continue;
                    if (gc.corpseState != GameCharacter.CorpseState.NONE) continue;
                    // GameCharacter.parentPlace is a legacy field the game no longer assigns
                    // (null on every character made since entities moved to InGameEntity);
                    // the live location is the parent entity's place.
                    if (gc.parentInGameEnt?.ParentPl()?.GetTopLvlPlace()?.uuid != top.uuid) continue;
                    // Player characters are typed NPC too - the director must not script them.
                    if (gc.IsMainPlayer() || (playable != null && playable.Contains(gc))) continue;
                    candidates.Add(gc);
                }
                if (candidates.Count == 0) return null;
                return candidates[UnityEngine.Random.Range(0, candidates.Count)].GetPrettyName();
            }
            catch { return null; }
        }
    }
}
