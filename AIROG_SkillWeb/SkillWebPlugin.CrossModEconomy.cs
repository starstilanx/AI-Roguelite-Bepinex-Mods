using System;
using System.Collections;
using System.Reflection;
using BepInEx.Bootstrap;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Extra Resonance sources sourced from other AIROG mods' progress (Chronicle chapters,
    /// NPCExpansion quests, Settlement buildings, Reverie dreams). Every integration is reached
    /// purely via reflection and gated behind a Chainloader GUID check, so SkillWeb has zero
    /// hard dependency on any of these mods — if one isn't installed, its check just no-ops.
    /// Polled once per turn tick from <see cref="SkillWebPlugin.OnTurnHappened"/> alongside the
    /// existing level/turn-survival/place-visit sources, using the same award-by-milestone-key
    /// idempotency the economy ledger already relies on.
    /// </summary>
    public partial class SkillWebPlugin
    {
        public void CheckCrossModResonance()
        {
            CheckChronicleChapters();
            CheckNPCExpansionQuests();
            CheckSettlementBuildings();
            CheckReverieDreams();
        }

        // ── Chronicle: chapter completed ────────────────────────────────────────

        static class ChronicleHook
        {
            static bool _resolved, _available;
            static PropertyInfo _stateProp, _currentChapterProp, _numberProp;

            public static int? GetCurrentChapterNumber()
            {
                if (!_resolved) Resolve();
                if (!_available) return null;
                try
                {
                    object state = _stateProp.GetValue(null);
                    object chapter = state != null ? _currentChapterProp.GetValue(state) : null;
                    return chapter != null ? (int)_numberProp.GetValue(chapter) : (int?)null;
                }
                catch { return null; }
            }

            static void Resolve()
            {
                _resolved = true;
                try
                {
                    if (!Chainloader.PluginInfos.ContainsKey("com.airog.chronicle")) return;
                    var managerType = Type.GetType("AIROG_Chronicle.ChronicleManager, AIROG_Chronicle");
                    _stateProp = managerType?.GetProperty("State", BindingFlags.Public | BindingFlags.Static);
                    _currentChapterProp = _stateProp?.PropertyType.GetProperty("CurrentChapter");
                    _numberProp = _currentChapterProp?.PropertyType.GetProperty("Number");
                    _available = _stateProp != null && _currentChapterProp != null && _numberProp != null;
                }
                catch { _available = false; }
            }
        }

        void CheckChronicleChapters()
        {
            int? chapterNum = ChronicleHook.GetCurrentChapterNumber();
            if (chapterNum == null || chapterNum < 2) return; // chapter 1 is the starting chapter, not a completion

            for (int c = 2; c <= chapterNum; c++)
                AwardResonance($"chronicle_chapter:{c}", 3, $"completing Chronicle chapter {c - 1}");
        }

        // ── NPCExpansion: quest completed ───────────────────────────────────────

        static class NPCExpansionHook
        {
            static bool _resolved, _available;
            static FieldInfo _allQuestsField;

            public static IEnumerable GetAllQuests()
            {
                if (!_resolved) Resolve();
                if (!_available) return null;
                try { return _allQuestsField.GetValue(null) as IEnumerable; }
                catch { return null; }
            }

            static void Resolve()
            {
                _resolved = true;
                try
                {
                    if (!Chainloader.PluginInfos.ContainsKey("com.airog.npcexpansion")) return;
                    var questManagerType = Type.GetType("AIROG_NPCExpansion.Quests.QuestManager, AIROG_NPCExpansion");
                    _allQuestsField = questManagerType?.GetField("AllQuests", BindingFlags.Public | BindingFlags.Static);
                    _available = _allQuestsField != null;
                }
                catch { _available = false; }
            }
        }

        void CheckNPCExpansionQuests()
        {
            try
            {
                var allQuests = NPCExpansionHook.GetAllQuests();
                if (allQuests == null) return;

                int completedCount = 0;
                foreach (var quest in allQuests)
                {
                    var statusField = quest.GetType().GetField("Status");
                    if (statusField?.GetValue(quest)?.ToString() == "Completed") completedCount++;
                }

                for (int i = 1; i <= completedCount; i++)
                    AwardResonance($"npcexp_quest:{i}", 2, $"completing quest #{i}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[SkillWeb] NPCExpansion quest poll failed: " + ex.Message);
            }
        }

        // ── Settlement: building completed ──────────────────────────────────────

        static class SettlementHook
        {
            static bool _resolved, _available;
            static FieldInfo _instanceField, _settlementField, _buildingsField;

            public static IEnumerable GetBuildings()
            {
                if (!_resolved) Resolve();
                if (!_available) return null;
                try
                {
                    object plugin = _instanceField.GetValue(null);
                    object settlement = plugin != null ? _settlementField.GetValue(plugin) : null;
                    return settlement != null ? _buildingsField.GetValue(settlement) as IEnumerable : null;
                }
                catch { return null; }
            }

            static void Resolve()
            {
                _resolved = true;
                try
                {
                    if (!Chainloader.PluginInfos.ContainsKey("com.airog.settlement")) return;
                    var pluginType = Type.GetType("AIROG_Settlement.SettlementPlugin, AIROG_Settlement");
                    _instanceField = pluginType?.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    _settlementField = pluginType?.GetField("CurrentSettlement", BindingFlags.Public | BindingFlags.Instance);
                    _buildingsField = _settlementField?.FieldType.GetField("Buildings", BindingFlags.Public | BindingFlags.Instance);
                    _available = _instanceField != null && _settlementField != null && _buildingsField != null;
                }
                catch { _available = false; }
            }
        }

        void CheckSettlementBuildings()
        {
            try
            {
                var buildings = SettlementHook.GetBuildings();
                if (buildings == null) return;

                int completeCount = 0;
                foreach (var building in buildings)
                {
                    var isCompleteField = building.GetType().GetField("IsComplete");
                    if (isCompleteField != null && (bool)isCompleteField.GetValue(building)) completeCount++;
                }

                for (int i = 1; i <= completeCount; i++)
                    AwardResonance($"settlement_building:{i}", 2, $"completing settlement building #{i}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[SkillWeb] Settlement building poll failed: " + ex.Message);
            }
        }

        // ── Reverie: dream survived ──────────────────────────────────────────────

        static class ReverieHook
        {
            static bool _resolved, _available;
            static PropertyInfo _stateProp, _totalDreamsProp;

            public static int? GetTotalDreams()
            {
                if (!_resolved) Resolve();
                if (!_available) return null;
                try
                {
                    object state = _stateProp.GetValue(null);
                    return state != null ? (int)_totalDreamsProp.GetValue(state) : (int?)null;
                }
                catch { return null; }
            }

            static void Resolve()
            {
                _resolved = true;
                try
                {
                    if (!Chainloader.PluginInfos.ContainsKey("com.airog.reverie")) return;
                    var managerType = Type.GetType("AIROG_Reverie.ReverieManager, AIROG_Reverie");
                    _stateProp = managerType?.GetProperty("State", BindingFlags.Public | BindingFlags.Static);
                    _totalDreamsProp = _stateProp?.PropertyType.GetProperty("TotalDreams", BindingFlags.Public | BindingFlags.Instance);
                    _available = _stateProp != null && _totalDreamsProp != null;
                }
                catch { _available = false; }
            }
        }

        void CheckReverieDreams()
        {
            int? totalDreams = ReverieHook.GetTotalDreams();
            if (totalDreams == null) return;

            for (int d = 1; d <= totalDreams; d++)
                AwardResonance($"reverie_dream:{d}", 2, $"surviving dream #{d}");
        }
    }
}
