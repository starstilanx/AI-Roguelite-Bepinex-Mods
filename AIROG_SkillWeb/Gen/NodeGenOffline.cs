using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public static class NodeGenOffline
    {
        private static readonly string[] Attributes = { "Strength", "Dexterity", "Intellect", "Cunning", "Charisma" };

        /// <summary>
        /// Analyzes a sector to identify its best thematic attribute mapping.
        /// </summary>
        public static string GetSectorAttribute(WebSector sector)
        {
            if (sector == null) return "Strength";
            string text = ((sector.name ?? "") + " " + (sector.purpose ?? "")).ToLowerInvariant();
            
            string bestAttr = "Strength";
            int maxHits = -1;

            var keywords = new Dictionary<string, string[]>
            {
                ["Strength"]  = new[] { "strength", "strong", "might", "mighty", "power", "force", "muscle", "brawn", "tough", "endurance", "stamina", "physical", "crush", "smash", "heavy", "carry", "fortitude", "constitution", "vigor", "iron" },
                ["Dexterity"] = new[] { "dexterity", "dexterous", "agile", "agility", "nimble", "speed", "swift", "quick", "reflex", "evasion", "dodge", "balance", "acrobat", "finesse", "precision", "aim", "archery", "footwork" },
                ["Intellect"] = new[] { "intellect", "intelligence", "intelligent", "magic", "magical", "arcane", "spell", "mana", "wisdom", "knowledge", "lore", "scholar", "study", "mind", "mental", "logic", "reason", "rune", "enchant", "mystic", "wit" },
                ["Cunning"]   = new[] { "cunning", "stealth", "sneak", "guile", "trick", "deceit", "deception", "shadow", "thief", "steal", "lockpick", "trap", "ambush", "subterfuge", "sly", "sabotage", "poison", "assassin", "scheme" },
                ["Charisma"]  = new[] { "charisma", "charm", "charming", "social", "persuade", "persuasion", "leader", "leadership", "diplomacy", "diplomat", "intimidate", "speech", "rhetoric", "inspire", "command", "presence", "negotiat", "barter", "seduc", "noble" },
            };

            foreach (var kvp in keywords)
            {
                int count = 0;
                foreach (var kw in kvp.Value)
                {
                    if (text.Contains(kw)) count++;
                }
                if (count > maxHits)
                {
                    maxHits = count;
                    bestAttr = kvp.Key;
                }
            }
            return bestAttr;
        }

        /// <summary>
        /// Generates a node using deterministic lexicon lookup based on the seed.
        /// </summary>
        public static WebNode GenerateNode(string id, WebNodeType type, WebSector sector, int ring, int index, long layoutSeed)
        {
            var node = new WebNode
            {
                id = id,
                type = type,
                sectorId = sector?.id,
                ring = ring,
                unlocked = false,
                tier = (type == WebNodeType.Basic || type == WebNodeType.Notable || type == WebNodeType.Confluence) ? 1 : 0,
                aiRefined = false
            };

            // Seed deterministic generator
            int sectorHash = sector != null ? sector.id.GetHashCode() : 0;
            int nodeSeed = (int)(layoutSeed ^ (sectorHash * 397) ^ (ring * 17) ^ (index * 7) ^ (int)type);
            var rng = new System.Random(nodeSeed);

            string primaryAttr = GetSectorAttribute(sector);

            // Fetch list arrays
            string[] prefixList = ThemeLexicon.Prefixes[primaryAttr];
            string[] nounList = ThemeLexicon.Nouns[primaryAttr];
            string[] flavorList = ThemeLexicon.Flavors[primaryAttr];
            string[] traitList = ThemeLexicon.Traits[primaryAttr];

            string prefix = prefixList[rng.Next(prefixList.Length)];
            string noun = nounList[rng.Next(nounList.Length)];
            string flavor = flavorList[rng.Next(flavorList.Length)];

            switch (type)
            {
                case WebNodeType.Basic:
                    node.name = $"{prefix} {noun}";
                    node.description = flavor;
                    node.stats[primaryAttr] = rng.Next(2, 5); // +2 to +4
                    break;

                case WebNodeType.Notable:
                    node.name = $"{prefix} {noun}";
                    node.description = flavor;
                    node.stats[primaryAttr] = rng.Next(4, 7); // +4 to +6
                    if (rng.NextDouble() < 0.50f)
                    {
                        // Add secondary stat
                        string secAttr = primaryAttr;
                        while (secAttr == primaryAttr)
                        {
                            secAttr = Attributes[rng.Next(Attributes.Length)];
                        }
                        node.stats[secAttr] = rng.Next(2, 5); // +2 to +4
                    }
                    node.traits.Add(traitList[rng.Next(traitList.Length)]);
                    break;

                case WebNodeType.Confluence:
                    // Blends two adjoining sectors
                    node.name = $"{prefix} of the Nexus";
                    node.description = "A powerful node channeling the overlapping forces of adjacent disciplines.";
                    node.stats[primaryAttr] = rng.Next(3, 6);
                    string otherAttr = Attributes[rng.Next(Attributes.Length)];
                    while (otherAttr == primaryAttr)
                    {
                        otherAttr = Attributes[rng.Next(Attributes.Length)];
                    }
                    node.stats[otherAttr] = rng.Next(3, 6);
                    node.traits.Add($"Nexus Alignment ({primaryAttr}/{otherAttr})");
                    break;

                case WebNodeType.Keystone:
                    int ksIndex = rng.Next(ThemeLexicon.GenericKeystones.Length);
                    node.name = ThemeLexicon.GenericKeystones[ksIndex];
                    node.description = "A paradigm-shifting constellation anchor.";
                    node.keystoneRule = ThemeLexicon.GenericKeystoneRules[ksIndex];
                    
                    // Attribute trade
                    node.stats[primaryAttr] = rng.Next(10, 15); // +10 to +14
                    
                    string penaltyAttr = primaryAttr;
                    while (penaltyAttr == primaryAttr)
                    {
                        penaltyAttr = Attributes[rng.Next(Attributes.Length)];
                    }
                    node.stats[penaltyAttr] = -rng.Next(4, 7); // -4 to -6
                    
                    node.traits.Add(traitList[rng.Next(traitList.Length)]);
                    break;
            }

            return node;
        }
    }
}
