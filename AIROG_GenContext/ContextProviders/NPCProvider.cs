using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.ContextProviders
{
    /// <summary>
    /// The single injection surface for everything AIROG_NPCExpansion produces.
    ///
    /// NPCExpansion owns the game logic and writes its state to JSON in the save folder;
    /// this provider is the only thing that turns that state into prompt text. Three files
    /// are read (all optional — each degrades to nothing if absent):
    ///   npcexpansion_lore.json          — per-NPC lore, memories, reputation, secrets, arcs
    ///   npcexpansion_quests.json        — active quests, keyed to their giver
    ///   npcexpansion_taught_skills.json — techniques NPCs have taught the *player*
    /// </summary>
    public class NPCProvider : IContextProvider
    {
        public int Priority => 200; // Critical Priority (Higher than History)
        public string Name => "NPC Expansion";
        public string Description => "Injects the NPC you are speaking with (personality, goals, memories, secrets, quests) plus techniques NPCs have taught you.";

        private const string LORE_FILE   = "npcexpansion_lore.json";
        private const string QUESTS_FILE = "npcexpansion_quests.json";
        private const string TAUGHT_FILE = "npcexpansion_taught_skills.json";

        // Per-field caps. AI-generated personality/background routinely run several hundred
        // words; without these a single NPC could consume the whole shared token budget and
        // starve every lower-priority provider (this one runs first at priority 200).
        private const int MAX_DESCRIPTION  = 300;
        private const int MAX_PERSONALITY  = 500;
        private const int MAX_SCENARIO     = 400;
        private const int MAX_GOAL         = 200;
        private const int MAX_CREATOR_NOTE = 200;
        private const int MAX_SYS_PROMPT   = 150;
        private const int MAX_TAUGHT_LINES = 8;
        private const int MAX_AMBIENT_NPCS = 2;

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

            // Death Tracking (v4.0)
            public bool IsDeceased;
            public string DeathInfo;
            public string Epitaph;

            // Secrets & Relationship Arcs (v3.0)
            public List<SecretStub> Secrets;
            public List<string> ArcMilestones;
        }

        private class SecretStub
        {
            public string Category;
            public string Text;
            public bool IsRevealed;
        }

        private class PlayerSkillStub
        {
            public int level;
        }

        // Minimal quest stub for cross-assembly JSON reading (no hard dep on NPCExpansion).
        // Status is declared as an enum with the SAME member order as NPCExpansion's
        // QuestStatus: Newtonsoft writes the enum as its integer ordinal ("Status": 0), so a
        // `string Status` field here would deserialize to "0" and never match "Active".
        private enum QuestStatusStub { Active, Completed, Failed }

        private class QuestStub
        {
            public string GiverId;
            public string ObjectiveText;
            public string CompletionCondition;
            public QuestStatusStub Status;
        }

        // Techniques an NPC has taught the player. Player-scoped rather than NPC-scoped, so
        // it is injected on every prompt regardless of who (if anyone) is being spoken to.
        private class TaughtSkillStub
        {
            public string SkillName;
            public string Description;
            public string TeacherName;
        }
#pragma warning restore 0649

        private Dictionary<string, NPCDataStub> _npcCache = new Dictionary<string, NPCDataStub>();
        private readonly Dictionary<string, QuestStub> _activeQuestByGiver = new Dictionary<string, QuestStub>();
        private List<TaughtSkillStub> _taughtSkills = new List<TaughtSkillStub>();

        // Stubs synthesised from the game's native ImportantCharacterData for NPCs that have
        // no entry in the lore JSON. Held separately so a cache reload doesn't discard them.
        private readonly Dictionary<string, NPCDataStub> _nativeStubs = new Dictionary<string, NPCDataStub>();

        private string _cachedSaveDir;
        private float _lastLoadTime = float.MinValue;
        private const float CACHE_REFRESH_RATE = 5f;

        public string GetContext(string prompt, int maxTokens)
        {
            try
            {
                var manager = SS.I?.hackyManager;
                if (manager == null) return "";

                // Must be set before the body is built — the known-facts line consults it.
                _budgetAllowsExtras = maxTokens > 100;

                RefreshCacheIfNeeded();

                var npc = manager.npcActionsHandler?.currentNpc;

                // Focused injection when in conversation; otherwise scan the prompt for
                // nearby characters that the story just mentioned.
                string body = npc != null ? BuildFocusedContext(manager, npc) : "";
                if (string.IsNullOrEmpty(body))
                    body = BuildAmbientContext(manager, npc, prompt);

                // Player-scoped. Previously injected by a Harmony postfix on
                // PlayableCharacterData.GetPlayerStatusStrToAppendNoSpace inside NPCExpansion,
                // which bypassed the shared token budget and the provider on/off toggle.
                body += BuildTaughtSkillsContext();

                return ApplyBudget(body, maxTokens);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] NPCProvider failed: {ex.Message}");
                return "";
            }
        }

        // ─── Focused (in-conversation) injection ───────────────────────────────────

        private string BuildFocusedContext(GameplayManager manager, GameCharacter npc)
        {
            var data = ResolveStub(npc);
            if (data == null) return "";

            var sb = new StringBuilder();
            sb.Append($"\n\n[NPC: {data.Name}]\n");

            // A corpse still selected in the conversation handler: report the death rather
            // than their goals and plans, which would read as though they were still alive.
            if (data.IsDeceased)
            {
                sb.Append("[This character is DEAD. They may only appear in memory, vision, or as a corpse.]\n");
                if (!string.IsNullOrEmpty(data.DeathInfo)) sb.Append($"Death: {data.DeathInfo}\n");
                if (!string.IsNullOrEmpty(data.Epitaph))   sb.Append($"Epitaph: {data.Epitaph}\n");
                if (!string.IsNullOrEmpty(data.Personality))
                    sb.Append($"They were: {Clip(data.Personality, MAX_DESCRIPTION)}\n");
                return sb.ToString();
            }

            // --- NEMESIS ALERT ---
            if (data.IsNemesis)
            {
                sb.Append("[NARRATIVE ALERT: This NPC is your NEMESIS. They previously KILLED you. They should be arrogant, mocking, and constantly reference their victory.]\n");
            }

            // 1. Core Identity
            if (!string.IsNullOrEmpty(data.Description)) sb.Append($"Description: {Clip(data.Description, MAX_DESCRIPTION)}\n");
            if (!string.IsNullOrEmpty(data.Personality)) sb.Append($"Personality: {Clip(data.Personality, MAX_PERSONALITY)}\n");

            // Tags (character archetype hints)
            if (data.Tags != null && data.Tags.Count > 0)
                sb.Append("Tags: " + string.Join(", ", data.Tags.Take(5)) + "\n");

            // Interaction Traits (behavioral quirks)
            if (data.InteractionTraits != null && data.InteractionTraits.Count > 0)
                sb.Append("Behavioral Traits: " + string.Join("; ", data.InteractionTraits.Take(4)) + "\n");

            // Reputation Tags (emergent from NPC behavior)
            if (data.ReputationTags != null && data.ReputationTags.Count > 0)
                sb.Append("Reputation: " + string.Join(", ", data.ReputationTags) + "\n");

            // 2. Character Card Guidance (AI Roleplay Instructions)
            if (!string.IsNullOrEmpty(data.CreatorNotes))
                sb.Append($"[Author Notes: {Clip(data.CreatorNotes, MAX_CREATOR_NOTE)}]\n");
            if (!string.IsNullOrEmpty(data.SystemPrompt))
                sb.Append($"[Character Instructions: {Clip(data.SystemPrompt, MAX_SYS_PROMPT)}]\n");

            // 3. Scene Snapshot (Current Context Summary)
            sb.Append(BuildSceneSnapshot(manager, npc));

            // 4. Current State & Goals
            if (!string.IsNullOrEmpty(data.Scenario)) sb.Append($"Current Status: {Clip(data.Scenario, MAX_SCENARIO)}\n");
            if (!string.IsNullOrEmpty(data.CurrentGoal))
            {
                string goalLine = $"Current Goal: {Clip(data.CurrentGoal, MAX_GOAL)}";
                if (!string.IsNullOrEmpty(data.GoalProgress)) goalLine += $" (Progress: {Clip(data.GoalProgress, MAX_GOAL)})";
                sb.Append(goalLine + "\n");
            }

            // Recent Thoughts (inner monologue)
            if (data.RecentThoughts != null && data.RecentThoughts.Count > 0)
                sb.Append("Recent Thoughts: \"" + string.Join("\" \"", data.RecentThoughts.Take(2)) + "\"\n");

            // 5. Relationship with Player
            sb.Append($"Relationship: {data.RelationshipStatus} (Affinity: {data.Affinity}/100)\n");

            if (data.LongTermMemories != null && data.LongTermMemories.Count > 0)
                sb.Append("Key Memories: " + string.Join("; ", data.LongTermMemories.Take(3)) + "\n");

            if (data.InteractionHistory != null && data.InteractionHistory.Count > 0)
                sb.Append("Recent Interactions: " + string.Join("; ", data.InteractionHistory.Take(3)) + "\n");

            // Relationship arc milestones — the shape of the bond over time, not just its value.
            if (data.ArcMilestones != null && data.ArcMilestones.Count > 0)
                sb.Append("Relationship Milestones: " + string.Join("; ", data.ArcMilestones.Take(3)) + "\n");

            // Secrets the player has already earned. Unrevealed ones are deliberately withheld
            // so the AI cannot leak knowledge the player has not been given.
            if (data.Secrets != null)
            {
                var revealed = data.Secrets.Where(s => s != null && s.IsRevealed && !string.IsNullOrEmpty(s.Text)).ToList();
                if (revealed.Count > 0)
                    sb.Append("Secrets shared with the player: " +
                              string.Join("; ", revealed.Take(3).Select(s => $"[{s.Category}] {s.Text}")) + "\n");
            }

            // 6. Stats & Skills (Simplified - only show if present)
            if (data.Attributes != null && data.Attributes.Count > 0)
                sb.Append("Stats: " + string.Join(", ", data.Attributes.Select(k => $"{k.Key}:{k.Value}")) + "\n");

            List<string> skillsAndAbilities = new List<string>();
            if (data.Skills != null)
            {
                foreach (var s in data.Skills) skillsAndAbilities.Add($"{s.Key} (Lvl {s.Value.level})");
            }

            if (data.DetailedAbilities != null && data.DetailedAbilities.Count > 0)
            {
                foreach (var abil in data.DetailedAbilities)
                    skillsAndAbilities.Add($"{abil.Name}: {abil.Description}");
            }
            else if (data.Abilities != null)
            {
                skillsAndAbilities.AddRange(data.Abilities);
            }

            if (skillsAndAbilities.Count > 0)
                sb.Append("Capabilities: " + string.Join("; ", skillsAndAbilities) + "\n");

            // 7. Equipment
            if (data.EquippedUuids != null && data.EquippedUuids.Count > 0 && npc.items != null)
            {
                List<string> itemNames = new List<string>();
                foreach (var kvp in data.EquippedUuids)
                {
                    // Use npc.items to resolve names, ensuring parity with the original NPCExpansion implementation
                    var item = npc.items.Find(i => i.uuid == kvp.Value);
                    if (item != null) itemNames.Add($"{kvp.Key}: {item.GetPrettyName()}");
                }

                if (itemNames.Count > 0)
                    sb.Append("Equipped: " + string.Join(", ", itemNames) + "\n");
            }

            // 8. Social Context (NPC-NPC)
            string social = BuildSocialDynamics(manager, npc, data);
            if (!string.IsNullOrEmpty(social)) sb.Append(social);

            // Known facts (from rumor network) — only when token budget permits
            if (data.KnownFacts != null && data.KnownFacts.Count > 0 && _budgetAllowsExtras)
            {
                var facts = data.KnownFacts.Skip(Math.Max(0, data.KnownFacts.Count - 2)).ToList();
                sb.Append("Known: " + string.Join("; ", facts) + "\n");
            }

            // Active quest from this NPC — if player has accepted one
            if (!string.IsNullOrEmpty(npc.uuid) && _activeQuestByGiver.TryGetValue(npc.uuid, out var activeQuest))
            {
                sb.Append($"[Active Quest given to player]: {activeQuest.ObjectiveText}");
                if (!string.IsNullOrEmpty(activeQuest.CompletionCondition))
                    sb.Append($" | Completion: {activeQuest.CompletionCondition}");
                sb.Append("\n");
            }

            sb.Append("[INSTRUCTION: Roleplay this NPC based on their Personality, Traits, Goals, Memories, and Relationships. Use their Capabilities in combat. Consider the current Scene context.]");

            return sb.ToString();
        }

        // Set at the top of each GetContext call so the known-facts line is the first thing
        // dropped when the shared budget is nearly exhausted.
        private bool _budgetAllowsExtras = true;

        private string BuildSocialDynamics(GameplayManager manager, GameCharacter npc, NPCDataStub data)
        {
            if (manager.currentPlace == null) return "";

            var nearbyChars = (manager.currentPlace.GetAliveNpcs() ?? new List<GameCharacter>())
                .Concat(manager.currentPlace.GetAliveEnemies() ?? new List<GameCharacter>())
                .Where(c => c != null && c != npc).ToList();
            if (nearbyChars.Count == 0) return "";

            var relations = new List<string>();
            foreach (var other in nearbyChars)
            {
                // 1. Base (Stored) Affinity. Dictionary.ContainsKey throws on a null key, and
                // uuid can legitimately be empty on freshly spawned characters.
                string otherUuid = other.uuid;
                string otherName = other.GetPrettyName();
                bool hasUuidEntry = data.NpcAffinities != null && !string.IsNullOrEmpty(otherUuid)
                                    && data.NpcAffinities.ContainsKey(otherUuid);
                bool hasNameEntry = data.NpcAffinities != null && !string.IsNullOrEmpty(otherName)
                                    && data.NpcAffinities.ContainsKey(otherName);

                int affinity = 0;
                if (hasUuidEntry) affinity = data.NpcAffinities[otherUuid];
                else if (hasNameEntry) affinity = data.NpcAffinities[otherName]; // Fallback to name match for robustness

                // 2. Faction Modifier
                int factionMod = 0;
                if (npc.faction != null && other.faction != null && npc.faction == other.faction)
                    factionMod += 20;

                // 3. Enemy Type Modifier
                int typeMod = 0;
                if (npc.IsEnemyType() != other.IsEnemyType()) typeMod -= 50;

                int effective = affinity + factionMod + typeMod;

                // Only report significant relationships or if manual affinity exists
                if (Math.Abs(effective) < 15 && !hasUuidEntry && !hasNameEntry) continue;

                string relStatus = "Neutral";
                if (effective >= 80) relStatus = "Ally";
                else if (effective >= 20) relStatus = "Friend";
                else if (effective <= -80) relStatus = "Nemesis";
                else if (effective <= -20) relStatus = "Adversary";

                string reason = "";
                if (factionMod > 0) reason = "(Faction)";
                if (typeMod < 0) reason = "(Species)";

                relations.Add($"{otherName}: {relStatus} {reason} [{effective}]");
            }

            return relations.Count > 0
                ? "Social Dynamics: " + string.Join("; ", relations) + "\n"
                : "";
        }

        // ─── Ambient (exploration) injection ───────────────────────────────────────

        /// <summary>
        /// When the player is not in a conversation, inject a one-line sketch of any nearby
        /// character the prompt already names, so the story AI writes them in character.
        /// </summary>
        private string BuildAmbientContext(GameplayManager manager, GameCharacter focused, string prompt)
        {
            if (manager.currentPlace == null || string.IsNullOrEmpty(prompt)) return "";

            var nearby = (manager.currentPlace.GetAliveNpcs() ?? new List<GameCharacter>())
                .Concat(manager.currentPlace.GetAliveEnemies() ?? new List<GameCharacter>())
                .Where(c => c != null && c != focused)
                .ToList();
            if (nearby.Count == 0) return "";

            var sb = new StringBuilder();
            int count = 0;

            foreach (var other in nearby)
            {
                if (count >= MAX_AMBIENT_NPCS) break; // Limit to save tokens

                string name = other.GetPrettyName();
                if (string.IsNullOrEmpty(name)) continue;
                if (prompt.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var data = ResolveStub(other);
                if (data == null || data.IsDeceased) continue;

                string personality = !string.IsNullOrEmpty(data.Personality) ? Clip(data.Personality, MAX_DESCRIPTION) : "Unknown";
                string status      = !string.IsNullOrEmpty(data.Scenario) ? Clip(data.Scenario, MAX_DESCRIPTION) : "No status";
                sb.Append($"\n[NPC '{data.Name}': {personality}, {status}]");
                count++;
            }

            return sb.ToString();
        }

        // ─── Player-scoped: NPC-taught techniques ──────────────────────────────────

        private string BuildTaughtSkillsContext()
        {
            if (_taughtSkills == null || _taughtSkills.Count == 0) return "";

            var sb = new StringBuilder("\n\n[NPC-Taught Techniques — the player has learned these from NPCs]");
            foreach (var s in _taughtSkills.Take(MAX_TAUGHT_LINES))
            {
                if (s == null || string.IsNullOrEmpty(s.SkillName)) continue;
                sb.Append($"\n  - {s.SkillName}: {s.Description} (taught by {s.TeacherName})");
            }
            return sb.ToString();
        }

        // ─── Scene snapshot ────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a concise snapshot of the current scene for AI context.
        /// Includes: Location, Nearby Characters, Player Presence
        /// </summary>
        private string BuildSceneSnapshot(GameplayManager manager, GameCharacter npc)
        {
            if (manager == null || manager.currentPlace == null) return "";

            var sb = new StringBuilder();
            sb.Append("[Scene: ");

            // 1. Location Name & Layout
            sb.Append(manager.currentPlace.GetPrettyName());

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
                    if (npcCount <= 3) present.AddRange(nearbyNpcs.Select(n => n.GetPrettyName()));
                    else present.Add($"{npcCount} NPCs");
                }

                if (enemyCount > 0)
                {
                    if (enemyCount <= 2) present.AddRange(nearbyEnemies.Select(e => e.GetPrettyName()));
                    else present.Add($"{enemyCount} hostiles");
                }

                sb.Append(string.Join(", ", present));
            }

            // 3. Player presence — manager.currentPlace is the active scene shared by this NPC and the manager.
            // The player is "nearby" only if they exist and are in this same active scene (i.e. not dead/absent).
            if (manager.playerCharacter != null && npc.parentPlace == manager.currentPlace)
                sb.Append(" | Player nearby");

            sb.Append("]\n");
            return sb.ToString();
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the best available stub for a character: the cached lore entry, layered
        /// with the game's native ImportantCharacterData, or a stub built purely from native
        /// data for NPCs profiled with the game's own "Generate details" button.
        /// </summary>
        private NPCDataStub ResolveStub(GameCharacter ch)
        {
            if (ch == null || string.IsNullOrEmpty(ch.uuid)) return null;

            NPCDataStub stub;
            if (!_npcCache.TryGetValue(ch.uuid, out stub)) stub = null;

            var native = ch.importantData;

            if (stub == null)
            {
                // No lore entry. Only worth synthesising one if the game has native details.
                if (native == null || string.IsNullOrEmpty(native.personality)) return null;
                if (!_nativeStubs.TryGetValue(ch.uuid, out stub) || stub == null)
                {
                    stub = new NPCDataStub { RelationshipStatus = "Stranger" };
                    _nativeStubs[ch.uuid] = stub;
                }
            }

            // Layer in any native fields that our stub is missing (old save, generation still
            // in progress, or details edited natively after our last write).
            if (native != null)
            {
                if (string.IsNullOrEmpty(stub.Personality) && !string.IsNullOrEmpty(native.personality))
                    stub.Personality = native.personality;
                if (string.IsNullOrEmpty(stub.Scenario) && !string.IsNullOrEmpty(native.background))
                    stub.Scenario = native.background;
                if (string.IsNullOrEmpty(stub.Description) && !string.IsNullOrEmpty(native.visualDescription))
                    stub.Description = native.visualDescription;
            }

            if (string.IsNullOrEmpty(stub.Name)) stub.Name = ch.GetPrettyName();
            return stub;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max).TrimEnd() + "...";
        }

        /// <summary>
        /// Honours the shared GenContext budget (~4 chars/token). ContextManager trusts each
        /// provider to stay inside the budget it was handed rather than truncating for it.
        /// </summary>
        private string ApplyBudget(string ctx, int maxTokens)
        {
            if (string.IsNullOrEmpty(ctx)) return "";

            int charBudget = maxTokens < int.MaxValue / 8 ? maxTokens * 4 : int.MaxValue;
            if (ctx.Length <= charBudget) return ctx;
            return charBudget < 80 ? "" : ctx.Substring(0, charBudget);
        }

        // ─── Cache ─────────────────────────────────────────────────────────────────

        private void RefreshCacheIfNeeded()
        {
            string saveDir = CurrentSaveDir();

            // A save switch must invalidate immediately — otherwise the previous run's NPCs
            // keep being injected until the refresh window happens to elapse.
            bool saveChanged = !string.Equals(saveDir, _cachedSaveDir, StringComparison.OrdinalIgnoreCase);
            if (!saveChanged && Time.time - _lastLoadTime < CACHE_REFRESH_RATE) return;

            _cachedSaveDir = saveDir;
            _lastLoadTime = Time.time;
            if (saveChanged) _nativeStubs.Clear();
            LoadData(saveDir);
        }

        private static string CurrentSaveDir()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return null;
            return Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
        }

        /// <summary>
        /// Loads all three NPCExpansion files in one pass. Previously the quest file was read
        /// from disk on every single prompt build; it now shares the same refresh window as
        /// the lore cache.
        /// </summary>
        private void LoadData(string saveDir)
        {
            _npcCache = ReadJson<Dictionary<string, NPCDataStub>>(saveDir, LORE_FILE)
                        ?? new Dictionary<string, NPCDataStub>();

            _activeQuestByGiver.Clear();
            var quests = ReadJson<List<QuestStub>>(saveDir, QUESTS_FILE);
            if (quests != null)
            {
                foreach (var q in quests)
                {
                    if (q == null || q.Status != QuestStatusStub.Active) continue;
                    if (string.IsNullOrEmpty(q.GiverId)) continue;
                    _activeQuestByGiver[q.GiverId] = q; // Most recent active quest per giver wins
                }
            }

            _taughtSkills = ReadJson<List<TaughtSkillStub>>(saveDir, TAUGHT_FILE) ?? new List<TaughtSkillStub>();
        }

        private static T ReadJson<T>(string saveDir, string fileName) where T : class
        {
            if (string.IsNullOrEmpty(saveDir)) return null;
            try
            {
                string path = Path.Combine(saveDir, fileName);
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load {fileName}: {ex.Message}");
                return null;
            }
        }
    }
}
