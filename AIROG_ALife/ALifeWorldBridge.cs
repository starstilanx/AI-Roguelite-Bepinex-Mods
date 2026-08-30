using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// Soft bridge to AIROG_WorldExpansion. All access to WorldExpansion types lives in
    /// [NoInlining] inner methods so that, if the DLL is absent, the TypeLoadException
    /// fires inside our try/catch on first use and the bridge degrades to "no wars,
    /// no territories" instead of killing the plugin.
    /// </summary>
    public static class ALifeWorldBridge
    {
        private static bool _failed;

        /// <summary>
        /// Clear the failure latch. Without this, one transient exception (e.g. WorldExpansion's
        /// state not yet populated during an early load-order race) disabled War Made Real for
        /// the rest of the process — including after loading into a perfectly healthy game or
        /// starting a brand new one. Called on New Game and Load Game.
        /// </summary>
        public static void Reset()
        {
            _failed = false;
        }

        public class WarInfo
        {
            public string ActorUuid, ActorName, TargetUuid, TargetName;
        }

        public static List<WarInfo> GetActiveWars()
        {
            if (_failed) return new List<WarInfo>();
            try { return GetActiveWarsInner(); }
            catch (Exception ex) { Fail(ex); return new List<WarInfo>(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<WarInfo> GetActiveWarsInner()
        {
            return AIROG_WorldExpansion.WorldData.CurrentState.ActiveWars.Values
                .Select(w => new WarInfo
                {
                    ActorUuid = w.ActorUuid, ActorName = w.ActorName,
                    TargetUuid = w.TargetUuid, TargetName = w.TargetName
                }).ToList();
        }

        public static bool AreAtWar(string facA, string facB)
        {
            if (string.IsNullOrEmpty(facA) || string.IsNullOrEmpty(facB) || facA == facB) return false;
            return GetActiveWars().Any(w =>
                (w.ActorUuid == facA && w.TargetUuid == facB) ||
                (w.ActorUuid == facB && w.TargetUuid == facA));
        }

        /// <summary>Faction uuids this faction is at war with.</summary>
        public static List<string> WarEnemiesOf(string facUuid)
        {
            return GetActiveWars()
                .Where(w => w.ActorUuid == facUuid || w.TargetUuid == facUuid)
                .Select(w => w.ActorUuid == facUuid ? w.TargetUuid : w.ActorUuid)
                .ToList();
        }

        public static List<string> GetClaimedPlaces(string factionUuid)
        {
            if (_failed || string.IsNullOrEmpty(factionUuid)) return new List<string>();
            try { return GetClaimedPlacesInner(factionUuid); }
            catch (Exception ex) { Fail(ex); return new List<string>(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<string> GetClaimedPlacesInner(string factionUuid)
        {
            var factions = AIROG_WorldExpansion.WorldData.CurrentState.Factions;
            if (factions != null && factions.TryGetValue(factionUuid, out var ext) && ext.ClaimedPlaceUuids != null)
                return new List<string>(ext.ClaimedPlaceUuids);
            return new List<string>();
        }

        public static bool PlayerHasBountyFrom(string factionUuid)
        {
            if (_failed || string.IsNullOrEmpty(factionUuid)) return false;
            try { return PlayerHasBountyFromInner(factionUuid); }
            catch (Exception ex) { Fail(ex); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PlayerHasBountyFromInner(string factionUuid)
        {
            var b = AIROG_WorldExpansion.WorldData.CurrentState.PlayerBounties;
            return b != null && b.Contains(factionUuid);
        }

        /// <summary>Push a big A-Life happening into WorldExpansion's event log + player alert queue.</summary>
        public static void PushWorldEvent(string desc, bool alertPlayer)
        {
            if (_failed) return;
            try { PushWorldEventInner(desc, alertPlayer); }
            catch (Exception ex) { Fail(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PushWorldEventInner(string desc, bool alertPlayer)
        {
            AIROG_WorldExpansion.WorldData.LogEvent(desc, "ALIFE");
            if (alertPlayer)
                AIROG_WorldExpansion.WorldData.QueuePlayerEvent(desc, "ALIFE");
        }

        // ── v2.1 War Made Real: A-Life WRITES into WorldExpansion's war ──────────

        /// <summary>WorldExpansion's canonical relationship key for a faction pair (order-independent).</summary>
        public static string GetWarKey(string facA, string facB)
        {
            if (_failed || string.IsNullOrEmpty(facA) || string.IsNullOrEmpty(facB)) return null;
            try { return GetWarKeyInner(facA, facB); }
            catch (Exception ex) { Fail(ex); return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GetWarKeyInner(string facA, string facB)
        {
            return AIROG_WorldExpansion.WorldData.GetRelationshipKey(facA, facB);
        }

        /// <summary>
        /// The front moves: the winner's faction seizes one of the loser's claimed places
        /// (nearest to the battle), flipping BOTH the mod claim and native Place.faction.
        /// Returns the seized place's name, or null if nothing could be taken.
        /// </summary>
        public static string SeizePlace(string winnerFacUuid, string loserFacUuid, string nearPlaceUuid)
        {
            if (_failed) return null;
            try { return SeizePlaceInner(winnerFacUuid, loserFacUuid, nearPlaceUuid); }
            catch (Exception ex) { Fail(ex); return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SeizePlaceInner(string winnerFacUuid, string loserFacUuid, string nearPlaceUuid)
        {
            var factions = AIROG_WorldExpansion.WorldData.CurrentState.Factions;
            if (!factions.TryGetValue(winnerFacUuid, out var winner)) return null;
            if (!factions.TryGetValue(loserFacUuid, out var loser)) return null;
            if (loser.ClaimedPlaceUuids == null || loser.ClaimedPlaceUuids.Count == 0) return null;

            // Prefer the claimed place closest to where the fighting happened.
            Place near = ALifeGraph.PlaceByUuid(nearPlaceUuid);
            string targetUuid = null;
            float best = float.MaxValue;
            foreach (var uuid in loser.ClaimedPlaceUuids)
            {
                Place pl = ALifeGraph.PlaceByUuid(uuid);
                if (pl == null) { targetUuid = targetUuid ?? uuid; continue; }
                float d = near != null ? (pl.worldCoords - near.worldCoords).sqrMagnitude : 0f;
                if (d < best) { best = d; targetUuid = uuid; }
            }
            if (targetUuid == null) return null;

            loser.ClaimedPlaceUuids.Remove(targetUuid);
            if (winner.ClaimedPlaceUuids == null) winner.ClaimedPlaceUuids = new List<string>();
            winner.ClaimedPlaceUuids.Add(targetUuid);

            string placeName = targetUuid;
            if (SS.I?.uuidToGameEntityMap != null
                && SS.I.uuidToGameEntityMap.TryGetValue(targetUuid, out var ent) && ent is Place place)
            {
                var manager = SS.I.hackyManager;
                Faction winFac = (manager?.GetCurrentFactions() ?? new List<Faction>())
                    .FirstOrDefault(f => f != null && f.uuid == winnerFacUuid);
                if (winFac != null) place.faction = winFac; // native ownership, visible in-game
                placeName = place.GetPrettyName();
            }
            return placeName;
        }

        public static int GetClaimedCount(string factionUuid) => GetClaimedPlaces(factionUuid).Count;

        /// <summary>End a war outright — the field decided it.</summary>
        public static void EndWarDecisively(string facA, string facB, string reason)
        {
            if (_failed) return;
            try { EndWarInner(facA, facB, reason); }
            catch (Exception ex) { Fail(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EndWarInner(string facA, string facB, string reason)
        {
            string key = AIROG_WorldExpansion.WorldData.GetRelationshipKey(facA, facB);
            AIROG_WorldExpansion.WorldData.EndWar(key, reason);
        }

        /// <summary>Battle losses cost the faction treasury (feeds WorldExpansion's own exhaustion-peace check).</summary>
        public static void DrainResources(string factionUuid, int amount)
        {
            if (_failed || string.IsNullOrEmpty(factionUuid)) return;
            try { DrainResourcesInner(factionUuid, amount); }
            catch (Exception ex) { Fail(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DrainResourcesInner(string factionUuid, int amount)
        {
            var factions = AIROG_WorldExpansion.WorldData.CurrentState.Factions;
            if (factions.TryGetValue(factionUuid, out var data))
                data.Resources = Math.Max(0, data.Resources - amount);
        }

        /// <summary>A landed raid can kill members of the defender's court (WorldExpansion's own odds).</summary>
        public static void RollRaidCourtCasualty(string defenderFacUuid, string attackerName)
        {
            if (_failed || string.IsNullOrEmpty(defenderFacUuid)) return;
            try { RollRaidCourtCasualtyInner(defenderFacUuid, attackerName); }
            catch (Exception ex) { Fail(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RollRaidCourtCasualtyInner(string defenderFacUuid, string attackerName)
        {
            var factions = AIROG_WorldExpansion.WorldData.CurrentState.Factions;
            if (factions.TryGetValue(defenderFacUuid, out var data))
                AIROG_WorldExpansion.FactionCourtSystem.RollRaidCasualty(data, attackerName);
        }

        // ── v2.1 court figures take the field ────────────────────────────────────

        public class FieldFigure
        {
            public string Name, Title;
        }

        /// <summary>A living, unbound lieutenant of this faction willing to lead a warband, or null.</summary>
        public static FieldFigure GetFieldLieutenant(string factionUuid)
        {
            if (_failed || string.IsNullOrEmpty(factionUuid)) return null;
            try { return GetFieldLieutenantInner(factionUuid); }
            catch (Exception ex) { Fail(ex); return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static FieldFigure GetFieldLieutenantInner(string factionUuid)
        {
            var factions = AIROG_WorldExpansion.WorldData.CurrentState.Factions;
            if (!factions.TryGetValue(factionUuid, out var data) || data.Lieutenants == null) return null;
            // Don't put a lieutenant in two places: skip ones already leading an A-Life squad.
            var taken = new HashSet<string>(ALifeData.State.Squads
                .Where(s => s.CourtFigureName != null)
                .Select(s => s.CourtFigureName));
            var lt = data.Lieutenants.FirstOrDefault(l =>
                !l.IsDead && string.IsNullOrEmpty(l.BoundNpcUuid) && !taken.Contains(l.Name));
            return lt == null ? null : new FieldFigure { Name = lt.Name, Title = lt.Title };
        }

        /// <summary>A court lieutenant leading a warband has fallen in the field. Idempotent.</summary>
        public static void MarkFieldFigureDead(string factionUuid, string figureName, string cause)
        {
            if (_failed || string.IsNullOrEmpty(factionUuid) || string.IsNullOrEmpty(figureName)) return;
            try { MarkFieldFigureDeadInner(factionUuid, figureName, cause); }
            catch (Exception ex) { Fail(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void MarkFieldFigureDeadInner(string factionUuid, string figureName, string cause)
        {
            var factions = AIROG_WorldExpansion.WorldData.CurrentState.Factions;
            if (!factions.TryGetValue(factionUuid, out var data)) return;
            var fig = data.Lieutenants?.FirstOrDefault(l => l.Name == figureName && !l.IsDead);
            if (fig != null)
            {
                fig.IsDead = true;
                AIROG_WorldExpansion.WorldData.LogEvent(
                    $"{fig.Display} of {data.Name} {cause}.", "COURT");
                return;
            }
            // Rare: the figure was promoted to Leader while in the field.
            if (data.Leader != null && data.Leader.Name == figureName && !data.Leader.IsDead)
            {
                data.Leader.IsDead = true;
                AIROG_WorldExpansion.FactionCourtSystem.Succeed(data, $"{data.Leader.Display} {cause}");
            }
        }

        private static void Fail(Exception ex)
        {
            if (!_failed)
                Debug.LogWarning("[ALife] WorldExpansion bridge unavailable — running standalone. (" + ex.GetType().Name + ")");
            _failed = true;
        }
    }
}
