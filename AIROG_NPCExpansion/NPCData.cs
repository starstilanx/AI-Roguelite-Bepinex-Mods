using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;
using UnityEngine;
using System.Linq;

namespace AIROG_NPCExpansion
{
    [Serializable]
    public class NPCData
    {
        public string Name;
        public string Description;
        public string Personality;
        public string Scenario;
        public string FirstMessage;
        public string MessageExamples;
        public string CreatorNotes;
        public string SystemPrompt;
        public string PostHistoryInstructions;
        public List<string> AlternateGreetings;
        public List<string> Tags;
        public List<string> InteractionTraits;
        public Dictionary<string, string> Extensions;
        public string GenerationInstructions = ""; // User-provided hints for generation

        // Long-Term Memory & Narrative Goals
        public List<string> LongTermMemories = new List<string>();
        public string CurrentGoal = "";
        public string GoalProgress = "";
        public List<string> RecentThoughts = new List<string>();

        // Relationship System
        public int Affinity = 0; // -100 to 100
        public string RelationshipStatus = "Stranger";
        public List<string> InteractionHistory = new List<string>();

        // Equipment System
        public Dictionary<string, string> EquippedUuids = new Dictionary<string, string>();

        // Autonomy Settings
        public bool AllowAutoEquip = true;
        public bool AllowSelfPreservation = true;
        public bool AllowEconomicActivity = false;
        public bool AllowWorldInteraction = true;
        public bool IsNemesis = false;

        // Reputation System — behavior-driven tags
        public List<string> ReputationTags = new List<string>();

        // Rumor / Knowledge Network
        public List<string> KnownFacts = new List<string>();

        // Bark System
        public int LastBarkTurn = -99;

        // Memory Synthesis
        public int MemorySynthesisTurn = 0;

        // Death Tracking
        public bool IsDeceased = false;
        public string DeathInfo = "";
        public string Epitaph = "";

        // Secret System
        public List<NPCSecret> Secrets = new List<NPCSecret>();

        // Relationship Arc
        public List<string> ArcMilestones = new List<string>();
        
        // Stats & Skills
        public Dictionary<SS.PlayerAttribute, long> Attributes = new Dictionary<SS.PlayerAttribute, long>();
        public Dictionary<string, PlayerSkill> Skills = new Dictionary<string, PlayerSkill>();
        
        [Serializable]
        public class NPCSecret
        {
            public string Category = "Unknown"; // Crime, Allegiance, Relationship, Ability, Past
            public string Text = "";
            public bool IsRevealed = false;
        }

        [Serializable]
        public struct AbilityData
        {
            public string Name;
            public string Description;

            public AbilityData(string name, string desc)
            {
                Name = name;
                Description = desc;
            }

            public override string ToString() => $"{Name}: {Description}";
        }

        public List<AbilityData> DetailedAbilities = new List<AbilityData>();
        
        // The Abilities property is a compatibility shim for old saves that stored a flat string list.
        // Getter: derives names from DetailedAbilities (use DetailedAbilities directly in hot paths).
        // Setter: migrates legacy string list by converting entries into AbilityData with no description.
        public List<string> Abilities
        {
            get { return DetailedAbilities.Select(a => a.Name).ToList(); }
            set
            {
                if (value == null || value.Count == 0) return;
                // Only migrate if DetailedAbilities is empty to avoid overwriting richer data
                if (DetailedAbilities.Count == 0)
                {
                    foreach (var name in value)
                    {
                        if (!string.IsNullOrEmpty(name))
                            DetailedAbilities.Add(new AbilityData(name, "No description provided."));
                    }
                }
            }
        }


        public NPCData()
        {
            AlternateGreetings = new List<string>();
            Tags = new List<string>();
            InteractionTraits = new List<string>();
            Extensions = new Dictionary<string, string>();
            InteractionHistory = new List<string>();
            EquippedUuids = new Dictionary<string, string>();
            Attributes = new Dictionary<SS.PlayerAttribute, long>();
            Skills = new Dictionary<string, PlayerSkill>();
            // Note: DetailedAbilities is initialized inline. Do NOT call Abilities setter here
            // as it would trigger migration logic on an empty list unnecessarily.
            
            // Default attributes
            Attributes[SS.PlayerAttribute.Strength] = 10;
            Attributes[SS.PlayerAttribute.Dexterity] = 10;
            Attributes[SS.PlayerAttribute.Intellect] = 10;
            Attributes[SS.PlayerAttribute.Cunning] = 10;
            Attributes[SS.PlayerAttribute.Charisma] = 10;
        }

        public void ChangeAffinity(int delta, string reason = null)
        {
            Affinity = Mathf.Clamp(Affinity + delta, -100, 100);
            
            if (!string.IsNullOrEmpty(reason))
            {
                InteractionHistory.Insert(0, reason);
                if (InteractionHistory.Count > 5) InteractionHistory.RemoveAt(5);
            }

            // Update status based on affinity — aligned with RelationshipArcSystem stages
            if (Affinity >= 90) RelationshipStatus = "Sworn Companion";
            else if (Affinity >= 75) RelationshipStatus = "Confidant";
            else if (Affinity >= 60) RelationshipStatus = "Ally";
            else if (Affinity >= 40) RelationshipStatus = "Friend";
            else if (Affinity >= 20) RelationshipStatus = "Acquaintance";
            else if (Affinity > -20) RelationshipStatus = "Stranger";
            else if (Affinity > -40) RelationshipStatus = "Disliked";
            else if (Affinity > -60) RelationshipStatus = "Enemy";
            else RelationshipStatus = "Nemesis";
        }

        // --- Social Network System (NPC-NPC) ---
        public Dictionary<string, int> NpcAffinities = new Dictionary<string, int>();

        public int GetAffinity(string targetUuid)
        {
            if (NpcAffinities.TryGetValue(targetUuid, out int val)) return val;
            return 0;
        }

        public void ChangeAffinity(string targetUuid, int delta)
        {
            if (!NpcAffinities.ContainsKey(targetUuid)) NpcAffinities[targetUuid] = 0;
            NpcAffinities[targetUuid] = Mathf.Clamp(NpcAffinities[targetUuid] + delta, -100, 100);
        }

        public static Dictionary<string, NPCData> LoreCache = new Dictionary<string, NPCData>();

        public static NPCData CreateDefault(string name)
        {
            return new NPCData
            {
                Name = name,
                Description = "",
                Personality = "",
                Scenario = "",
                FirstMessage = "",
                MessageExamples = ""
            };
        }

        public static void Save(string uuid, NPCData data)
        {
            LoreCache[uuid] = data;
            
            // 1. Save to global plugin folder (Global Backup)
            try
            {
                string globalPath = Path.Combine(NPCExpansionPlugin.NPCDataPath, uuid + ".json");
                File.WriteAllText(globalPath, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Failed global save: {ex.Message}");
            }
        }

        public static NPCData Load(string uuid)
        {
            // 1. Check Cache
            if (LoreCache.TryGetValue(uuid, out var cachedData)) return cachedData;

            // 2. Check Global Folder
            string globalPath = Path.Combine(NPCExpansionPlugin.NPCDataPath, uuid + ".json");
            if (File.Exists(globalPath))
            {
                try
                {
                    var data = JsonConvert.DeserializeObject<NPCData>(File.ReadAllText(globalPath));
                    LoreCache[uuid] = data;
                    return data;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AIROG_NPCExpansion] Failed global load: {ex.Message}");
                }
            }
            return null;
        }

        public static void SaveSessionLore(string saveDir)
        {
            if (LoreCache.Count == 0) return;
            try
            {
                string path = Path.Combine(saveDir, "npcexpansion_lore.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(LoreCache, Formatting.Indented));
                Debug.Log($"[AIROG_NPCExpansion] Saved {LoreCache.Count} lore entries to save folder.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Failed to save lore bundle: {ex.Message}");
            }
        }

        public static void LoadSessionLore(string saveDir)
        {
            LoreCache.Clear(); // Clear existing cache for new session
            string path = Path.Combine(saveDir, "npcexpansion_lore.json");
            if (!File.Exists(path)) return;

            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, NPCData>>(File.ReadAllText(path));
                foreach (var kvp in loaded)
                {
                    LoreCache[kvp.Key] = kvp.Value;
                }
                Debug.Log($"[AIROG_NPCExpansion] Loaded {loaded.Count} lore entries from save folder.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Failed to load lore bundle: {ex.Message}");
            }
        }
    }
}
