using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Maps a native perk's name + description onto attribute bonuses
    /// (Strength / Dexterity / Intellect / Cunning / Charisma).
    /// The native perk system is purely narrative; this is the mechanical bridge.
    /// </summary>
    public static class PerkStatDeriver
    {
        static readonly string[] Attrs = { "Strength", "Dexterity", "Intellect", "Cunning", "Charisma" };

        // Keyword → attribute. Matched case-insensitively against name + description.
        static readonly Dictionary<string, string[]> Keywords = new Dictionary<string, string[]>
        {
            ["Strength"]  = new[] { "strength", "strong", "might", "mighty", "power", "force", "muscle", "brawn", "tough", "endurance", "stamina", "physical", "crush", "smash", "heavy", "carry", "fortitude", "constitution", "vigor", "iron" },
            ["Dexterity"] = new[] { "dexterity", "dexterous", "agile", "agility", "nimble", "speed", "swift", "quick", "reflex", "evasion", "dodge", "balance", "acrobat", "finesse", "precision", "aim", "archery", "footwork" },
            ["Intellect"] = new[] { "intellect", "intelligence", "intelligent", "magic", "magical", "arcane", "spell", "mana", "wisdom", "knowledge", "lore", "scholar", "study", "mind", "mental", "logic", "reason", "rune", "enchant", "mystic", "wit" },
            ["Cunning"]   = new[] { "cunning", "stealth", "sneak", "guile", "trick", "deceit", "deception", "shadow", "thief", "steal", "lockpick", "trap", "ambush", "subterfuge", "sly", "sabotage", "poison", "assassin", "scheme" },
            ["Charisma"]  = new[] { "charisma", "charm", "charming", "social", "persuade", "persuasion", "leader", "leadership", "diplomacy", "diplomat", "intimidate", "speech", "rhetoric", "inspire", "command", "presence", "negotiat", "barter", "seduc", "noble" },
        };

        // ── Deterministic fallback ────────────────────────────────────────────────

        /// <summary>
        /// Keyword-based derivation. Distributes <paramref name="budget"/> across whichever
        /// attributes the perk text references. Returns an empty dict if nothing matched
        /// (a purely-narrative perk gives no mechanical bonus, which is fine — native still injects it).
        /// </summary>
        public static Dictionary<string, float> Heuristic(string name, string description, float budget)
        {
            string text = ((name ?? "") + " " + (description ?? "")).ToLowerInvariant();
            var hits = new Dictionary<string, int>();

            foreach (var kvp in Keywords)
            {
                int count = kvp.Value.Count(kw => text.Contains(kw));
                if (count > 0) hits[kvp.Key] = count;
            }

            var result = new Dictionary<string, float>();
            if (hits.Count == 0) return result;

            int totalHits = hits.Values.Sum();
            foreach (var kvp in hits)
            {
                float share = budget * kvp.Value / totalHits;
                // Round to the nearest whole point, minimum 1 for any matched attribute.
                result[kvp.Key] = Mathf.Max(1f, Mathf.Round(share));
            }
            return result;
        }

        // ── AI refinement ─────────────────────────────────────────────────────────

        private class StatResp
        {
#pragma warning disable 0649 // assigned by Newtonsoft.Json during deserialization
            public Dictionary<string, object> stats;
#pragma warning restore 0649
        }

        /// <summary>
        /// Asks the AI to map the perk onto attribute bonuses. Returns null on any failure
        /// so the caller can keep the heuristic result.
        /// </summary>
        public static async Task<Dictionary<string, float>> ViaAI(GameplayManager manager, string name, string description)
        {
            string prompt =
                "You assign tabletop-RPG attribute bonuses to a character perk.\n\n" +
                "Perk name: " + (name ?? "") + "\n" +
                "Perk description: " + (description ?? "") + "\n\n" +
                "Rules:\n" +
                "- Use ONLY these attributes: Strength, Dexterity, Intellect, Cunning, Charisma.\n" +
                "- Include only attributes the perk thematically improves (usually 1-2, never more than 3).\n" +
                "- Values 1-12. A perk with a strong drawback may give a small negative to one attribute.\n" +
                "- If the perk is purely narrative with no attribute relevance, return an empty stats object.\n\n" +
                "Respond ONLY with JSON, no prose:\n" +
                "{ \"stats\": { \"Strength\": 5 } }";

            try
            {
                string response = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE,
                    false, false, null, false, true,
                    AIAsker.ModelOverrideMode.GOOD_FOR_CORRECTNESS,
                    true);

                int s = response.IndexOf('{');
                int e = response.LastIndexOf('}');
                if (s == -1 || e == -1) return null;

                var parsed = JsonConvert.DeserializeObject<StatResp>(response.Substring(s, e - s + 1));
                if (parsed?.stats == null) return new Dictionary<string, float>(); // valid empty result

                var result = new Dictionary<string, float>();
                foreach (var kvp in parsed.stats)
                {
                    string key = Attrs.FirstOrDefault(a => string.Equals(a, kvp.Key, StringComparison.OrdinalIgnoreCase));
                    if (key == null) continue;
                    float val = ParseNum(kvp.Value);
                    if (val != 0f) result[key] = val;
                }
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] AI stat derivation failed for '" + name + "': " + ex.Message);
                return null;
            }
        }

        static float ParseNum(object value)
        {
            try
            {
                if (value is double d) return (float)d;
                if (value is long l) return l;
                if (value is int i) return i;
                if (value is float f) return f;
                if (value is string str && float.TryParse(
                    new string(str.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray()),
                    out float r)) return r;
            }
            catch { }
            return 0f;
        }
    }
}
