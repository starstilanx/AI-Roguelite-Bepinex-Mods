using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Rewrites a charted ring's Notables, Keystones, Masteries and Confluences so they belong to
    /// the current world. Structure and node types never change here; stats are clamped back to
    /// each type's budget so the AI can't inflate the web.
    /// </summary>
    public static class NodeGenAI
    {
        private static readonly string[] Attributes = ThemeLexicon.Attributes;

#pragma warning disable 0649 // populated by JSON deserialization
        class AINodeInfo
        {
            public string id;
            public string name;
            public string description;
            public Dictionary<string, object> stats;
            public List<string> traits;
            public string keystoneRule;
            public string abilityName;
            public List<string> options;
        }
#pragma warning restore 0649

        public class RefinedNode
        {
            public string id;
            public string name;
            public string description;
            public Dictionary<string, float> stats;
            public List<string> traits;
            public string keystoneRule;
            public string abilityName;
            public List<string> masteryOptions;
        }

        // ── Context Helpers ─────────────────────────────────────────────────────

        private static string WorldContext(GameplayManager manager)
        {
            if (manager == null) return "Unknown World";
            var univ = manager.GetCurrentUniverse();
            string univName = univ?.GetPrettyName() ?? "Unknown";
            string univDesc = univ?.GetPotentiallyNullDescription() ?? "";
            string worldBkg = manager.worldBackgroundText ?? "";
            string placeName = manager.currentPlace?.GetPrettyName() ?? "Unknown";
            string placeDesc = manager.currentPlace?.GetPotentiallyNullDescription() ?? "";
            return
                "Universe: " + univName + "\n" +
                "Universe Lore: " + univDesc + "\n" +
                "World Background: " + worldBkg + "\n" +
                "Current Location: " + placeName + " — " + placeDesc;
        }

        private static string PlayerContext(GameplayManager manager)
        {
            if (manager == null) return "Player";
            var pc = manager.playerCharacter;
            string playerName = pc?.pcGameEntity?.playerName ?? "Player";
            string level = (pc?.playerLevel ?? 1).ToString();
            string background = manager.playerBackgroundText ?? "";

            string skills = "";
            if (pc?.pcGameEntity?.skillsDict != null)
                foreach (var kvp in pc.pcGameEntity.skillsDict)
                    skills += "  - " + kvp.Value.skillName + " (Lv " + kvp.Value.level + ")\n";

            return
                "Name: " + playerName + "\n" +
                "Level: " + level + "\n" +
                "Background: " + background + "\n" +
                (skills.Length > 0 ? "Known Skills:\n" + skills : "");
        }

        static string Budget(WebNode n, WebSector s)
        {
            switch (n.type)
            {
                case WebNodeType.Notable:
                    return $"Notable. Stats: {s.attr} +2 to +3 and {s.altAttr} +1 to +2. Exactly 1 trait: one sentence describing how the world treats the player.";
                case WebNodeType.Confluence:
                    return "Confluence joining two disciplines. Stats: +2 in each of the two attributes already on it. Exactly 1 trait sentence that blends both.";
                case WebNodeType.Keystone:
                    return $"Keystone. Stats: {s.attr} +4 to +6 and one other attribute -1 to -3. keystoneRule: one sentence giving a strong advantage AND a real drawback the narrator must enforce. abilityName: 1-4 words naming the active ability it grants. No traits.";
                case WebNodeType.Mastery:
                    return "Mastery. No stats. options: exactly 3 alternative trait sentences, each a different small advantage the player can choose between.";
                default:
                    return "";
            }
        }

        // ── Main entry point ──────────────────────────────────────────────────────

        /// <summary>
        /// Asks the AI to rewrite <paramref name="batch"/> (all in one sector ring). Returns null on
        /// failure; nodes the response leaves out simply keep their offline content.
        /// </summary>
        public static async Task<List<RefinedNode>> RefineBatchAsync(GameplayManager manager, SkillWebData data,
            WebSector sector, int ring, List<WebNode> batch)
        {
            if (manager == null || sector == null || batch == null || batch.Count == 0) return null;

            string continuity = "";
            var inner = data.nodes.Where(n => n.sectorId == sector.id && n.ring == ring - 1 && n.aiRefined
                                              && (n.type == WebNodeType.Notable || n.type == WebNodeType.Keystone))
                                  .Take(5).ToList();
            if (inner.Count > 0)
                continuity = "\nContinuity (nodes one ring further in): " +
                             string.Join(", ", inner.Select(n => $"{n.name} ({n.description})"));

            string recipe = "";
            foreach (var n in batch)
            {
                string current = n.type == WebNodeType.Confluence
                    ? " Current stats: " + string.Join(", ", n.stats.Select(kv => kv.Key + " " + kv.Value))
                    : "";
                string joins = n.type == WebNodeType.Confluence
                    ? $" Joins {sector.name} with {data.GetSector(n.sectorId2)?.name ?? "a neighbouring discipline"}."
                    : "";
                recipe += $"- id \"{n.id}\": {Budget(n, sector)}{joins}{current}\n";
            }

            string prompt =
                "You are designing nodes for a lore-driven RPG passive skill web.\n\n" +
                "=== WORLD ===\n" + WorldContext(manager) + "\n\n" +
                "=== PLAYER ===\n" + PlayerContext(manager) + "\n\n" +
                "=== DISCIPLINE ===\n" +
                $"Name: {sector.name}\n" +
                $"Theme: {sector.purpose}\n" +
                $"Primary attribute: {sector.attr}; secondary: {sector.altAttr}\n" +
                $"Ring: {ring} of {data.maxRing} (higher rings are rarer and more dramatic)" +
                continuity + "\n\n" +
                "=== NODES TO WRITE ===\n" + recipe + "\n" +
                "=== RULES ===\n" +
                "- Fit the world's genre and tone; never assume fantasy if the world is not fantasy.\n" +
                "- Names: 2-4 evocative words. Descriptions: one sentence of lore.\n" +
                "- Stats: ONLY these keys: Strength, Dexterity, Intellect, Cunning, Charisma.\n" +
                "- Traits and options are narrative capabilities the storyteller will honour, written as one sentence each.\n\n" +
                "Respond ONLY with a JSON array, one object per node, no prose:\n" +
                "[{\"id\":\"...\",\"name\":\"...\",\"description\":\"...\",\"stats\":{\"Strength\":2},\"traits\":[\"...\"],\"keystoneRule\":null,\"abilityName\":null,\"options\":null}]";

            try
            {
                string response = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE,
                    false, false, null, false, true,
                    AIAsker.ModelOverrideMode.GOOD_FOR_CORRECTNESS,
                    true);

                int s = response.IndexOf('[');
                int e = response.LastIndexOf(']');
                if (s == -1 || e <= s) return null;

                var raw = JsonConvert.DeserializeObject<List<AINodeInfo>>(response.Substring(s, e - s + 1));
                if (raw == null || raw.Count == 0) return null;

                var result = new List<RefinedNode>();
                for (int i = 0; i < batch.Count; i++)
                {
                    var node = batch[i];
                    var info = raw.Find(x => x != null && x.id == node.id) ?? (i < raw.Count ? raw[i] : null);
                    if (info == null) continue;
                    result.Add(Clamp(node, sector, info));
                }
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkillWeb] AI refinement failed for {sector.name} ring {ring}: {ex.Message}. Keeping offline content.");
                return null;
            }
        }

        // ── Validation and Clamping ─────────────────────────────────────────────

        static RefinedNode Clamp(WebNode node, WebSector sector, AINodeInfo info)
        {
            var r = new RefinedNode
            {
                id = node.id,
                name = string.IsNullOrWhiteSpace(info.name) ? node.name : info.name.Trim(),
                description = string.IsNullOrWhiteSpace(info.description) ? node.description : info.description.Trim(),
                stats = new Dictionary<string, float>(),
                traits = (info.traits ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList(),
                keystoneRule = null,
            };

            var parsed = new Dictionary<string, float>();
            if (info.stats != null)
            {
                foreach (var kvp in info.stats)
                {
                    string attr = Attributes.FirstOrDefault(a => string.Equals(a, kvp.Key, StringComparison.OrdinalIgnoreCase));
                    float val = ParseNum(kvp.Value);
                    if (attr != null && val != 0f) parsed[attr] = val;
                }
            }

            switch (node.type)
            {
                case WebNodeType.Notable:
                    r.stats[sector.attr] = Mathf.Clamp(Mathf.Round(Get(parsed, sector.attr, 2)), 2, 3);
                    r.stats[sector.altAttr] = Mathf.Clamp(Mathf.Round(Get(parsed, sector.altAttr, 1)), 1, 2);
                    r.traits = OneTrait(r.traits, node);
                    break;

                case WebNodeType.Confluence:
                    // Keep the attributes the generator chose (they encode which sectors it joins).
                    foreach (var kv in node.stats) r.stats[kv.Key] = kv.Value;
                    r.traits = OneTrait(r.traits, node);
                    break;

                case WebNodeType.Keystone:
                {
                    r.stats[sector.attr] = Mathf.Clamp(Mathf.Round(Get(parsed, sector.attr, 5)), 4, 6);
                    string neg = parsed.Where(kv => kv.Value < 0 && kv.Key != sector.attr).Select(kv => kv.Key).FirstOrDefault()
                              ?? node.stats.Where(kv => kv.Value < 0).Select(kv => kv.Key).FirstOrDefault()
                              ?? Attributes.First(a => a != sector.attr && a != sector.altAttr);
                    r.stats[neg] = -Mathf.Clamp(Mathf.Round(Mathf.Abs(Get(parsed, neg, 2))), 1, 3);
                    r.traits = new List<string>();
                    r.keystoneRule = string.IsNullOrWhiteSpace(info.keystoneRule) ? node.keystoneRule : info.keystoneRule.Trim();
                    r.abilityName = string.IsNullOrWhiteSpace(info.abilityName) ? node.abilityName : info.abilityName.Trim();
                    break;
                }

                case WebNodeType.Mastery:
                {
                    var opts = (info.options ?? new List<string>()).Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
                    // Top up from the offline options if the AI returned fewer than three.
                    foreach (var o in node.masteryOptions ?? new List<string>())
                        if (opts.Count < 3 && !opts.Contains(o)) opts.Add(o);
                    r.masteryOptions = opts.Take(3).ToList();
                    r.traits = new List<string>();
                    break;
                }
            }
            return r;
        }

        static List<string> OneTrait(List<string> traits, WebNode node)
        {
            if (traits.Count > 0) return new List<string> { traits[0] };
            return node.traits.Count > 0 ? new List<string> { node.traits[0] } : new List<string>();
        }

        static float Get(Dictionary<string, float> d, string key, float fallback) =>
            d.TryGetValue(key, out float v) ? v : fallback;

        private static float ParseNum(object value)
        {
            try
            {
                if (value is double d) return (float)d;
                if (value is long l) return l;
                if (value is int i) return i;
                if (value is float f) return f;
                if (value is string str)
                {
                    string clean = new string(str.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
                    if (float.TryParse(clean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r)) return r;
                }
            }
            catch { }
            return 0f;
        }
    }
}
