using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Extends RumorNetwork with player-reputation gossip.
    /// Significant player interactions seed facts that spread between co-located NPCs,
    /// causing small affinity shifts based on whether the news is positive or negative.
    /// Facts can distort as they spread ("I heard that...").
    /// </summary>
    public static class WorldGossipSystem
    {
        private const float GOSSIP_CHANCE = 0.30f;
        private const float DISTORT_CHANCE = 0.20f;
        private static readonly System.Random _rng = new System.Random();

        // ─── Seeding ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Seeds a gossip fact about a player–NPC interaction into the NPC's rumor network.
        /// Only significant affinity shifts (≥5 or ≤-5) produce gossip.
        /// </summary>
        public static void SeedPlayerGossip(string npcUuid, string npcName, int affinityDelta)
        {
            if (affinityDelta == 0) return;

            string fact;
            if (affinityDelta >= 15)       fact = $"Player greatly aided {npcName}.";
            else if (affinityDelta >= 5)   fact = $"Player helped {npcName}.";
            else if (affinityDelta <= -15) fact = $"Player seriously harmed {npcName}.";
            else if (affinityDelta <= -5)  fact = $"Player acted against {npcName}.";
            else return; // Too minor to gossip about

            RumorNetwork.AddFact(npcUuid, fact);
        }

        // ─── Propagation ───────────────────────────────────────────────────────────

        /// <summary>
        /// Spreads player-related gossip between NPCs in the same location.
        /// Recipients gain or lose small affinity toward the player based on content.
        /// </summary>
        public static void ProcessGossipInPlace(List<GameCharacter> npcsInPlace)
        {
            if (npcsInPlace == null || npcsInPlace.Count < 2) return;

            var pairs = npcsInPlace
                .Where(n => n != null && n.corpseState == GameCharacter.CorpseState.NONE)
                .Select(n => (npc: n, data: NPCData.Load(n.uuid)))
                .Where(p => p.data != null)
                .ToList();

            if (pairs.Count < 2) return;

            foreach (var (npc, data) in pairs)
            {
                if (data.KnownFacts == null || data.KnownFacts.Count == 0) continue;
                if (_rng.NextDouble() > GOSSIP_CHANCE) continue;

                // Find facts about the player
                var playerFacts = data.KnownFacts
                    .Where(f => f.StartsWith("Player ") || f.StartsWith("I heard that player "))
                    .ToList();

                if (playerFacts.Count == 0) continue;

                var candidates = pairs.Where(p => p.npc.uuid != npc.uuid).ToList();
                if (candidates.Count == 0) continue;

                var (targetNpc, targetData) = candidates[_rng.Next(candidates.Count)];
                string fact = playerFacts[_rng.Next(playerFacts.Count)];

                // Possible distortion on re-spread
                string spread = (!fact.StartsWith("I heard that") && _rng.NextDouble() < DISTORT_CHANCE)
                    ? "I heard that " + char.ToLower(fact[0]) + fact.Substring(1)
                    : fact;

                if (targetData.KnownFacts == null) targetData.KnownFacts = new List<string>();
                if (targetData.KnownFacts.Contains(spread)) continue;

                targetData.KnownFacts.Add(spread);
                while (targetData.KnownFacts.Count > 8) targetData.KnownFacts.RemoveAt(0);

                // Small affinity consequence toward the player
                int delta = 0;
                if (fact.Contains("aided") || fact.Contains("helped")) delta = 3;
                else if (fact.Contains("harmed") || fact.Contains("acted against")) delta = -3;

                if (delta != 0)
                {
                    targetData.ChangeAffinity(delta, $"Heard gossip from {npc.GetPrettyName()}: {spread}");
                    NPCExpansionPlugin.SyncAffinityToGame(targetNpc.uuid, targetData);
                }

                NPCData.Save(targetNpc.uuid, targetData);
                Debug.Log($"[GossipSystem] {npc.GetPrettyName()} → {targetNpc.GetPrettyName()}: '{spread}' (Δ{delta:+#;-#;0})");
            }
        }
    }
}
