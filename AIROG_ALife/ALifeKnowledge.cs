using System;
using System.Collections.Generic;
using System.Linq;

namespace AIROG_ALife
{
    /// <summary>
    /// v2.2 fog of war: the simulation knows everything; the PLAYER only knows what
    /// they've seen or heard. Intel comes from three channels — meeting a band face
    /// to face (full dossier), rumor of bands within one hop (name + rough position),
    /// and discovering event sites by visiting them. Records go stale the moment a
    /// band moves out of earshot: the Rumors tab and Tracks lens show last-known
    /// intel with its age, never live state.
    /// </summary>
    public static class ALifeKnowledge
    {
        public const int RUMOR_STALE_TURNS = 15;   // markers/entries older than this render faded
        public const int FORGET_TURNS = 150;       // intel this old is dropped entirely

        /// <summary>
        /// How many co-located squads ALifeProvider narrates as directly present ("In this
        /// area: ..."). Shared with Tick below so the Rumors tab's fog-of-war state can't
        /// disagree with what the AI was just told: a squad the provider names outright
        /// (leader, activity) must count as met, not merely rumored.
        /// </summary>
        public const int VISIBLE_HERE_CAP = 2;

        /// <summary>Turn upkeep: absorb rumors of nearby bands, hear nearby events, forget ancient intel.</summary>
        public static void Tick(string playerTopUuid)
        {
            if (playerTopUuid == null) return;
            var state = ALifeData.State;
            var neighborUuids = new HashSet<string>(ALifeGraph.Neighbors(playerTopUuid).Select(p => p.uuid));

            int hereIdx = 0;
            foreach (var s in state.Squads)
            {
                if (s.CurrentPlaceUuid == playerTopUuid)
                {
                    // Only the first VISIBLE_HERE_CAP squads sharing this ground are the
                    // ones the provider actually names to the AI as present; the rest are
                    // just sharing the region — solid rumor, same as before contact.
                    Learn(s, met: hereIdx < VISIBLE_HERE_CAP);
                    hereIdx++;
                }
                else if (neighborUuids.Contains(s.CurrentPlaceUuid))
                {
                    Learn(s, met: false);
                }
            }

            // Word of nearby happenings reaches the player's ears.
            foreach (var e in state.RecentEvents)
            {
                if (e.Known || e.Turn < state.CurrentTurn - 2) continue;
                if (e.PlaceUuid == playerTopUuid || neighborUuids.Contains(e.PlaceUuid))
                    e.Known = true;
            }

            // Old intel fades from memory.
            foreach (var key in state.Knowledge.Keys.ToList())
                if (state.Knowledge[key].LastKnownTurn < state.CurrentTurn - FORGET_TURNS)
                    state.Knowledge.Remove(key);
        }

        /// <summary>Record/refresh intel on a band. Met = face-to-face (unlocks the dossier).</summary>
        public static void Learn(VirtualSquad s, bool met)
        {
            var state = ALifeData.State;
            if (!state.Knowledge.TryGetValue(s.Id, out var k))
            {
                k = new SquadKnowledge { SquadId = s.Id };
                state.Knowledge[s.Id] = k;
            }
            k.KnownName = s.Name;
            k.Archetype = s.Archetype;
            k.LastKnownPlaceUuid = s.CurrentPlaceUuid;
            k.LastKnownPlaceName = s.CurrentPlaceName;
            k.LastKnownTurn = state.CurrentTurn;
            k.LastKnownSize = s.Size;
            k.LastKnownActivity = s.Activity;
            if (met)
            {
                k.Met = true;
                k.KnownLeaderName = s.Leader?.FullName;
            }
            else if (k.Met && s.Leader != null)
            {
                k.KnownLeaderName = s.Leader.FullName; // once met, rumors keep the name current
            }
        }

        /// <summary>The player arrived somewhere: everything that happened here is discovered.</summary>
        public static void DiscoverSite(string topUuid)
        {
            if (topUuid == null) return;
            foreach (var e in ALifeData.State.RecentEvents)
                if (e.PlaceUuid == topUuid)
                    e.Known = true;
        }

        public static List<ALifeEvent> KnownEvents(int max)
        {
            var list = ALifeData.State.RecentEvents.Where(e => e.Known).ToList();
            return list.Skip(Math.Max(0, list.Count - max)).ToList();
        }

        public static bool IsStale(SquadKnowledge k)
            => ALifeData.State.CurrentTurn - k.LastKnownTurn > RUMOR_STALE_TURNS;

        /// <summary>The band this intel describes, or null if it no longer exists (fate unknown).</summary>
        public static VirtualSquad LiveSquad(SquadKnowledge k) => ALifeData.SquadById(k.SquadId);
    }
}
