using System;
using System.Collections.Generic;
using AIROG_GenContext;
using AIROG_Schedules.Logic;

namespace AIROG_Schedules
{
    /// <summary>
    /// GenContext provider: a short block saying what the schedule puts at the player's current
    /// place right now, and where scheduled characters are supposed to be.
    ///
    /// It only informs. Nothing here (or anywhere in this mod) moves, spawns or removes a
    /// character; the storyteller already has npcs_leave / move_entities_here / spawn_entities
    /// for that and uses them when the story calls for it.
    /// </summary>
    public class SchedulesProvider : IContextProvider
    {
        // The name is also the per-save switch key and the row label in the Mods checklist.
        public string Name => SchedulesPlugin.SWITCH_KEY;

        // Just above Chronicle (88), below Settlement (90) / DM Notes (95) / Reverie (96): the
        // block is small and about the scene at hand, so it should not be the first thing lost
        // when Chronicle's timeline is long.
        public int Priority => 89;

        public string Description =>
            "Timetables and routines: what normally happens at the current place at this time of day, and where scheduled characters normally are.";

        public string GetContext(string prompt, int maxTokens)
        {
            try
            {
                if (maxTokens <= 0 || !SchedulesPlugin.IsActive()) return "";
                if (ScheduleStore.Data.entries.Count == 0) return ""; // nothing scheduled: do not even look at the scene
                return BuildBlock(GameProbe.Capture(), prompt, maxTokens).Text;
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarningOnce("provider", "Schedule block failed: " + ex);
                return "";
            }
        }

        /// <summary>Shared by the provider and SCHEDULE_NOW so the command shows exactly what is injected.</summary>
        internal static BlockResult BuildBlock(NowSnapshot snap, string prompt, int maxTokens)
        {
            if (snap == null || snap.Clock == null) return new BlockResult();
            List<ScheduleEntry> entries = ScheduleStore.Data.entries;
            if (entries.Count == 0) return new BlockResult();

            return BlockBuilder.BuildDetailed(new BlockInput
            {
                Clock = snap.Clock,
                PlaceChain = snap.PlaceChain,
                TopLocationName = snap.TopLocationName,
                NearbyPlaces = snap.NearbyPlaces,
                PresentNames = snap.PresentNames,
                PromptText = prompt,
                Entries = entries.ToArray(),
                Options = SchedulesPlugin.BlockOptionsFromConfig(),
                MaxTokens = maxTokens
            });
        }
    }
}
