using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    /// <summary>AI-powered generation of skill nodes, trees, and frontier expansion.</summary>
    public static class SkillWebGenerator
    {
        // ── Data classes for AI JSON responses ─────────────────────────────────

        public class NodeInfo
        {
            public string name;
            public string description;
            public string treeName;
            public string treePurpose;
            public Dictionary<string, object> stats;
            public List<string> traits;
        }

        // ── Context helpers ─────────────────────────────────────────────────────

        static string WorldContext(GameplayManager manager)
        {
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

        static string PlayerContext(GameplayManager manager)
        {
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

        // ── Node building ───────────────────────────────────────────────────────

        static float ParseStatValue(object value)
        {
            try
            {
                if (value is double d) return (float)d;
                if (value is float  f) return f;
                if (value is int    i) return i;
                if (value is long   l) return l;
                if (value is string s)
                {
                    string clean = Regex.Replace(s, @"[^0-9.\-]", "");
                    if (float.TryParse(clean, out float r)) return r;
                }
            }
            catch { }
            return 0f;
        }

        static SkillNode BuildNode(NodeInfo info, Vector2 position, string treeId = null, NodeType nodeType = NodeType.Basic)
        {
            var node = new SkillNode(Guid.NewGuid().ToString(), info.name, info.description, position)
            {
                treeId     = treeId,
                isUnlocked = false,
                tier       = 0,
                nodeType   = nodeType
            };
            if (info.stats != null)
                foreach (var kvp in info.stats)
                    node.statModifiers[kvp.Key] = ParseStatValue(kvp.Value);
            if (info.traits != null)
                node.narrativeTraits.AddRange(info.traits);
            return node;
        }

        static NodeInfo ExtractSingleNodeInfo(string response)
        {
            try
            {
                int s = response.IndexOf('{');
                int e = response.LastIndexOf('}');
                if (s == -1 || e == -1) return null;
                return JsonConvert.DeserializeObject<NodeInfo>(response.Substring(s, e - s + 1));
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] JSON parse error: " + ex.Message + "\nResponse: " + response);
                return null;
            }
        }

        // ── Public generation API ───────────────────────────────────────────────

        /// <summary>
        /// Generates a single locked skill node branching from <paramref name="parent"/>.
        /// Optionally constrained to a specific discipline tree.
        /// </summary>
        public static async Task<SkillNode> GenerateLoreBasedNode(
            GameplayManager manager, SkillNode parent, Vector2 position, SkillTree tree = null,
            NodeType nodeType = NodeType.Notable)
        {
            string worldCtx  = WorldContext(manager);
            string playerCtx = PlayerContext(manager);

            string disciplineCtx = tree != null
                ? "\nDiscipline: " + tree.name + "\nDiscipline Theme: " + tree.purpose +
                  "\nThis node MUST fit thematically within this discipline."
                : "";

            string parentCtx = parent != null
                ? "\nBranching from: " + parent.name + " - " + parent.description +
                  "\nThis skill may be an evolution or complement of the parent."
                : "";

            string typeGuidance = nodeType == NodeType.Notable
                ? "- This is a NOTABLE node: meaningful, named passive with moderate power.\n" +
                  "- Stats: values 5-12. Maximum 2 stats and 2 traits.\n"
                : "- This is a BASIC node: a small stepping-stone passive.\n" +
                  "- Stats: values 3-8. Maximum 1 stat and 1 trait.\n";

            string prompt = "You are designing a node for a lore-driven Path-of-Exile-style skill web.\n\n" +
                "=== WORLD ===\n" + worldCtx + "\n\n" +
                "=== PLAYER ===\n" + playerCtx + disciplineCtx + parentCtx + "\n\n" +
                "=== RULES ===\n" +
                "- Name: 2-5 evocative words\n" +
                "- Description: 1-2 sentences, grounded in the world's lore\n" +
                "- Stats: use ONLY these keys: Strength, Dexterity, Intellect, Cunning, Charisma.\n" +
                typeGuidance +
                "- Traits: non-numerical capabilities such as Water Breathing, Passive Regeneration, Improved Tracking\n\n" +
                "Respond ONLY with valid JSON, no prose:\n" +
                "{\n" +
                "  \"name\": \"\",\n" +
                "  \"description\": \"\",\n" +
                "  \"stats\": { \"StatName\": 5 },\n" +
                "  \"traits\": [\"Trait\"]\n" +
                "}";

            try
            {
                string response = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE,
                    false, false, null, false, true,
                    AIAsker.ModelOverrideMode.GOOD_FOR_CORRECTNESS,
                    true);

                var info = ExtractSingleNodeInfo(response);
                if (info == null) return null;

                var node = BuildNode(info, position, tree?.id, nodeType);
                await GenerateImageForNode(manager, node);
                return node;
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] GenerateLoreBasedNode failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Generates the initial cluster of 3 discipline-root nodes arranged in a triangle.
        /// All nodes are returned LOCKED — the player must unlock one to begin.
        /// </summary>
        public static async Task<(List<SkillNode> nodes, List<SkillTree> trees)> GenerateStartingCluster(
            GameplayManager manager)
        {
            string worldCtx  = WorldContext(manager);
            string playerCtx = PlayerContext(manager);

            string prompt =
                "You are designing the starting Skill Web for a lore-driven roguelite character.\n\n" +
                "=== WORLD ===\n" + worldCtx + "\n\n" +
                "=== PLAYER ===\n" + playerCtx + "\n\n" +
                "=== TASK ===\n" +
                "Create exactly 3 DISTINCT starting nodes, each anchoring a different discipline:\n" +
                "e.g. Combat, Magic, Social/Cunning, or based on the character's race or background.\n\n" +
                "Rules:\n" +
                "- Stats: use ONLY Strength, Dexterity, Intellect, Cunning, Charisma. Values 3-8 for starting nodes.\n" +
                "- Traits: 0 or 1 per node.\n" +
                "- treeName: 2-4 words, evocative\n" +
                "- treePurpose: 1 sentence about the discipline's focus\n\n" +
                "Respond ONLY with a JSON array of exactly 3 objects:\n" +
                "[\n" +
                "  {\n" +
                "    \"name\": \"\",\n" +
                "    \"description\": \"\",\n" +
                "    \"stats\": { \"StatName\": 5 },\n" +
                "    \"traits\": [],\n" +
                "    \"treeName\": \"\",\n" +
                "    \"treePurpose\": \"\"\n" +
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
                if (s == -1 || e == -1) return (null, null);

                var infos = JsonConvert.DeserializeObject<List<NodeInfo>>(response.Substring(s, e - s + 1));
                if (infos == null || infos.Count == 0) return (null, null);

                // Triangle layout around origin
                Vector2[] positions =
                {
                    new Vector2(   0, -300),
                    new Vector2(-260,  170),
                    new Vector2( 260,  170)
                };

                string[] colors = { "#E8734A", "#4A9BE8", "#7AE84A", "#E8D44A", "#C44AE8", "#4AE8C8" };

                var newNodes = new List<SkillNode>();
                var newTrees = new List<SkillTree>();

                for (int i = 0; i < Math.Min(infos.Count, 3); i++)
                {
                    var info = infos[i];
                    var t = new SkillTree(
                        Guid.NewGuid().ToString(),
                        info.treeName ?? "Discipline",
                        info.treePurpose ?? "")
                    {
                        colorHex = colors[i % colors.Length]
                    };
                    newTrees.Add(t);

                    var node = BuildNode(info, positions[i], t.id, NodeType.Notable);
                    await GenerateImageForNode(manager, node);
                    newNodes.Add(node);
                }

                return (newNodes, newTrees);
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] GenerateStartingCluster failed: " + ex.Message);
                return (null, null);
            }
        }

        /// <summary>
        /// Auto-generates locked frontier nodes around a newly-unlocked node,
        /// adds them to <paramref name="data"/>, and connects them to <paramref name="origin"/>.
        /// </summary>
        public static async Task<List<SkillNode>> GenerateFrontierNodes(
            GameplayManager manager, SkillNode origin, SkillWebData data, SkillTree tree, int count = 2)
        {
            var result    = new List<SkillNode>();
            var positions = FindFrontierPositions(origin, data, count);

            foreach (var pos in positions)
            {
                var node = await GenerateLoreBasedNode(manager, origin, pos, tree, NodeType.Basic);
                if (node == null) continue;
                if (data.TryAddNode(node))
                {
                    data.AddConnection(origin.id, node.id);
                    result.Add(node);
                }
            }
            return result;
        }

        /// <summary>
        /// Generates a Keystone node: a paradigm-shifting passive with power and a tradeoff.
        /// </summary>
        public static async Task<SkillNode> GenerateKeystoneNode(
            GameplayManager manager, SkillNode parent, Vector2 position, SkillTree tree = null)
        {
            string worldCtx  = WorldContext(manager);
            string playerCtx = PlayerContext(manager);

            string disciplineCtx = tree != null
                ? "\nDiscipline: " + tree.name + "\nDiscipline Theme: " + tree.purpose +
                  "\nThis keystone MUST fit thematically within this discipline."
                : "";

            string parentCtx = parent != null
                ? "\nBranching from: " + parent.name + " - " + parent.description
                : "";

            string prompt = "You are designing a KEYSTONE node for a lore-driven Path-of-Exile-style skill web.\n\n" +
                "=== WORLD ===\n" + worldCtx + "\n\n" +
                "=== PLAYER ===\n" + playerCtx + disciplineCtx + parentCtx + "\n\n" +
                "=== RULES ===\n" +
                "- Keystones are LEGENDARY, paradigm-shifting passives that fundamentally alter the character.\n" +
                "- Name: 2-4 powerful, iconic words (feel legendary and unique)\n" +
                "- Description: 2-3 sentences. Describe a transformative ability AND a meaningful cost or limitation.\n" +
                "- Stats: 1-2 from [Strength, Dexterity, Intellect, Cunning, Charisma]. Values 12-20.\n" +
                "- Traits: 1-2 powerful narrative capabilities. One MAY be a drawback (e.g. 'Cannot Heal Naturally').\n" +
                "- Maximum 2 stats and 2 traits\n\n" +
                "Respond ONLY with valid JSON, no prose:\n" +
                "{\n" +
                "  \"name\": \"\",\n" +
                "  \"description\": \"\",\n" +
                "  \"stats\": { \"StatName\": 15 },\n" +
                "  \"traits\": [\"Powerful Trait\", \"Drawback Trait\"]\n" +
                "}";

            try
            {
                string response = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE,
                    false, false, null, false, true,
                    AIAsker.ModelOverrideMode.GOOD_FOR_CORRECTNESS,
                    true);

                var info = ExtractSingleNodeInfo(response);
                if (info == null) return null;

                var node = BuildNode(info, position, tree?.id, NodeType.Keystone);
                node.isMilestone = true;
                await GenerateImageForNode(manager, node);
                return node;
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] GenerateKeystoneNode failed: " + ex.Message);
                return null;
            }
        }

        // ── Image generation ────────────────────────────────────────────────────

        /// <summary>Generates a sprite icon for a node that has no image yet.</summary>
        public static async Task GenerateImageForNode(GameplayManager manager, SkillNode node)
        {
            if (!string.IsNullOrEmpty(node.imageUuid)) return;
            try
            {
                var ge  = new ThingGameEntity(null, node.name, manager, false, true);
                ge.uuid = Guid.NewGuid().ToString();
                ge.SetDescription(node.description);
                if (ge.spGenInfo == null)
                    ge.spGenInfo = new GameEntity.ImgGenInfo(GameEntity.ImgType.SPRITE);

                var imgSettings = new SettingsPojo.EntImgSettings(
                    512, 512, 28,
                    "${prompt}",
                    "blurry, watermarked, low quality, text, letters, words",
                    true);

                await AIAsker.getGeneratedSprite(imgSettings, ge, true);
                node.imageUuid = ge.uuid;
                ge.TearDown();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] Image generation failed for '" + node.name + "': " + ex.Message);
            }
        }

        // ── Spatial helpers ─────────────────────────────────────────────────────

        static List<Vector2> FindFrontierPositions(SkillNode origin, SkillWebData data, int count)
        {
            var positions = new List<Vector2>();

            // Try preferred cardinal/diagonal angles with small jitter first
            float[] angles = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };
            foreach (float baseDeg in angles)
            {
                if (positions.Count >= count) break;
                float rad  = (baseDeg + UnityEngine.Random.Range(-20f, 20f)) * Mathf.Deg2Rad;
                float dist = UnityEngine.Random.Range(240f, 330f);
                var   pos  = origin.position + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * dist;
                if (!data.CheckCollision(pos)) positions.Add(pos);
            }

            // Random fallback
            for (int i = 0; positions.Count < count && i < 40; i++)
            {
                float rad  = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist = UnityEngine.Random.Range(220f, 360f);
                var   pos  = origin.position + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * dist;
                if (!data.CheckCollision(pos)) positions.Add(pos);
            }

            return positions;
        }
    }
}
