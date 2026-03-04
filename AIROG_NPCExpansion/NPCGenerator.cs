using System;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AIROG_NPCExpansion
{
    public static class NPCGenerator
    {
        public static async Task<bool> GenerateLore(GameCharacter npc, string context)
        {
            Debug.Log($"[AIROG_NPCExpansion] Starting real AI generation for {npc.GetPrettyName()}...");

            // Truncate context to avoid hitting token limits
            if (context.Length > 2000) context = context.Substring(context.Length - 2000);

            // Match loading logic early to get instructions
            NPCData existingData = NPCData.Load(npc.uuid);
            if (existingData == null) existingData = NPCData.CreateDefault(npc.GetPrettyName());

            string prompt = ConstructPrompt(npc, existingData, context);
            
            try 
            {
                // Call the game's AI engine
                string generatedText = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER, 
                    prompt, 
                    AIAsker.ChatGptPostprocessingType.NONE
                );

                Debug.Log($"[AIROG_NPCExpansion] Raw AI Response: {generatedText}");

                // Match loading logic
                // NPCData existingData = NPCData.Load(npc.uuid); // Already loaded above

                // Parse the response, updating existing data if available
                NPCData data = ParseAIResponse(generatedText, npc, existingData);
                
                // Save the data
                NPCData.Save(npc.uuid, data);
                 
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Generation failed: {ex}");
                return false;
            }
        }

        public static async Task<bool> UpdateScenario(GameCharacter npc, NPCData data, string context)
        {
            try
            {
                // Truncate context for scenario updates too
                // Truncate context for scenario updates but keep enough for local history
                if (context.Length > 3000) context = context.Substring(context.Length - 3000);

                string playerName = npc.manager?.playerCharacter?.pcGameEntity?.name ?? "the player";
                string prompt = $"You are a creative writer for a fantasy RPG. You are updating the situation for an NPC named '{npc.GetPrettyName()}'.\n" +
                                $"IMPORTANT: In the context below, 'You' refers to the player character, '{playerName}'. " +
                                $"The NPC '{npc.GetPrettyName()}' is a separate entity. Do NOT confuse the NPC with the player.\n\n" +
                                $"The NPC's current situation:\n{data.Scenario}\n\n" +
                                $"Recent world developments (from the perspective of {playerName}):\n{context}\n\n" +
                                $"Based on the world context, update the NPC '{npc.GetPrettyName()}'s current 'scenario' or 'situation' to reflect these developments. " +
                                $"Describe what the NPC is doing or where they are, considering the world changes. " +
                                $"If the NPC is nearby the player, they might be observing or reacting to the player's actions. " +
                                $"Keep it concise (1-3 sentences). Respond ONLY with the updated scenario text.";

                string updatedScenario = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE
                );

                Debug.Log($"[AIROG_NPCExpansion] UpdateScenario AI response for {npc.GetPrettyName()}: {updatedScenario}");

                if (!string.IsNullOrEmpty(updatedScenario))
                {
                    data.Scenario = updatedScenario.Trim();
                    NPCData.Save(npc.uuid, data);

                    // Log situation update to game chat so players see NPC reactions without opening profiles
                    var gameLog = npc.manager?.gameLogView;
                    if (gameLog != null)
                        _ = gameLog.LogText(GameLogView.AiDecision($"[{npc.GetPrettyName()}] {data.Scenario}"));

                    // Notify UI if open
                    if (NPCExamineUI.Instance != null)
                    {
                        NPCExamineUI.Instance.RefreshIfNpc(npc.uuid);
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Scenario update failed for {npc.GetPrettyName()}: {ex}");
            }
            return false;
        }

        private static string ConstructPrompt(GameCharacter npc, NPCData data, string context)
        {
            string playerName = npc.manager?.playerCharacter?.pcGameEntity?.name ?? "the player";
            
            string extraInstructions = "";
            if (!string.IsNullOrEmpty(data.GenerationInstructions))
            {
                extraInstructions = $"\nIMPORTANT USER INSTRUCTION: {data.GenerationInstructions}\n" +
                                    $"Follow the above instruction STRICTLY when generating stats, skills, and background. It overrides conflicting implied traits from the name.";
            }

            string prompt = $"You are a creative writer for a fantasy RPG. " +
                   $"Identify this NPC: '{npc.GetPrettyName()}'. Description: '{npc.description}'. " +
                   $"{extraInstructions}" +
                   $"\n\nIMPORTANT: In the context below, 'You' refers to the player character, '{playerName}'. " +
                   $"The NPC '{npc.GetPrettyName()}' is a separate entity. Do NOT confuse the NPC with the player.\n\n" +
                   $"Context (from the perspective of {playerName}): {context}\n\n" +
                   $"System Instruction: Respond ONLY with a valid JSON object. No extra text. " +
                   $"Fields (IN ORDER):\n" +
                   $"- \"attributes\": (JSON object) {{\"Strength\": 10-50, \"Dexterity\": 10-50, \"Intellect\": 10-50, \"Cunning\": 10-50, \"Charisma\": 10-50}}. Assign values based on their description (e.g., a warrior has high Strength).\n" +
                   $"- \"tags\": (array) 2-4 strings describing NPC nature (e.g., beast, humanoid, undead, holy, mechanical).\n" +
                   $"- \"interaction_traits\": (array) 2-3 strings describing interaction style (e.g., pious, curious, violent, timid, greedy).\n" +
                   $"- \"first_message\": (string) Their first greeting.\n" +
                   $"- \"scenario\": (string) Their current situation.\n" +
                   $"- \"personality\": (string) Their personality.\n" +
                   $"- \"example_dialogue\": (string) 2-3 lines of dialogue.\n" +
                   $"- \"system_prompt\": (string) AI character instructions.\n" +
                   $"- \"creator_notes\": (string) Meta notes.\n" +
                   $"- \"alternate_greetings\": (array) 2 other greetings.\n" +
                   $"- \"skills\": (object) 3-5 relevant skill names and their levels (1-10).\n" +
                   $"- \"abilities\": (array) 2-4 unique abilities. Each item should be an object with \"name\" and \"description\" (briefly explain what it does). Example: {{\"name\": \"Fireball\", \"description\": \"Hurls a ball of fire.\"}}\n" +
                   $"- \"current_goal\": (string) A short-term goal for the NPC (e.g., 'Find food', 'Protect the village').";
            
            return prompt;
        }

        private static NPCData ParseAIResponse(string jsonResponse, GameCharacter npc, NPCData data = null)
        {
            if (data == null) data = NPCData.CreateDefault(npc.GetPrettyName());
            
            // Preserve instructions if this was a fresh object or overwrite happened
            string instructions = data.GenerationInstructions; 
            
            data.Description = npc.description;

            // First, try full JSON parsing
            try
            {
                int startIndex = jsonResponse.IndexOf('{');
                int endIndex = jsonResponse.LastIndexOf('}');

                if (startIndex != -1 && endIndex != -1)
                {
                    string jsonString = jsonResponse.Substring(startIndex, endIndex - startIndex + 1);
                    JObject json = JObject.Parse(jsonString);

                    if (json.ContainsKey("first_message")) data.FirstMessage = json["first_message"].ToString();
                    if (json.ContainsKey("scenario")) data.Scenario = json["scenario"].ToString();
                    if (json.ContainsKey("personality")) data.Personality = json["personality"].ToString();
                    if (json.ContainsKey("example_dialogue")) data.MessageExamples = json["example_dialogue"].ToString();
                    if (json.ContainsKey("system_prompt")) data.SystemPrompt = json["system_prompt"].ToString();
                    if (json.ContainsKey("creator_notes")) data.CreatorNotes = json["creator_notes"].ToString();
                    
                    if (json.ContainsKey("alternate_greetings") && json["alternate_greetings"].Type == JTokenType.Array)
                    {
                        data.AlternateGreetings = json["alternate_greetings"].ToObject<System.Collections.Generic.List<string>>();
                    }

                    if (json.ContainsKey("attributes") && json["attributes"].Type == JTokenType.Object)
                    {
                        var attrs = json["attributes"].ToObject<System.Collections.Generic.Dictionary<string, long>>();
                        foreach (var kvp in attrs)
                        {
                            if (Enum.TryParse<SS.PlayerAttribute>(kvp.Key, true, out var attr))
                            {
                                data.Attributes[attr] = kvp.Value;
                            }
                        }
                    }

                    if (json.ContainsKey("skills") && json["skills"].Type == JTokenType.Object)
                    {
                        var skills = json["skills"].ToObject<System.Collections.Generic.Dictionary<string, int>>();
                        foreach (var kvp in skills)
                        {
                            data.Skills[kvp.Key] = new PlayerSkill(kvp.Key, kvp.Value);
                        }
                    }

                    if (json.ContainsKey("abilities") && json["abilities"].Type == JTokenType.Array)
                    {
                        // Handle both simple strings and objects
                        data.DetailedAbilities.Clear();
                        foreach (var token in json["abilities"])
                        {
                            if (token.Type == JTokenType.String)
                            {
                                data.DetailedAbilities.Add(new NPCData.AbilityData(token.ToString(), "No description provided."));
                            }
                            else if (token.Type == JTokenType.Object)
                            {
                                string name = token["name"]?.ToString() ?? "Unknown";
                                string desc = token["description"]?.ToString() ?? "No description provided.";
                                data.DetailedAbilities.Add(new NPCData.AbilityData(name, desc));
                            }
                        }
                    }

                    if (json.ContainsKey("current_goal"))
                    {
                        string newGoal = json["current_goal"].ToString();
                        if (newGoal != data.CurrentGoal)
                        {
                            // Goal changed — clear stale thoughts so old "Thinking about goal: (starting)." entries don't persist
                            data.RecentThoughts?.Clear();
                        }
                        data.CurrentGoal = newGoal;
                    }

                    if (json.ContainsKey("tags") && json["tags"].Type == JTokenType.Array)
                    {
                        data.Tags = json["tags"].ToObject<System.Collections.Generic.List<string>>();
                    }

                    if (json.ContainsKey("interaction_traits") && json["interaction_traits"].Type == JTokenType.Array)
                    {
                        data.InteractionTraits = json["interaction_traits"].ToObject<System.Collections.Generic.List<string>>();
                    }

                    return data; 
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] JObject.Parse failed ({ex.Message}). Attempting regex salvage...");
            }

            // Fallback: Regex salvage for truncated JSON
            string s_first = TryExtractField(jsonResponse, "first_message");
            string s_scen = TryExtractField(jsonResponse, "scenario");
            string s_pers = TryExtractField(jsonResponse, "personality");
            string s_ex = TryExtractField(jsonResponse, "example_dialogue");
            string s_sys = TryExtractField(jsonResponse, "system_prompt");
            
            if (s_first != null) data.FirstMessage = s_first;
            if (s_scen != null) data.Scenario = s_scen;
            if (s_pers != null) data.Personality = s_pers;
            if (s_ex != null) data.MessageExamples = s_ex;
            if (s_sys != null) data.SystemPrompt = s_sys;

            string s_goal = TryExtractField(jsonResponse, "current_goal");
            if (s_goal != null) data.CurrentGoal = s_goal;

            // Attribute Regex Salvage
            foreach (SS.PlayerAttribute attr in Enum.GetValues(typeof(SS.PlayerAttribute)))
            {
                string sAttr = TryExtractField(jsonResponse, attr.ToString());
                if (sAttr != null && long.TryParse(sAttr, out long val))
                {
                    data.Attributes[attr] = val;
                }
            }

            // Ability Regex Salvage (Basic)
            // This is rudimentary and might catch simple lists. 
            // Improving this would require robust array parsing via regex which is hard.
            // For now, if JSON fails, we might lose detailed abilities.
            // ...

            Debug.Log($"[AIROG_NPCExpansion] Regex Salvage Results - Greeting: {s_first != null}, Scenario: {s_scen != null}, Personality: {s_pers != null}");

            data.GenerationInstructions = instructions;
            return data;
        }

        private static string TryExtractField(string input, string fieldName)
        {
            try
            {
                // Look for "fieldName": "contents" (string)
                var pattern = $"\"{fieldName}\"\\s*:\\s*\"(.*?)(?<!\\\\)\"";
                var match = Regex.Match(input, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", "\n");
                
                // Look for "fieldName": 123 (number)
                pattern = $"\"{fieldName}\"\\s*:\\s*(\\d+)";
                match = Regex.Match(input, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value;

                // Fallback for truncated content (no closing quote)
                pattern = $"\"{fieldName}\"\\s*:\\s*\"(.*?)$";
                match = Regex.Match(input, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim().TrimEnd(',', '}', ' ', '"');
            }
            catch {}
            return null;
        }
    }
}
