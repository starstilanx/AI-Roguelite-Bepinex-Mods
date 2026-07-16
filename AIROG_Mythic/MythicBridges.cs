using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AIROG_Mythic
{
    /// <summary>
    /// Pure-reflection soft bridge to AIROG_NPCExpansion's quest system (the "Threads"
    /// list for event targeting and quest-outcome control signals). No compile-time
    /// reference — degrades to "no quests" when NPCExpansion is absent.
    /// </summary>
    public static class MythicBridges
    {
        private static bool _questsFailed;
        private static bool _questsResolved;
        private static FieldInfo _fiAllQuests;      // static List<QuestData>
        private static FieldInfo _fiStatus, _fiObjective, _fiGiver;

        private static bool ResolveQuests()
        {
            if (_questsResolved) return !_questsFailed;
            _questsResolved = true;
            try
            {
                Type mgr = Type.GetType("AIROG_NPCExpansion.QuestManager, AIROG_NPCExpansion");
                Type data = Type.GetType("AIROG_NPCExpansion.QuestData, AIROG_NPCExpansion");
                if (mgr == null || data == null) { _questsFailed = true; return false; }
                _fiAllQuests = mgr.GetField("AllQuests", BindingFlags.Public | BindingFlags.Static);
                _fiStatus = data.GetField("Status", BindingFlags.Public | BindingFlags.Instance);
                _fiObjective = data.GetField("ObjectiveText", BindingFlags.Public | BindingFlags.Instance);
                _fiGiver = data.GetField("GiverName", BindingFlags.Public | BindingFlags.Instance);
                _questsFailed = _fiAllQuests == null || _fiStatus == null || _fiObjective == null;
                if (_questsFailed)
                    Debug.LogWarning("[Mythic] NPCExpansion quest bridge: field layout changed — quests disabled.");
                return !_questsFailed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Mythic] NPCExpansion quest bridge unavailable. (" + ex.GetType().Name + ")");
                _questsFailed = true;
                return false;
            }
        }

        /// <summary>Counts by status. Returns false when NPCExpansion is absent.</summary>
        public static bool QuestCounts(out int active, out int completed, out int failed)
        {
            active = completed = failed = 0;
            if (!ResolveQuests()) return false;
            try
            {
                var list = _fiAllQuests.GetValue(null) as IEnumerable;
                if (list == null) return false;
                foreach (object q in list)
                {
                    switch (_fiStatus.GetValue(q)?.ToString())
                    {
                        case "Active": active++; break;
                        case "Completed": completed++; break;
                        case "Failed": failed++; break;
                    }
                }
                return true;
            }
            catch { _questsFailed = true; return false; }
        }

        /// <summary>Random active quest → "objective (from giver)" string, or null.</summary>
        public static string RandomActiveQuest()
        {
            if (!ResolveQuests()) return null;
            try
            {
                var actives = new List<string>();
                var list = _fiAllQuests.GetValue(null) as IEnumerable;
                if (list == null) return null;
                foreach (object q in list)
                {
                    if (_fiStatus.GetValue(q)?.ToString() != "Active") continue;
                    string obj = _fiObjective.GetValue(q) as string;
                    if (string.IsNullOrEmpty(obj)) continue;
                    string giver = _fiGiver?.GetValue(q) as string;
                    actives.Add(string.IsNullOrEmpty(giver) ? obj : $"{obj} (given by {giver})");
                }
                if (actives.Count == 0) return null;
                return actives[UnityEngine.Random.Range(0, actives.Count)];
            }
            catch { _questsFailed = true; return null; }
        }
    }
}
