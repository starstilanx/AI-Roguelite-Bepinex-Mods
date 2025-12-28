using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    public class SkillWebGenerator
    {
        public class NodeGeneratedInfo
        {
            public string name;
            public string description;
            public string treeName;
            public string treePurpose;
            public Dictionary<string, object> stats;
            public List<string> traits; // Non-numerical bonuses
        }

        public static async Task<SkillNode> GenerateLoreBasedNode(GameplayManager manager, SkillNode parent, Vector2 position, SkillTree tree = null)
        {
            var universe = manager.GetCurrentUniverse();
            string universeName = universe?.GetPrettyName() ?? "Unknown";
            string universeDesc = universe?.GetPotentiallyNullDescription() ?? "";
            string worldBkgd = manager.worldBackgroundText ?? "";
            string currentPlace = manager.currentPlace?.GetPrettyName() ?? "Unknown";
            string placeDesc = manager.currentPlace?.GetPotentiallyNullDescription() ?? "";
            string playerName = manager.playerCharacter?.pcGameEntity?.playerName ?? "Player";
            int playerLevel = manager.playerCharacter?.playerLevel ?? 1;
            string playerBkgd = manager.playerBackgroundText ?? "";

            // Gather player traits/abilities (skills) for context
            string playerTraitsTxt = "";
            var pc = manager.playerCharacter;
            if (pc != null && pc.pcGameEntity != null && pc.pcGameEntity.skillsDict != null)
            {
                playerTraitsTxt = "Current Abilities/Skills:\n";
                foreach (var kvp in pc.pcGameEntity.skillsDict)
                {
                    playerTraitsTxt += $"- {kvp.Value.skillName} (Level {kvp.Value.level})\n";
                }
            }

            string disciplineContext = "";
            if (tree != null)
            {
                disciplineContext = $"\nDiscipline: {tree.name}\nTheme/Purpose: {tree.purpose}\nThis skill MUST belong to this specific discipline (could be a Class, Race, or Lore-based tree).";
            }

            string parentContext = parent != null ? $"\nParent Skill: {parent.name} ({parent.description})" : "";

            string prompt = $@"As an AI skill tree generator for a roguelite game, create a new skill node.
The new skill should be rooted in the character's background, current abilities, and the world's lore.

World Context:
Universe: {universeName}
Universe Lore: {universeDesc}
World Background: {worldBkgd}
Current Location: {currentPlace} ({placeDesc})

Player Context:
Name: {playerName}
Level: {playerLevel}
Background: {playerBkgd}
{playerTraitsTxt}
{disciplineContext}
{parentContext}

Instructions:
1. Provide a unique, atmospheric name for the new skill.
2. Provide a 1-2 sentence description explaining its lore-based effect.
3. Provide 1-2 gameplay stat modifiers (numeric values like 5 or 10.5).
4. Provide 'traits' if applicable. These are non-numerical upgrades (e.g. 'Enhanced Punches', 'Wall Climbing', 'Water Breathing', 'Passive Regeneration', 'Improved Tracking').
5. If branching from a parent, this could be an UPGRADE or EVOLUTION of that capability.

Output MUST be in the following JSON format:
{{
  ""name"": ""Skill Name"",
  ""description"": ""Skill Lore Description"",
  ""stats"": {{ ""StatName"": 5, ... }},
  ""traits"": [""Trait 1"", ""Trait 2""]
}}";

            try
            {
                Debug.Log("[SkillWeb] Requesting lore-based node from AI...");
                string response = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE,
                    false, false, null, false, true, 
                    AIAsker.ModelOverrideMode.GOOD_FOR_CORRECTNESS,
                    true
                );

                // Find JSON in response
                int start = response.IndexOf('{');
                int end = response.LastIndexOf('}');
                if (start != -1 && end != -1)
                {
                    string json = response.Substring(start, end - start + 1);
                    var info = JsonConvert.DeserializeObject<NodeGeneratedInfo>(json);

                    var newNode = new SkillNode(Guid.NewGuid().ToString(), info.name, info.description, position);
                    newNode.treeId = tree?.id;
                    if (info.stats != null)
                    {
                        foreach (var kvp in info.stats)
                        {
                            float value = 0f;
                            try {
                                if (kvp.Value is double d) value = (float)d;
                                else if (kvp.Value is float f) value = f;
                                else if (kvp.Value is int i) value = (float)i;
                                else if (kvp.Value is long l) value = (float)l;
                                else if (kvp.Value is string s)
                                {
                                    // Clean string: remove %, +, spaces
                                    string clean = System.Text.RegularExpressions.Regex.Replace(s, @"[^0-9\.\-]", "");
                                    if (float.TryParse(clean, out float res)) value = res;
                                }
                            } catch {}
                            
                            newNode.statModifiers[kvp.Key] = value;
                        }
                    }

                    if (info.traits != null)
                    {
                        newNode.narrativeTraits.AddRange(info.traits);
                    }

                    // Generate Image
                    try
                    {
                        Debug.Log($"[SkillWeb] Generating image for node: {newNode.name}");
                        
                        // GameEntity is abstract, use ThingGameEntity
                        var ge = new ThingGameEntity(null, newNode.name, manager, false, true); 
                        ge.uuid = Guid.NewGuid().ToString(); // New UUID for the image
                        ge.SetDescription(newNode.description);
                        
                        // Initialize ImgGenInfo if it's null (it likely is on a fresh component)
                        if (ge.spGenInfo == null)
                            ge.spGenInfo = new GameEntity.ImgGenInfo(GameEntity.ImgType.SPRITE);

                        // Create settings - Enable Background Removal
                        var imgSettings = new SettingsPojo.EntImgSettings(512, 512, 28, "${prompt}", "blurry, watermarked, low quality", true);

                        await AIAsker.getGeneratedSprite(imgSettings, ge, true);
                        
                        newNode.imageUuid = ge.uuid;
                        
                        // No need to destroy dummyObj since we didn't create one
                        // GameEntity might need manual cleanup if it subscribed to events
                        ge.TearDown(); 
                    }
                    catch (Exception imgEx)
                    {
                        Debug.LogError($"[SkillWeb] Image generation failed: {imgEx.Message}");
                    }

                    return newNode;
                }
                else
                {
                    Debug.LogError("[SkillWeb] AI response did not contain valid JSON: " + response);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] Error generating node: " + ex.Message);
            }

            return null;
        }

        public static async Task<(List<SkillNode> nodes, List<SkillTree> trees)> GenerateStartingCluster(GameplayManager manager, Vector2 center)
        {
            var universe = manager.GetCurrentUniverse();
            string universeName = universe?.GetPrettyName() ?? "Unknown";
            string universeDesc = universe?.GetPotentiallyNullDescription() ?? "";
            string worldBkgd = manager.worldBackgroundText ?? "";
            string currentPlace = manager.currentPlace?.GetPrettyName() ?? "Unknown";
            string placeDesc = manager.currentPlace?.GetPotentiallyNullDescription() ?? "";
            string playerName = manager.playerCharacter?.pcGameEntity?.playerName ?? "Player";

            string prompt = $@"As an AI skill tree generator for a roguelite game, create 3 distinct starting skill nodes for a new player character.
Each skill should be the root of a distinct 'Discipline' or 'Tree' rooted in the current world's lore.

World Context:
Universe: {universeName}
Universe Lore: {universeDesc}
World Background: {worldBkgd}
Current Location: {currentPlace} ({placeDesc})

Player Context:
Name: {playerName}

Instructions:
1. Provide 3 unique skills, each representing a DIFFERENT category of growth (e.g. Technique, Magic, Race-based, Background-based).
2. For each, provide a 'treeName' and a 'treePurpose' (a brief description of what this tree focuses on).
3. Provide atmospheric names and descriptions for the starting node itself.
4. Provide stat modifiers (numeric).
5. Provide narrative traits (non-numerical upgrades, e.g. 'Minor Regeneration', 'Night Vision').

Output MUST be a JSON ARRAY format:
[
  {{
    ""name"": ""Starting Skill"",
    ""description"": ""Description"",
    ""stats"": {{ ""StatName"": 5 }},
    ""traits"": [""Narrative Trait""],
    ""treeName"": ""Shadow Arts"",
    ""treePurpose"": ""Skills related to stealth, poison, and illusion.""
  }},
  ...
]";

            try
            {
                Debug.Log("[SkillWeb] Requesting starting cluster...");
                string response = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE,
                    false, false, null, false, true, 
                    AIAsker.ModelOverrideMode.GOOD_FOR_CORRECTNESS,
                    true
                );

                int start = response.IndexOf('[');
                int end = response.LastIndexOf(']');
                if (start != -1 && end != -1)
                {
                    string json = response.Substring(start, end - start + 1);
                    var infos = JsonConvert.DeserializeObject<List<NodeGeneratedInfo>>(json);
                    
                    var newNodes = new List<SkillNode>();
                    var newTrees = new List<SkillTree>();
                    
                    // Positions: Triangle around center
                    Vector2[] offsets = new Vector2[] {
                        new Vector2(0, -250),
                        new Vector2(-220, 150),
                        new Vector2(220, 150)
                    };

                    for (int i = 0; i < infos.Count && i < offsets.Length; i++)
                    {
                        var info = infos[i];
                        
                        // Create Tree
                        var tree = new SkillTree(Guid.NewGuid().ToString(), info.treeName ?? "New Discipline", info.treePurpose ?? "No purpose defined.");
                        newTrees.Add(tree);

                        var node = new SkillNode(Guid.NewGuid().ToString(), info.name, info.description, center + offsets[i]);
                        node.treeId = tree.id;
                        
                        // Parse stats
                        if (info.stats != null)
                        {
                            foreach (var kvp in info.stats)
                            {
                                float value = 0f;
                                try {
                                    if (kvp.Value is double d) value = (float)d;
                                    else if (kvp.Value is float f) value = f;
                                    else if (kvp.Value is int x) value = (float)x;
                                    else if (kvp.Value is long l) value = (float)l;
                                    else if (kvp.Value is string s)
                                    {
                                        string clean = System.Text.RegularExpressions.Regex.Replace(s, @"[^0-9\.\-]", "");
                                        if (float.TryParse(clean, out float res)) value = res;
                                    }
                                } catch {}
                                node.statModifiers[kvp.Key] = value;
                            }
                        }

                        if (info.traits != null)
                        {
                            node.narrativeTraits.AddRange(info.traits);
                        }

                        await GenerateImageForNode(manager, node); 
                        newNodes.Add(node);
                    }
                    
                    return (newNodes, newTrees);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] Error generating cluster: " + ex.Message);
            }
            return (null, null);
        }

        public static async Task GenerateImageForNode(GameplayManager manager, SkillNode node)
        {
            if (!string.IsNullOrEmpty(node.imageUuid)) return;

            try
            {
                Debug.Log($"[SkillWeb] Generating image for existing node: {node.name}");

                // GameEntity is abstract, use ThingGameEntity
                var ge = new ThingGameEntity(null, node.name, manager, false, true);
                ge.uuid = Guid.NewGuid().ToString(); // New UUID for the image
                ge.SetDescription(node.description);

                // Initialize ImgGenInfo if it's null (it likely is on a fresh component)
                if (ge.spGenInfo == null)
                    ge.spGenInfo = new GameEntity.ImgGenInfo(GameEntity.ImgType.SPRITE);

                // Create settings - Enable Background Removal
                var imgSettings = new SettingsPojo.EntImgSettings(512, 512, 28, "${prompt}", "blurry, watermarked, low quality", true);

                await AIAsker.getGeneratedSprite(imgSettings, ge, true);

                node.imageUuid = ge.uuid;

                ge.TearDown();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkillWeb] Image generation failed for existing node: {ex.Message}");
            }
        }
    }
}
