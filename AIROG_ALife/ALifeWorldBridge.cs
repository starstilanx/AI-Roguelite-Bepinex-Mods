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

        private static void Fail(Exception ex)
        {
            if (!_failed)
                Debug.LogWarning("[ALife] WorldExpansion bridge unavailable — running standalone. (" + ex.GetType().Name + ")");
            _failed = true;
        }
    }
}
