using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    public static class NodeGenAI
    {
        private static readonly string[] Attributes = { "Strength", "Dexterity", "Intellect", "Cunning", "Charisma" };

        public class AINodeInfo
        {
            public string id;
            public string name;
            public string description;
            public Dictionary<string, object> stats;
            public List<string> traits;
            public string keystoneRule;
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

        // ── Main Generation Entrypoint ──────────────────────────────────────────

        /// <summary>
        /// Asynchronously generates a batch of WebNodes for a given sector and ring using the AI.
        /// Falls back to offline lexicon generation if the AI fails.
        /// </summary>
        public static async Task<List<WebNode>> GenerateRingBatchAsync(
            GameplayManager manager, 
            WebSector sector, 
            int ring, 
            List<(string id, WebNodeType type)> targets, 
            List<WebNode> existingSectorNodes,
            long layoutSeed)
        {
            if (manager == null || sector == null || targets == null || targets.Count == 0)
            {
                return new List<WebNode>();
            }

            string worldCtx = WorldContext(manager);
            string playerCtx = PlayerContext(manager);
            string primaryAttr = NodeGenOffline.GetSectorAttribute(sector);

            // Continuity context from existing nodes
            string continuityCtx = "";
            if (existingSectorNodes != null && existingSectorNodes.Count > 0)
            {
                var recent = existingSectorNodes.FindAll(n => n.ring == ring - 1).Take(5).ToList();
                if (recent.Count > 0)
                {
                    continuityCtx = "\nContinuity (stars in the previous ring): " + 
                        string.Join(", ", recent.Select(n => $"{n.name} ({n.description})"));
                }
            }

            // Recipe instructions
            string recipe = "";
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                recipe += $"- Star index {i}: ID = \"{t.id}\", Type = {t.type}\n";
                if (t.type == WebNodeType.Basic)
                {
                    recipe += "  * Basic budget: Exactly 1 positive attribute bonus (value +2 to +4). No traits.\n";
                }
                else if (t.type == WebNodeType.Notable)
                {
                    recipe += $"  * Notable budget: Total positive attributes bonus +6 to +10 (divided across 1 or 2 attributes matching the sector's affinity: {primaryAttr}). Exactly 1 non-numerical narrative trait.\n";
                }
                else if (t.type == WebNodeType.Keystone)
                {
                    recipe += $"  * Keystone budget: Exactly 1 positive attribute bonus ({primaryAttr}, value +10 to +14) AND exactly 1 negative attribute penalty (value -4 to -6). Exactly 1 narrative trait. Exactly 1 keystoneRule (a single gameplay directive text/rule sentence).\n";
                }
            }

            string prompt = 
                "You are designing a batch of stars for a lore-driven RPG skill constellation web.\n\n" +
                "=== WORLD ===\n" + worldCtx + "\n\n" +
                "=== PLAYER ===\n" + playerCtx + "\n\n" +
                "=== DISCIPLINE / SECTOR ===\n" +
                $"Name: {sector.name}\n" +
                $"Theme: {sector.purpose}\n" +
                $"Primary Attribute: {primaryAttr}\n" +
                continuityCtx + "\n\n" +
                "=== RECIPE ===\n" +
                "Create exactly the following stars matching their IDs, Types, and Budgets:\n" +
                recipe + "\n" +
                "=== RULES ===\n" +
                "- Names: 2-4 evocative, lore-grounded words.\n" +
                "- Descriptions: 1-2 lore-grounded sentences.\n" +
                "- Stats: ONLY use these keys: Strength, Dexterity, Intellect, Cunning, Charisma.\n" +
                "- Traits: Non-numerical capabilities (e.g. 'Silent Stride', 'Flame Touch').\n\n" +
                "Respond ONLY with a valid JSON array of objects matching the size of the recipe, no prose:\n" +
                "[\n" +
                "  {\n" +
                "    \"id\": \"target-node-id\",\n" +
                "    \"name\": \"Star Name\",\n" +
                "    \"description\": \"Lore description.\",\n" +
                "    \"stats\": { \"Strength\": 3 },\n" +
                "    \"traits\": [\"Trait\"],\n" +
                "    \"keystoneRule\": null\n" +
                "  }\n" +
                "]";

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
                if (s == -1 || e == -1) return null;

                var rawInfos = JsonConvert.DeserializeObject<List<AINodeInfo>>(response.Substring(s, e - s + 1));
                if (rawInfos == null || rawInfos.Count == 0) return null;

                var resultNodes = new List<WebNode>();
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    var info = rawInfos.Find(infoObj => infoObj.id == target.id);
                    
                    // Fallback to match by index if ID matching failed
                    if (info == null && i < rawInfos.Count)
                    {
                        info = rawInfos[i];
                    }

                    if (info == null)
                    {
                        // Fallback generator for this missing node
                        var fallbackNode = NodeGenOffline.GenerateNode(target.id, target.type, sector, ring, i, layoutSeed);
                        resultNodes.Add(fallbackNode);
                        continue;
                    }

                    var node = new WebNode
                    {
                        id = target.id,
                        type = target.type,
                        sectorId = sector.id,
                        ring = ring,
                        name = string.IsNullOrEmpty(info.name) ? "Unnamed Star" : info.name,
                        description = string.IsNullOrEmpty(info.description) ? "A mysterious star in the night sky." : info.description,
                        keystoneRule = info.keystoneRule,
                        unlocked = false,
                        tier = (target.type == WebNodeType.Basic || target.type == WebNodeType.Notable || target.type == WebNodeType.Confluence) ? 1 : 0,
                        aiRefined = true
                    };

                    // Populate stats
                    if (info.stats != null)
                    {
                        foreach (var kvp in info.stats)
                        {
                            string attrName = Attributes.FirstOrDefault(a => string.Equals(a, kvp.Key, StringComparison.OrdinalIgnoreCase));
                            if (attrName == null) continue;
                            float val = ParseNum(kvp.Value);
                            if (val != 0f) node.stats[attrName] = val;
                        }
                    }

                    // Populate traits
                    if (info.traits != null)
                    {
                        foreach (var trait in info.traits)
                        {
                            if (!string.IsNullOrEmpty(trait)) node.traits.Add(trait);
                        }
                    }

                    // Budget constraints checks (Clamping)
                    ClampNodeStats(node, primaryAttr);

                    resultNodes.Add(node);
                }

                return resultNodes;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkillWeb] AI batch generation failed for {sector.name} Ring {ring}: {ex.Message}. Falling back to offline lexicon.");
                return null;
            }
        }

        // ── Validation and Clamping ─────────────────────────────────────────────

        private static void ClampNodeStats(WebNode node, string primaryAttr)
        {
            if (node.stats.Count == 0)
            {
                node.stats[primaryAttr] = (node.type == WebNodeType.Basic) ? 3 : (node.type == WebNodeType.Notable ? 6 : 12);
            }

            switch (node.type)
            {
                case WebNodeType.Basic:
                    // Basic budget: Exactly 1 attribute, clamped +2..+4
                    string basicAttr = node.stats.Keys.FirstOrDefault() ?? primaryAttr;
                    float val = node.stats[basicAttr];
                    node.stats.Clear();
                    node.stats[basicAttr] = Mathf.Clamp(val, 2f, 4f);
                    node.traits.Clear();
                    node.keystoneRule = null;
                    break;

                case WebNodeType.Notable:
                case WebNodeType.Confluence:
                    // Notable budget: <= 2 attributes, total value +6..+10, exactly 1 trait
                    var notableKeys = node.stats.Keys.Take(2).ToList();
                    float sum = notableKeys.Sum(k => Mathf.Max(0f, node.stats[k]));
                    if (sum < 6f || sum > 10f)
                    {
                        float scale = sum > 0 ? Mathf.Clamp(8f / sum, 0.5f, 2f) : 1f;
                        foreach (var k in notableKeys)
                        {
                            node.stats[k] = Mathf.Round(Mathf.Clamp(node.stats[k] * scale, 2f, 8f));
                        }
                    }
                    var cleanStats = new Dictionary<string, float>();
                    foreach (var k in notableKeys)
                    {
                        if (node.stats[k] > 0) cleanStats[k] = node.stats[k];
                    }
                    if (cleanStats.Count == 0) cleanStats[primaryAttr] = 6f;
                    node.stats = cleanStats;

                    // Ensure exactly 1 trait
                    if (node.traits.Count == 0)
                    {
                        string[] traits = ThemeLexicon.Traits[primaryAttr];
                        node.traits.Add(traits[new System.Random(node.id.GetHashCode()).Next(traits.Length)]);
                    }
                    else if (node.traits.Count > 1)
                    {
                        node.traits = new List<string> { node.traits[0] };
                    }
                    node.keystoneRule = null;
                    break;

                case WebNodeType.Keystone:
                    // Keystone budget: 1 positive (+10..+14), 1 negative (-4..-6), 1 trait, 1 rule
                    string posKey = node.stats.Keys.FirstOrDefault(k => node.stats[k] > 0) ?? primaryAttr;
                    string negKey = node.stats.Keys.FirstOrDefault(k => node.stats[k] < 0 && k != posKey);
                    if (negKey == null)
                    {
                        negKey = Attributes.FirstOrDefault(k => k != posKey) ?? "Dexterity";
                    }

                    float posVal = Mathf.Clamp(node.stats.ContainsKey(posKey) ? node.stats[posKey] : 12f, 10f, 14f);
                    float negVal = -Mathf.Clamp(node.stats.ContainsKey(negKey) ? Mathf.Abs(node.stats[negKey]) : 5f, 4f, 6f);

                    node.stats.Clear();
                    node.stats[posKey] = posVal;
                    node.stats[negKey] = negVal;

                    if (node.traits.Count == 0)
                    {
                        string[] traits = ThemeLexicon.Traits[primaryAttr];
                        node.traits.Add(traits[new System.Random(node.id.GetHashCode()).Next(traits.Length)]);
                    }
                    else if (node.traits.Count > 1)
                    {
                        node.traits = new List<string> { node.traits[0] };
                    }

                    if (string.IsNullOrEmpty(node.keystoneRule))
                    {
                        node.keystoneRule = ThemeLexicon.GenericKeystoneRules[new System.Random(node.id.GetHashCode()).Next(ThemeLexicon.GenericKeystoneRules.Length)];
                    }
                    break;
            }
        }

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
                    if (float.TryParse(clean, out float r)) return r;
                }
            }
            catch { }
            return 0f;
        }
    }
}
