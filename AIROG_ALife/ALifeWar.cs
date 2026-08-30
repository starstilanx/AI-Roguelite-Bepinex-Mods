using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// v2.1 "War Made Real": WorldExpansion declares the wars; A-Life fights them.
    /// Every warband battle feeds a per-war ledger. When one side's ledger lead
    /// reaches the push threshold, the front moves — a real territory flips owner
    /// (mod claim + native Place.faction). Enough seized ground, or a landless
    /// enemy, ends the war decisively. Battle losses also drain faction resources,
    /// feeding WorldExpansion's own exhaustion-peace check — so even a stalemate
    /// ground out by squads eventually bleeds a war to its end.
    /// </summary>
    public static class ALifeWar
    {
        public const int FRONT_PUSH_SCORE = 3;   // net battle score that moves the front
        public const int DECISIVE_SEIZURES = 3;  // net territories that end the war
        public const int BATTLE_RESOURCE_DRAIN = 8;
        public const int RAID_RESOURCE_DRAIN = 5;

        /// <summary>Is this faction the defender (target) in any active war?</summary>
        public static bool IsWarDefender(string facUuid)
        {
            if (string.IsNullOrEmpty(facUuid)) return false;
            return ALifeWorldBridge.GetActiveWars().Any(w => w.TargetUuid == facUuid);
        }

        /// <summary>
        /// A battle between two faction squads was decided. If their factions are at
        /// war, score it — and let the front move when the ledger tips.
        /// </summary>
        public static void RecordBattle(VirtualSquad winner, VirtualSquad loser, bool loserWiped, bool loserLeaderDied)
        {
            if (!ALifePlugin.CfgWarMadeReal.Value) return;
            if (winner.FactionUuid == null || loser.FactionUuid == null) return;

            var war = ALifeWorldBridge.GetActiveWars().FirstOrDefault(w =>
                (w.ActorUuid == winner.FactionUuid && w.TargetUuid == loser.FactionUuid) ||
                (w.ActorUuid == loser.FactionUuid && w.TargetUuid == winner.FactionUuid));
            if (war == null) return;
            string key = ALifeWorldBridge.GetWarKey(war.ActorUuid, war.TargetUuid);
            if (key == null) return;

            int delta = 1 + (loserWiped ? 1 : 0) + (loserLeaderDied ? 1 : 0);
            int signed = winner.FactionUuid == war.ActorUuid ? delta : -delta;

            var scores = ALifeData.State.WarScores;
            scores.TryGetValue(key, out int score);
            scores[key] = score + signed;

            ALifeWorldBridge.DrainResources(loser.FactionUuid, BATTLE_RESOURCE_DRAIN);

            string seized = CheckFrontPush(war, key, winner.CurrentPlaceUuid, winner.CurrentPlaceName);
            if (seized != null)
            {
                string winName = winner.FactionUuid == war.ActorUuid ? war.ActorName : war.TargetName;
                winner.AddChronicle($"Won the ground that took {seized} for {winName}.");
            }
        }

        /// <summary>A raid landed unopposed on faction ground. During a war this is
        /// pressure on the ledger; either way the defenders bleed for it.</summary>
        public static void RaidLanded(VirtualSquad raider, Place target)
        {
            if (!ALifePlugin.CfgWarMadeReal.Value) return;
            string targetFac = target?.faction?.uuid;
            if (targetFac == null) return;

            ALifeWorldBridge.DrainResources(targetFac, RAID_RESOURCE_DRAIN);
            ALifeWorldBridge.RollRaidCourtCasualty(targetFac, ALifeSimulation.Cap(raider.Name));

            if (raider.FactionUuid != null && ALifeWorldBridge.AreAtWar(raider.FactionUuid, targetFac))
            {
                string key = ALifeWorldBridge.GetWarKey(raider.FactionUuid, targetFac);
                if (key == null) return;
                var war = ALifeWorldBridge.GetActiveWars().FirstOrDefault(w =>
                    ALifeWorldBridge.GetWarKey(w.ActorUuid, w.TargetUuid) == key);
                if (war == null) return;
                var scores = ALifeData.State.WarScores;
                scores.TryGetValue(key, out int score);
                scores[key] = score + (raider.FactionUuid == war.ActorUuid ? 1 : -1);

                CheckFrontPush(war, key, target.uuid, target.GetPrettyName());
            }
        }

        /// <summary>
        /// Shared by battles and raids: once a ledger crosses the push threshold, the front
        /// moves (and the war may end outright). Runs the decisive-end check even when the
        /// battle itself couldn't seize anything (SeizePlace returns null when the loser was
        /// ALREADY landless) — the old code nested that check inside "if seized != null",
        /// so a landless enemy's war could never end via that promised path.
        /// Returns the seized place's name, or null if the front didn't move (or moved but
        /// nothing was there left to take).
        /// </summary>
        private static string CheckFrontPush(ALifeWorldBridge.WarInfo war, string key, string nearPlaceUuid, string atPlaceName)
        {
            var scores = ALifeData.State.WarScores;
            scores.TryGetValue(key, out int score);
            if (Math.Abs(score) < FRONT_PUSH_SCORE) return null;

            string winFac = score > 0 ? war.ActorUuid : war.TargetUuid;
            string loseFac = score > 0 ? war.TargetUuid : war.ActorUuid;
            string winName = score > 0 ? war.ActorName : war.TargetName;
            string loseName = score > 0 ? war.TargetName : war.ActorName;
            scores[key] = score - Math.Sign(score) * FRONT_PUSH_SCORE;

            string seized = ALifeWorldBridge.SeizePlace(winFac, loseFac, nearPlaceUuid);
            if (seized != null)
            {
                var seizures = ALifeData.State.WarSeizures;
                seizures.TryGetValue(key, out int net);
                net += winFac == war.ActorUuid ? 1 : -1;
                seizures[key] = net;

                string desc = $"The front has moved: after fighting at {atPlaceName}, " +
                              $"{winName} seized {seized} from {loseName}.";
                ALifeData.LogEvent(nearPlaceUuid, atPlaceName, "WAR", desc);
                ALifeWorldBridge.PushWorldEvent(desc, alertPlayer: false);
            }

            // Decisive end: enough ground taken, or nothing left to take — checked
            // regardless of whether THIS push seized anything, since the loser may
            // already have been landless going in.
            ALifeData.State.WarSeizures.TryGetValue(key, out int netNow);
            if (Math.Abs(netNow) >= DECISIVE_SEIZURES || ALifeWorldBridge.GetClaimedCount(loseFac) == 0)
            {
                ALifeWorldBridge.EndWarDecisively(war.ActorUuid, war.TargetUuid,
                    $"decided in the field — {winName}'s warbands broke {loseName}");
                ALifeData.State.WarScores.Remove(key);
                ALifeData.State.WarSeizures.Remove(key);
            }
            return seized;
        }

        /// <summary>Drop ledger entries for wars that ended by other means (peace, exhaustion).</summary>
        public static void Upkeep()
        {
            var liveKeys = new HashSet<string>(ALifeWorldBridge.GetActiveWars()
                .Select(w => ALifeWorldBridge.GetWarKey(w.ActorUuid, w.TargetUuid))
                .Where(k => k != null));
            foreach (var key in ALifeData.State.WarScores.Keys.ToList())
                if (!liveKeys.Contains(key)) ALifeData.State.WarScores.Remove(key);
            foreach (var key in ALifeData.State.WarSeizures.Keys.ToList())
                if (!liveKeys.Contains(key)) ALifeData.State.WarSeizures.Remove(key);
        }

        /// <summary>Human-readable front status for a war, or null when the ledger is even.</summary>
        public static string FrontLine(ALifeWorldBridge.WarInfo war)
        {
            string key = ALifeWorldBridge.GetWarKey(war.ActorUuid, war.TargetUuid);
            if (key == null) return null;
            ALifeData.State.WarScores.TryGetValue(key, out int score);
            ALifeData.State.WarSeizures.TryGetValue(key, out int net);
            if (score == 0 && net == 0) return null;
            string leader = (net != 0 ? net : score) > 0 ? war.ActorName : war.TargetName;
            string detail = net != 0
                ? $"{Math.Abs(net)} territor{(Math.Abs(net) == 1 ? "y" : "ies")} taken"
                : "winning the skirmishes";
            return $"the front favors {leader} ({detail})";
        }
    }
}
