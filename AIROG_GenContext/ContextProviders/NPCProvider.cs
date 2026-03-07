using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.ContextProviders
{
    public class NPCProvider : IContextProvider
    {
        public int Priority => 200; // Critical Priority (Higher than History)
        public string Name => "NPC Expansion";
        public string Description => "Injects detailed information about the NPC you are currently conversing with, including personality, goals, and recent thoughts.";

        // Data classes to match JSON structure from AIROG_NPCExpansion
        // SYNC NOTE: Keep in sync with NPCData.cs in AIROG_NPCExpansion
#pragma warning disable 0649
        private class NPCDataStub
        {
            // Core Identity
            public string Name;
            public string Description;
            public string Personality;
            public string Scenario;

            // Character Card Fields (for context if needed)
            public string FirstMessage;
            public string MessageExamples; // Kept for JSON round-trip fidelity; not injected into prompt
            public string CreatorNotes;
            public string SystemPrompt;
            public string PostHistoryInstructions;
            public List<string> AlternateGreetings;
            public List<string> Tags;
            public List<string> InteractionTraits;
            public string GenerationInstructions;

            // Long-Term Memory & Narrative Goals
            public List<string> LongTermMemories;
            public string CurrentGoal;
            public string GoalProgress;
            public List<string> RecentThoughts;

            // Relationship System
            public int Affinity;
            public string RelationshipStatus;
            public List<string> InteractionHistory;

            // Equipment System
            public Dictionary<string, string> EquippedUuids;

            // Autonomy Settings (for reference, not injected)
            public bool AllowAutoEquip;
            public bool AllowSelfPreservation;
            public bool AllowEconomicActivity;
            public bool AllowWorldInteraction;
            public bool IsNemesis;

            // Stats & Skills
            public Dictionary<SS.PlayerAttribute, long> Attributes;
            public Dictionary<string, PlayerSkillStub> Skills;

            // Matches NPCData.AbilityData struct
            public class AbilityDataStub
            {
                public string Name;
                public string Description;
            }
            public List<AbilityDataStub> DetailedAbilities;
            public List<string> Abilities; // Legacy fallback

            // Social Network (NPC-NPC)
            public Dictionary<string, int> NpcAffinities;

            // New Systems (v2.0)
            public List<string> ReputationTags;
            public List<string> KnownFacts;
            public bool IsDeceased;
        }

        private class PlayerSkillStub
        {
            public int level;
        }

        // Minimal quest stub for cross-assembly JSON reading (no hard dep on NPCExpansion)
        private class QuestStub
        {
            public string GiverId;
            public string ObjectiveText;
            public string CompletionCondition;
            public string Status; // "Active", "Completed", "Failed"
        }

        private Dictionary<string, NPCDataStub> _npcCache = new Dictionary<string, NPCDataStub>();
        private float _lastLoadTime = 0;
        private const float CACHE_REFRESH_RATE = 5f; 

        public string GetContext(string prompt, int maxTokens)
        {
            var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
            if (manager == null || manager.npcActionsHandler == null) return "";

            // Check if we are talking to someone
            var npc = manager.npcActionsHandler.currentNpc;
            if (npc == null) return "";

            RefreshCacheIfNeeded();

            if (_npcCache.TryGetValue(npc.uuid, out var data))
            {
                // Format concise context
                string context = $"\n\n[NPC: {data.Name}]\n";

                // --- NEMESIS ALERT ---
                if (data.IsNemesis)
                {
                    context += "[NARRATIVE ALERT: This NPC is your NEMESIS. They previously KILLED you. They should be arrogant, mocking, and constantly reference their victory.]\n";
                }
                
                // 1. Core Identity
                if (!string.IsNullOrEmpty(data.Description)) context += $"Description: {data.Description}\n";
                if (!string.IsNullOrEmpty(data.Personality)) context += $"Personality: {data.Personality}\n";
                
                // Tags (character archetype hints)
                if (data.Tags != null && data.Tags.Count > 0)
                {
                    context += "Tags: " + string.Join(", ", data.Tags.Take(5)) + "\n";
                }
                
                // Interaction Traits (behavioral quirks)
                if (data.InteractionTraits != null && data.InteractionTraits.Count > 0)
                {
                    context += "Behavioral Traits: " + string.Join("; ", data.InteractionTraits.Take(4)) + "\n";
                }

                // Reputation Tags (emergent from NPC behavior)
                if (data.ReputationTags != null && data.ReputationTags.Count > 0)
                {
                    context += "Reputation: " + string.Join(", ", data.ReputationTags) + "\n";
                }

                // 2. Character Card Guidance (AI Roleplay Instructions)
                if (!string.IsNullOrEmpty(data.CreatorNotes))
                {
                    // Truncate if too long to save tokens
                    string notes = data.CreatorNotes.Length > 200 
                        ? data.CreatorNotes.Substring(0, 200) + "..." 
                        : data.CreatorNotes;
                    context += $"[Author Notes: {notes}]\n";
                }
                if (!string.IsNullOrEmpty(data.SystemPrompt))
                {
                    string sysPrompt = data.SystemPrompt.Length > 150 
                        ? data.SystemPrompt.Substring(0, 150) + "..." 
                        : data.SystemPrompt;
                    context += $"[Character Instructions: {sysPrompt}]\n";
                }
                
                // 3. Scene Snapshot (Current Context Summary)
                context += BuildSceneSnapshot(manager, npc);
                
                // 4. Current State & Goals
                if (!string.IsNullOrEmpty(data.Scenario)) context += $"Current Status: {data.Scenario}\n";
                if (!string.IsNullOrEmpty(data.CurrentGoal)) 
                {
                    string goalLine = $"Current Goal: {data.CurrentGoal}";
                    if (!string.IsNullOrEmpty(data.GoalProgress)) goalLine += $" (Progress: {data.GoalProgress})";
                    context += goalLine + "\n";
                }
                
                // Recent Thoughts (inner monologue)
                if (data.RecentThoughts != null && data.RecentThoughts.Count > 0)
                {
                    context += "Recent Thoughts: \"" + string.Join("\" \"", data.RecentThoughts.Take(2)) + "\"\n";
                }
                
                // 5. Relationship with Player
                context += $"Relationship: {data.RelationshipStatus} (Affinity: {data.Affinity}/100)\n";
                
                if (data.LongTermMemories != null && data.LongTermMemories.Count > 0)
                {
                     context += "Key Memories: " + string.Join("; ", data.LongTermMemories.Take(3)) + "\n";
                }

                if (data.InteractionHistory != null && data.InteractionHistory.Count > 0)
                {
                   context += "Recent Interactions: " + string.Join("; ", data.InteractionHistory.Take(3)) + "\n";
                }

                // 6. Stats & Skills (Simplified - only show if present)
                if (data.Attributes != null && data.Attributes.Count > 0)
                {
                    context += "Stats: " + string.Join(", ", data.Attributes.Select(k => $"{k.Key}:{k.Value}")) + "\n";
                }
                
                List<string> skillsAndAbilities = new List<string>();
                if (data.Skills != null)
                {
                    foreach(var s in data.Skills) skillsAndAbilities.Add($"{s.Key} (Lvl {s.Value.level})");
                }
                
                if (data.DetailedAbilities != null && data.DetailedAbilities.Count > 0)
                {
                    foreach (var abil in data.DetailedAbilities)
                    {
                        skillsAndAbilities.Add($"{abil.Name}: {abil.Description}");
                    }
                }
                else if (data.Abilities != null)
                {
                    skillsAndAbilities.AddRange(data.Abilities);
                }
                
                if (skillsAndAbilities.Count > 0)
                {
                    context += "Capabilities: " + string.Join("; ", skillsAndAbilities) + "\n";
                }

                // 7. Equipment
                if (data.EquippedUuids != null && data.EquippedUuids.Count > 0)
                {
                    List<string> itemNames = new List<string>();
                    foreach(var kvp in data.EquippedUuids)
                    {
                        // Use npc.items to resolve names, ensuring parity with the original NPCExpansion implementation
                        string uid = kvp.Value;
                        if (npc.items != null)
                        {
                            var item = npc.items.Find(i => i.uuid == uid);
                            if (item != null)
                            {
                                itemNames.Add($"{kvp.Key}: {item.GetPrettyName()}");
                            }
                        }
                    }
                    
                    if (itemNames.Count > 0)
                    {
                        context += "Equipped: " + string.Join(", ", itemNames) + "\n";
                    }
                }

                // 8. Social Context (NPC-NPC)
                if (manager.currentPlace != null)
                {
                    var nearbyChars = manager.currentPlace.GetAliveNpcs()
                        .Concat(manager.currentPlace.GetAliveEnemies())
                        .Where(c => c != npc).ToList();

                    if (nearbyChars.Count > 0)
                    {
                        List<string> relations = new List<string>();
                        foreach (var other in nearbyChars)
                        {
                            // 1. Base (Stored) Affinity
                            int affinity = 0;
                            string otherUuid = other.uuid; // Assuming GameEntity has uuid
                             
                            if (data.NpcAffinities != null && !string.IsNullOrEmpty(otherUuid) && data.NpcAffinities.ContainsKey(otherUuid))
                            {
                                affinity = data.NpcAffinities[otherUuid];
                            }
                            // Fallback to name match if UUID fails/missing (for robustness)
                            else if (data.NpcAffinities != null && data.NpcAffinities.ContainsKey(other.GetPrettyName()))
                            {
                                affinity = data.NpcAffinities[other.GetPrettyName()];
                            }

                            // 2. Faction Modifier
                            int factionMod = 0;
                            if (npc.faction != null && other.faction != null)
                            {
                                if (npc.faction == other.faction) factionMod += 20;
                                // Can add more complex faction logic here if Faction class exposes stance
                            }

                            // 3. Enemy Type Modifier
                            int typeMod = 0;
                            if (npc.IsEnemyType() && !other.IsEnemyType()) typeMod -= 50;
                            else if (!npc.IsEnemyType() && other.IsEnemyType()) typeMod -= 50;

                            int effective = affinity + factionMod + typeMod;

                            // Only report significant relationships or if manual affinity exists
                            if (Math.Abs(effective) >= 15 || (data.NpcAffinities != null && (data.NpcAffinities.ContainsKey(otherUuid) || data.NpcAffinities.ContainsKey(other.GetPrettyName()))))
                            {
                                string relStatus = "Neutral";
                                if (effective >= 80) relStatus = "Ally";
                                else if (effective >= 20) relStatus = "Friend";
                                else if (effective <= -80) relStatus = "Nemesis";
                                else if (effective <= -20) relStatus = "Adversary";
                                
                                string reason = "";
                                if (factionMod > 0) reason = "(Faction)";
                                if (typeMod < 0) reason = "(Species)";
                                
                                relations.Add($"{other.GetPrettyName()}: {relStatus} {reason} [{effective}]");
                            }
                        }
                        
                        if (relations.Count > 0)
                        {
                            context += "Social Dynamics: " + string.Join("; ", relations) + "\n";
                        }
                    }
                }

                // Known facts (from rumor network) — only when token budget permits
                if (data.KnownFacts != null && data.KnownFacts.Count > 0 && maxTokens > 100)
                {
                    var facts = data.KnownFacts.Skip(Math.Max(0, data.KnownFacts.Count - 2)).ToList();
                    context += "Known: " + string.Join("; ", facts) + "\n";
                }

                // Active quest from this NPC — if player has accepted one
                try
                {
                    // Dynamically load quest data without hard assembly dependency
                    string questSaveDir = SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg)
                        ? System.IO.Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "npcexpansion_quests.json")
                        : null;

                    if (questSaveDir != null && System.IO.File.Exists(questSaveDir))
                    {
                        string questJson = System.IO.File.ReadAllText(questSaveDir);
                        var quests = Newtonsoft.Json.JsonConvert.DeserializeObject<List<QuestStub>>(questJson);
                        var activeQuest = quests?.FirstOrDefault(q =>
                            q.GiverId == npc.uuid && q.Status == "Active");

                        if (activeQuest != null)
                        {
                            context += $"[Active Quest given to player]: {activeQuest.ObjectiveText}";
                            if (!string.IsNullOrEmpty(activeQuest.CompletionCondition))
                                context += $" | Completion: {activeQuest.CompletionCondition}";
                            context += "\n";
                        }
                    }
                }
                catch { /* Non-critical; quest file may not exist yet */ }

                context += "[INSTRUCTION: Roleplay this NPC based on their Personality, Traits, Goals, Memories, and Relationships. Use their Capabilities in combat. Consider the current Scene context.]";

                return context;
            }



            // --- AMBIENT/EXPLORATION INJECTION ---
            // If we are NOT talking to an NPC, check if any nearby NPCs are mentioned in the prompt.
            // This restores the functionality removed from NPCExpansionPlugin.
            
            if (manager.currentPlace != null)
            {
                var nearbyChars = manager.currentPlace.GetAliveNpcs();
                if (nearbyChars == null) return "";

                string ambientContext = "";
                int count = 0;

                foreach (var other in nearbyChars)
                {
                    if (count >= 2) break; // Limit to 2 NPCs max to save tokens
                    
                    // Simple case-insensitive check
                    if (prompt.IndexOf(other.GetPrettyName(), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (_npcCache.TryGetValue(other.uuid, out var ambientData))
                        {
                            string shortInj = $"\n[NPC '{ambientData.Name}': {ambientData.Personality ?? "Unknown"}, {ambientData.Scenario ?? "No status"}]";
                            ambientContext += shortInj;
                            count++;
                        }
                    }
                }
                
                return ambientContext;
            }

            return "";
        }

        /// <summary>
        /// Builds a concise snapshot of the current scene for AI context.
        /// Includes: Location, Nearby Characters, Player Presence
        /// </summary>
        private string BuildSceneSnapshot(GameplayManager manager, GameCharacter npc)
        {
            if (manager == null || manager.currentPlace == null) return "";
            
            var sb = new System.Text.StringBuilder();
            sb.Append("[Scene: ");
            
            // 1. Location Name & Layout
            string placeName = manager.currentPlace.GetPrettyName();
            sb.Append(placeName);
            
            // Add danger level if meaningful
            if (manager.currentPlace.dangerLvl > 0)
            {
                string dangerDesc = manager.currentPlace.dangerLvl switch
                {
                    1 => "Safe",
                    2 => "Low Danger",
                    3 => "Moderate Danger", 
                    4 => "Dangerous",
                    5 => "Deadly",
                    _ => ""
                };
                if (!string.IsNullOrEmpty(dangerDesc)) sb.Append($" ({dangerDesc})");
            }
            
            // 2. Nearby Characters (summary)
            var nearbyNpcs = manager.currentPlace.GetAliveNpcs()?.Where(c => c != npc).ToList();
            var nearbyEnemies = manager.currentPlace.GetAliveEnemies()?.Where(c => c != npc).ToList();
            
            int npcCount = nearbyNpcs?.Count ?? 0;
            int enemyCount = nearbyEnemies?.Count ?? 0;
            
            if (npcCount > 0 || enemyCount > 0)
            {
                sb.Append(" | Present: ");
                List<string> present = new List<string>();
                
                if (npcCount > 0)
                {
                    if (npcCount <= 3)
                    {
                        present.AddRange(nearbyNpcs.Select(n => n.GetPrettyName()));
                    }
                    else
                    {
                        present.Add($"{npcCount} NPCs");
                    }
                }
                
                if (enemyCount > 0)
                {
                    if (enemyCount <= 2)
                    {
                        present.AddRange(nearbyEnemies.Select(e => e.GetPrettyName()));
                    }
                    else
                    {
                        present.Add($"{enemyCount} hostiles");
                    }
                }
                
                sb.Append(string.Join(", ", present));
            }
            
            // 3. Player presence — manager.currentPlace is the active scene shared by this NPC and the manager.
            // The player is "nearby" only if they exist and are in this same active scene (i.e. not dead/absent).
            if (manager.playerCharacter != null && manager.currentPlace != null && npc.parentPlace == manager.currentPlace)
            {
                sb.Append(" | Player nearby");
            }
            
            sb.Append("]\n");
            return sb.ToString();
        }

        private void RefreshCacheIfNeeded()
        {
            if (Time.time - _lastLoadTime > CACHE_REFRESH_RATE)
            {
                LoadData();
                _lastLoadTime = Time.time;
            }
        }

        private void LoadData()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;

            string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "npcexpansion_lore.json");
            if (!File.Exists(path)) return;

            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, NPCDataStub>>(File.ReadAllText(path));
                if (loaded != null)
                {
                    _npcCache = loaded;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load NPC data: {ex.Message}");
            }
        }
    }
}
