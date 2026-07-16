using System;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Mythic
{
    /// <summary>
    /// Scene boundaries and (optional) Mythic scene testing. A scene boundary is a
    /// change of top-level place: the outgoing scene settles the CF (ChaosEngine),
    /// then the arrival may be tested — d10 ≤ CF means the scene is modified
    /// (published rule: odd = Altered, even = Interrupted).
    /// </summary>
    public static class SceneTest
    {
        [HarmonyPatch(typeof(GameplayManager), "ApplyLocationChange")]
        public static class Patch_ApplyLocationChange
        {
            [HarmonyPostfix]
            public static void Postfix(GameplayManager __instance, Place newPl)
            {
                try { OnLocationChange(__instance, newPl); }
                catch (Exception ex) { Debug.LogError("[Mythic] Scene handling failed: " + ex); }
            }
        }

        public static void OnLocationChange(GameplayManager manager, Place newPl)
        {
            Place top = newPl?.GetTopLvlPlace();
            if (top == null) return;

            var st = MythicData.State;
            if (top.uuid == st.ScenePlaceUuid) return; // moving within the same scene

            // Scene boundary: settle the outgoing scene, open the new one.
            ChaosEngine.OnSceneEnd();
            ChaosEngine.OnSceneStart(top);

            bool firstVisit = !st.VisitedTopPlaces.Contains(top.uuid);
            if (firstVisit)
            {
                st.VisitedTopPlaces.Add(top.uuid);
                if (st.VisitedTopPlaces.Count > 400)
                    st.VisitedTopPlaces.RemoveRange(0, st.VisitedTopPlaces.Count - 400);
            }

            if (!(MythicPlugin.CfgEnableSceneTest?.Value ?? false)) return;
            if ((MythicPlugin.CfgSceneTestOnlyNewPlaces?.Value ?? true) && !firstVisit) return;

            RunSceneTest(top);
        }

        private static void RunSceneTest(Place top)
        {
            var st = MythicData.State;
            int cf = ChaosEngine.EffectiveCF();
            int d10 = UnityEngine.Random.Range(1, 11);
            if (d10 > cf) return; // Normal scene — plays as expected

            string meaning = OracleTables.RollMeaning();
            bool altered = d10 % 2 == 1;

            MythicEvent ev;
            if (altered)
            {
                ev = new MythicEvent
                {
                    Focus = "SCENE: ALTERED",
                    Meaning = meaning,
                    Directive = $"On arriving at {top.GetPrettyName()}, ONE significant element is different from " +
                                $"what the player would reasonably expect: interpret '{meaning}' as the difference " +
                                "(someone absent or unexpectedly present, circumstances shifted, the mood wrong). " +
                                "Everything else is as anticipated.",
                    QueuedTurn = st.CurrentTurn,
                    ExpiresTurn = st.CurrentTurn + 2
                };
            }
            else
            {
                ev = new MythicEvent
                {
                    Focus = "SCENE: INTERRUPTED",
                    Meaning = meaning,
                    Directive = BuildInterruption(top, meaning),
                    QueuedTurn = st.CurrentTurn,
                    ExpiresTurn = st.CurrentTurn + 2,
                    Significant = true
                };
            }

            MythicData.QueueEvent(ev);
            st.SceneEventCount++;
            MythicData.LogLine($"Scene test at {top.GetPrettyName()}: d10={d10} vs CF {cf} -> {ev.Focus} ('{meaning}')");
        }

        private static string BuildInterruption(Place top, string meaning)
        {
            int d10 = UnityEngine.Random.Range(1, 11);
            string arrival = $"The player's arrival at {top.GetPrettyName()} does not go as intended — ";
            if (d10 <= 3)
                return arrival + $"an immediate, unexpected danger is already here: interpret '{meaning}' as the " +
                       "threat, discovered too late to prepare for. It demands attention now.";
            if (d10 <= 5)
                return arrival + $"someone the player trusted, relied on, or expected to be neutral turns against " +
                       $"them here: interpret '{meaning}' as who and why. Give it the personal weight of a betrayal, " +
                       "even if their reasons are understandable.";
            if (d10 <= 8)
                return arrival + $"the place itself has gone wrong (fire, collapse, storm, unrest, abandonment): " +
                       $"interpret '{meaning}' as the catastrophe already in progress when the player arrives.";
            return arrival + $"whatever the player intended here, there is suddenly far less time than expected: " +
                   $"interpret '{meaning}' as the reason the clock has tightened. Make the pressure felt.";
        }
    }
}
