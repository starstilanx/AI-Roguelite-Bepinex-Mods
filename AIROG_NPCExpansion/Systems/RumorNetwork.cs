using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Spreads knowledge facts between NPCs in the same location each tick.
    /// Facts are seeded by scenario updates and player interactions.
    /// </summary>
    public static class RumorNetwork
    {
        private const int MAX_FACTS = 8;
        private const float SPREAD_CHANCE = 0.35f;
        private static readonly System.Random _rng = new System.Random();

        /// <summary>Propagate one fact between NPCs in the same place.</summary>
        public static void PropagateInPlace(List<GameCharacter> npcsInPlace)
        {
            if (npcsInPlace == null || npcsInPlace.Count < 2) return;

            var lorePairs = npcsInPlace
                .Where(n => n != null && n.corpseState == GameCharacter.CorpseState.NONE)
                .Select(n => (npc: n, data: NPCData.Load(n.uuid)))
                .Where(p => p.data != null)
                .ToList();

            if (lorePairs.Count < 2) return;

            foreach (var (npc, data) in lorePairs)
            {
                if (data.KnownFacts == null || data.KnownFacts.Count == 0) continue;
                if (_rng.NextDouble() > SPREAD_CHANCE) continue;

                // Pick a random target that isn't self
                var candidates = lorePairs.Where(p => p.npc.uuid != npc.uuid).ToList();
                if (candidates.Count == 0) continue;

                var (targetNpc, targetData) = candidates[_rng.Next(candidates.Count)];
                string fact = data.KnownFacts[_rng.Next(data.KnownFacts.Count)];

                if (targetData.KnownFacts == null) targetData.KnownFacts = new List<string>();
                if (!targetData.KnownFacts.Contains(fact))
                {
                    targetData.KnownFacts.Add(fact);
                    while (targetData.KnownFacts.Count > MAX_FACTS)
                        targetData.KnownFacts.RemoveAt(0);
                    NPCData.Save(targetNpc.uuid, targetData);
                    Debug.Log($"[RumorNetwork] '{fact}' spread from {npc.GetPrettyName()} to {targetNpc.GetPrettyName()}");
                }
            }
        }

        /// <summary>Seed a fact into an NPC's known facts. Truncated to 80 chars.</summary>
        public static void AddFact(string npcUuid, string fact)
        {
            if (string.IsNullOrEmpty(fact) || string.IsNullOrEmpty(npcUuid)) return;
            var data = NPCData.Load(npcUuid);
            if (data == null) return;

            fact = fact.Length > 80 ? fact.Substring(0, 80) : fact;
            if (data.KnownFacts == null) data.KnownFacts = new List<string>();
            if (!data.KnownFacts.Contains(fact))
            {
                data.KnownFacts.Add(fact);
                while (data.KnownFacts.Count > MAX_FACTS)
                    data.KnownFacts.RemoveAt(0);
                NPCData.Save(npcUuid, data);
            }
        }
    }
}
