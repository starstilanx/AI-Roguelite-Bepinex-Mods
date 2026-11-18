using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AIROG_NPCExpansion
{
    public static class NPCGenerator
    {
        /// <param name="keepProfileText">
        /// True when the NPC already has a personality/background the caller did not ask to replace
        /// (the game just generated them, or the player only asked for extended stats): those two
        /// texts are then kept and only the rest of the profile is filled in.
        /// </param>
        public static async Task<bool> GenerateLore(GameCharacter npc, string context, bool keepProfileText = false)
        {
            Debug.Log($"[AIROG_NPCExpansion] Starting real AI generation for {npc.GetPrettyName()}...");

            // Truncate context to avoid hitting token limits
            context = context ?? "";
            if (context.Length > 2000) context = context.Substring(context.Length - 2000);

            // Match loading logic early to get instructions
            NPCData existingData = NPCData.Load(npc.uuid);
            if (existingData == null) existingData = NPCData.CreateDefault(npc.GetPrettyName());
            string keptPersonality = keepProfileText ? NPCData.GetPersonality(npc, existingData) : null;
            string keptBackground  = keepProfileText ? NPCData.GetBackground(npc, existingData) : null;

            string prompt = ConstructPrompt(npc, existingData, context, keptPersonality, keptBackground);

            try
            {
                // Call the game's AI engine
                string generatedText = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE
                );

                Debug.Log($"[AIROG_NPCExpansion] Raw AI Response: {generatedText}");

                // An empty reply used to "succeed" with nothing generated.
                if (string.IsNullOrWhiteSpace(generatedText)) return false;
                // The player left this save while the AI was thinking: this NPC object is no longer in the world.
                if (!existingData.IsFromCurrentSave) return false;

                // Parse the response, updating existing data if available
                NPCData data = ParseAIResponse(generatedText, npc, existingData);
                if (!string.IsNullOrEmpty(keptPersonality)) data.Personality = keptPersonality;
                if (!string.IsNullOrEmpty(keptBackground))  data.Scenario = keptBackground;

                // Save the data
                NPCData.Save(npc.uuid, data);
                // GenContext reads the bundle on disk: without this the first conversation after
                // generating a profile was written without it.
                NPCData.FlushSessionLore();

                // Sync to native ImportantCharacterData so CharacterSheet displays our results
                NPCData.SyncToNativeImportantData(npc, data.Personality, data.Scenario, npc.GetPotentiallyNullDescription());

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
                context = context ?? "";
                if (context.Length > 3000) context = context.Substring(context.Length - 3000);

                string playerName = PlayerName();

                // Must run before CurrentActivity is replaced below: it recognises the old mirrored
                // text in the native background by comparing against the activity it was copied from.
                string background = Clip(NPCData.GetBackground(npc, data), 400);

                // Private dwellings are off limits: the NPC must not be written as going to, entering
                // or turning up at one (unless it is where they already are).
                var forbidden = PrivateDwellingGuard.ForbiddenDwellingNames(npc);
                var playerPlace = SS.I?.hackyManager?.currentPlace;
                // PlaceOf, not npc.parentPlace: that legacy field is null, which made this true for every
                // NPC whenever the player was in a private home, including those in the room with them.
                bool playerAtHome = PrivateDwellingGuard.IsPrivate(playerPlace) && PrivateDwellingGuard.PlaceOf(npc) != playerPlace;
                string privacyRule = "";
                if (forbidden.Count > 0)
                    privacyRule += $"PRIVACY RULE: These places are private homes: {string.Join(", ", forbidden.Select(n => $"'{n}'"))}. " +
                                   $"Do NOT place, move, send or have '{npc.GetPrettyName()}' visit, enter or arrive at any of them.\n";
                if (playerAtHome)
                    privacyRule += $"{playerName} is inside a private home right now. '{npc.GetPrettyName()}' is NOT there and cannot see what happens inside; " +
                                   $"describe them going about their own business elsewhere.\n";
                if (privacyRule.Length > 0) privacyRule += "\n";

                string prompt = $"You are a creative writer for a fantasy RPG. You are updating the situation for an NPC named '{npc.GetPrettyName()}'.\n" +
                                privacyRule +
                                $"IMPORTANT: In the context below, 'You' refers to the player character, '{playerName}'. " +
                                $"The NPC '{npc.GetPrettyName()}' is a separate entity. Do NOT confuse the NPC with the player.\n\n" +
                                (background.Length > 0 ? $"Who the NPC is (background, for reference only, do not repeat it):\n{background}\n\n" : "") +
                                $"The NPC's current situation:\n{data.CurrentActivity}\n\n" +
                                $"Recent world developments (from the perspective of {playerName}):\n{context}\n\n" +
                                $"Based on the world context, update the NPC '{npc.GetPrettyName()}'s current 'scenario' or 'situation' to reflect these developments. " +
                                $"Describe what the NPC is doing or where they are, considering the world changes. " +
                                $"If the NPC is nearby the player, they might be observing or reacting to the player's actions. " +
                                $"Keep it concise (1-3 sentences). Respond ONLY with the updated scenario text.";

                string updatedScenario = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE
                );

                Debug.Log($"[AIROG_NPCExpansion] UpdateScenario AI response for {npc.GetPrettyName()}: {updatedScenario}");

                // The player may have left this save, or switched the mod off, while the AI was thinking.
                if (!data.IsFromCurrentSave || !ModSwitch.Enabled)
                    return false;

                // Backstop for the privacy rule: models do not always obey it, so an update that still
                // puts the NPC at someone's private home is dropped and the previous situation kept.
                string intrusion = PrivateDwellingGuard.MentionedDwelling(updatedScenario, forbidden);
                if (intrusion != null)
                {
                    Debug.Log($"[AIROG_NPCExpansion] Discarded scenario update for {npc.GetPrettyName()}: it mentions the private dwelling '{intrusion}'.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(updatedScenario))
                {
                    // The activity has its own field. It is deliberately NOT written to Scenario or to the
                    // game's native background any more: both hold the NPC's background, which is the player's.
                    data.CurrentActivity = updatedScenario.Trim();
                    NPCData.Save(npc.uuid, data);

                    // Seed scenario snippet into the rumor network so other NPCs can learn it
                    string factSnippet = $"{npc.GetPrettyName()}: {data.CurrentActivity}";
                    RumorNetwork.AddFact(npc.uuid, factSnippet);

                    // Log situation update to game chat so players see NPC reactions without opening profiles
                    var gameLog = SS.I?.hackyManager?.gameLogView;
                    if (gameLog != null)
                        _ = gameLog.LogTextCompat(GameLogView.AiDecision($"[{npc.GetPrettyName()}] {data.CurrentActivity}"));

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

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        // The legacy PcGameEntity (playerCharacter.pcGameEntity) is null on current saves, so the
        // name has to come from the player's GameCharacter.
        private static string PlayerName()
        {
            try
            {
                var manager = SS.I?.hackyManager;
                if (manager != null && manager.HasPlayerName()) return manager.GetPlayerName();
            }
            catch { /* player not spawned yet */ }
            return "the player";
        }

        private static string ConstructPrompt(GameCharacter npc, NPCData data, string context,
            string keptPersonality = null, string keptBackground = null)
        {
            string playerName = PlayerName();

            string extraInstructions = "";
            if (!string.IsNullOrEmpty(data.GenerationInstructions))
            {
                extraInstructions = $"\nIMPORTANT USER INSTRUCTION: {data.GenerationInstructions}\n" +
                                    $"Follow the above instruction STRICTLY when generating stats, skills, and background. It overrides conflicting implied traits from the name.";
            }
            // Text that is being kept still has to steer the stats, abilities and goal generated around it.
            if (!string.IsNullOrEmpty(keptPersonality))
                extraInstructions += $"\nESTABLISHED PERSONALITY (stay consistent with it): {Clip(keptPersonality, 400)}";
            if (!string.IsNullOrEmpty(keptBackground))
                extraInstructions += $"\nESTABLISHED BACKGROUND (stay consistent with it): {Clip(keptBackground, 400)}";

            string prompt = $"You are a creative writer for a fantasy RPG. " +
                   $"Identify this NPC: '{npc.GetPrettyName()}'. Description: '{npc.GetPotentiallyNullDescription()}'. " +
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
                   $"- \"background\": (string) Who they are and where they come from: history, role, ties. 2-3 sentences. NOT what they are doing right now.\n" +
                   $"- \"scenario\": (string) What they are doing right now, in 1-2 sentences.\n" +
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
            
            // The component's own description field is not where the game keeps the live text.
            data.Description = npc.GetPotentiallyNullDescription();

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
                    // "scenario" is the present-tense situation, "background" the history; they are separate fields.
                    if (json.ContainsKey("scenario")) data.CurrentActivity = json["scenario"].ToString();
                    if (json.ContainsKey("background") && !string.IsNullOrWhiteSpace(json["background"].ToString()))
                        data.Scenario = json["background"].ToString();
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
            string s_bg = TryExtractField(jsonResponse, "background");
            string s_pers = TryExtractField(jsonResponse, "personality");
            string s_ex = TryExtractField(jsonResponse, "example_dialogue");
            string s_sys = TryExtractField(jsonResponse, "system_prompt");
            
            if (s_first != null) data.FirstMessage = s_first;
            if (s_scen != null) data.CurrentActivity = s_scen;
            if (!string.IsNullOrWhiteSpace(s_bg)) data.Scenario = s_bg;
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
