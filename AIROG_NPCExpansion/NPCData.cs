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
        // The NPC's BACKGROUND (who they are, where they come from). The JSON name predates the
        // split below and is kept so existing saves and GenContext's stub keep reading it.
        public string Scenario;
        // What the NPC is doing right now; rewritten by the scenario update every few turns.
        // Through v4.5.0 that text was written into Scenario and mirrored into the game's own
        // background field, which destroyed whatever background the NPC had.
        public string CurrentActivity = "";
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

        // Items placed by the player (Give button or Trade UI); persisted so they survive merchant restock wipes.
        public HashSet<string> PlayerPlacedItemUuids = new HashSet<string>();

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

        // Tags which save session (SS.I.saveSubDirAsArg) this instance was created/loaded under.
        // Not persisted — stamped fresh by the constructor every time an instance is built,
        // whether via `new NPCData()`, CreateDefault(), or Newtonsoft deserialization. Lets
        // Save() detect and drop writes from a background task that is still holding an NPCData
        // loaded under a save the player has since switched away from.
        [JsonIgnore]
        public string SessionSaveDir;

        /// <summary>False once the player has moved to a different save than the one this instance was built under.</summary>
        [JsonIgnore]
        public bool IsFromCurrentSave
        {
            get
            {
                string current = SS.I?.saveSubDirAsArg;
                return string.IsNullOrEmpty(SessionSaveDir) || string.IsNullOrEmpty(current)
                       || string.Equals(SessionSaveDir, current, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Folder name of the save this entry was last written under. Only the global-folder
        // fallback in Load() reads it: scenario presets ship NPCs with fixed uuids, so without
        // it a second playthrough of the same preset picked up the first one's NPC files.
        public string OwnerSave;

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
            SessionSaveDir = SS.I?.saveSubDirAsArg;
            AlternateGreetings = new List<string>();
            Tags = new List<string>();
            InteractionTraits = new List<string>();
            Extensions = new Dictionary<string, string>();
            InteractionHistory = new List<string>();
            EquippedUuids = new Dictionary<string, string>();
            PlayerPlacedItemUuids = new HashSet<string>();
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
                // The Bonds tab lists up to 20; a cap of 5 made its "full history" identical to the overview.
                while (InteractionHistory.Count > 20) InteractionHistory.RemoveAt(InteractionHistory.Count - 1);
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

        // Set by Save(), cleared by SaveSessionLore(). Lets FlushSessionLore() be called
        // freely from anywhere without re-serialising the whole bundle when nothing changed.
        private static bool _loreDirty = false;

        public static void Save(string uuid, NPCData data)
        {
            if (data == null) return;

            // A background task (e.g. an in-flight AI generation) can still be holding an
            // NPCData it loaded under a save the player has since switched away from. Writing
            // it now would contaminate the newly-loaded save with the previous one's data, so
            // drop it instead. Only compares when both sides know a save dir; older/edge-case
            // data with no tag is allowed through rather than blocked.
            string currentSaveDir = SS.I?.saveSubDirAsArg;
            if (!string.IsNullOrEmpty(data.SessionSaveDir) && !string.IsNullOrEmpty(currentSaveDir) &&
                !string.Equals(data.SessionSaveDir, currentSaveDir, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Discarded stale NPCData.Save for {uuid} — data belongs to save '{data.SessionSaveDir}', current save is '{currentSaveDir}'.");
                return;
            }
            data.SessionSaveDir = currentSaveDir;
            data.OwnerSave = SaveKey(currentSaveDir);

            LoreCache[uuid] = data;
            _loreDirty = true;

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
            if (string.IsNullOrEmpty(uuid)) return null;

            // 1. Check Cache
            if (LoreCache.TryGetValue(uuid, out var cachedData)) return cachedData;

            // 2. Check Global Folder (backup for an entry the save's own bundle lost)
            string ownerKey = SaveKey(SS.I?.saveSubDirAsArg);
            if (string.IsNullOrEmpty(ownerKey)) return null;
            string globalPath = Path.Combine(NPCExpansionPlugin.NPCDataPath, uuid + ".json");
            if (File.Exists(globalPath))
            {
                try
                {
                    var data = JsonConvert.DeserializeObject<NPCData>(File.ReadAllText(globalPath));
                    // Files written under another save (or before OwnerSave existed, so unattributable)
                    // describe a different playthrough's NPC that merely shares this uuid.
                    if (data == null || !string.Equals(data.OwnerSave, ownerKey, StringComparison.OrdinalIgnoreCase))
                        return null;
                    data.MigrateLegacyActivity();
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

        /// <summary>
        /// The NPC's entry, created and registered on first use. For characters the game profiled on
        /// its own (HasProfile is true through the native sheet) that have no entry of ours yet.
        /// </summary>
        public static NPCData LoadOrCreate(GameCharacter npc)
        {
            var data = Load(npc.uuid);
            if (data == null)
            {
                data = CreateDefault(npc.GetPrettyName());
                Save(npc.uuid, data);
            }
            return data;
        }

        private static string SaveKey(string saveDir)
        {
            if (string.IsNullOrEmpty(saveDir)) return null;
            try { return Path.GetFileName(saveDir.TrimEnd('\\', '/')); }
            catch { return saveDir; }
        }

        // ─── Background vs. current activity ──────────────────────────────────────

        /// <summary>
        /// True when <paramref name="text"/> is provably a scenario-update result. Every such update
        /// also seeded "&lt;name&gt;: &lt;text&gt;" (clipped to 80 chars) into this NPC's own KnownFacts,
        /// so a rumour whose body opens the text can only have come from that update.
        /// </summary>
        /// <param name="strict">
        /// For the native background field, where a wrong match would discard text the player may
        /// have written: only a long run counts, never a short sentence.
        /// </param>
        private bool IsSeededActivityText(string text, bool strict = false)
        {
            if (string.IsNullOrWhiteSpace(text) || KnownFacts == null) return false;
            text = text.Trim();
            foreach (var fact in KnownFacts)
            {
                // "News: ..." facts come from the world simulation, not from a scenario update.
                if (string.IsNullOrEmpty(fact) || fact.StartsWith("News: ", StringComparison.Ordinal)) continue;
                int sep = fact.IndexOf(": ", StringComparison.Ordinal);
                if (sep < 0) continue;
                string body = fact.Substring(sep + 2);
                if (body.EndsWith("...")) body = body.Substring(0, body.Length - 3);
                body = body.Trim();
                // Rumours about OTHER characters sit in this list too. From those a short shared opening
                // ("The ...") proves nothing; the whole text, a real run, or our own name on it does.
                bool aboutSelf = !string.IsNullOrEmpty(Name) && sep == Name.Length && fact.StartsWith(Name, StringComparison.Ordinal);
                if (body.Length == 0) continue;
                if (strict ? body.Length < 20 : (!aboutSelf && body.Length < 12 && body.Length != text.Length)) continue;
                if (text.StartsWith(body, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Saves from before CurrentActivity existed hold the activity text in Scenario. Moves it
        /// across when it is provably ours; anything else stays in the background for the player.
        /// </summary>
        internal bool MigrateLegacyActivity()
        {
            if (!string.IsNullOrEmpty(CurrentActivity) || !IsSeededActivityText(Scenario)) return false;
            CurrentActivity = Scenario.Trim();
            Scenario = "";
            return true;
        }

        /// <summary>
        /// The other half of that migration: old versions mirrored the activity text into the game's
        /// native background field. If the native field still holds that text, hand the field back.
        /// </summary>
        public static void ReleaseNativeBackground(GameCharacter npc, NPCData data)
        {
            try
            {
                var native = npc?.importantData;
                if (native == null || data == null || string.IsNullOrWhiteSpace(native.background)) return;
                bool isCurrent = !string.IsNullOrWhiteSpace(data.CurrentActivity)
                                 && string.Equals(native.background.Trim(), data.CurrentActivity.Trim(), StringComparison.Ordinal);
                if (!isCurrent && !data.IsSeededActivityText(native.background, strict: true)) return;
                native.background = data.Scenario ?? "";

                // A profile object that held nothing but our text was created by that old mirroring
                // (the game never makes a background-only profile). Left behind, it keeps the NPC
                // flagged "important", which hides the native Generate Profile button and stops the
                // game's own "this character became important" offer for them. Playable characters
                // are excluded: the game requires theirs to exist.
                if (string.IsNullOrEmpty(native.background) && string.IsNullOrEmpty(native.personality)
                    && string.IsNullOrEmpty(native.visualDescription)
                    && (native.relationships == null || native.relationships.Count == 0)
                    && !npc.IsPlayable() && !npc.IsMainPlayer())
                {
                    npc.importantData = null;
                    lock (SS.I.uuidToGameEntityMap) native.TearDown();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCExpansion] ReleaseNativeBackground failed for {npc?.GetPrettyName()}: {ex.Message}");
            }
        }

        // ─── Native importantData bridge helpers ──────────────────────────────────

        /// <summary>
        /// Returns true if this NPC has lore from either source:
        /// our NPCData JSON file OR the game's native ImportantCharacterData.
        /// </summary>
        public static bool HasProfile(GameCharacter npc, NPCData data)
        {
            if (npc?.importantData != null && !string.IsNullOrEmpty(npc.importantData.personality))
                return true;
            return data != null && !string.IsNullOrEmpty(data.Personality);
        }

        /// <summary>
        /// Returns the best available personality string: native importantData first,
        /// then our NPCData field (backward compat with pre-update saves).
        /// </summary>
        public static string GetPersonality(GameCharacter npc, NPCData data)
        {
            if (npc?.importantData != null && !string.IsNullOrEmpty(npc.importantData.personality))
                return npc.importantData.personality;
            return data?.Personality ?? "";
        }

        /// <summary>
        /// Returns the best available background string: native importantData.background
        /// first, then our NPCData.Scenario (backward compat).
        /// </summary>
        public static string GetBackground(GameCharacter npc, NPCData data)
        {
            ReleaseNativeBackground(npc, data);
            if (npc?.importantData != null && !string.IsNullOrEmpty(npc.importantData.background))
                return npc.importantData.background;
            return data?.Scenario ?? "";
        }

        /// <summary>
        /// Writes personality and background into the game's native ImportantCharacterData,
        /// creating the object if needed so CharacterSheet can display our generated data.
        /// Must be called from the main Unity thread (or with manager in scope).
        /// <paramref name="exact"/> is for the dossier's Save: the fields then mirror the boxes
        /// even when the player emptied one (the default only ever fills, never clears).
        /// </summary>
        public static void SyncToNativeImportantData(GameCharacter npc, string personality, string background, string visualDescription = null, bool exact = false)
        {
            try
            {
                // Not npc.manager: NPCs that came out of the new-game menu keep pointing at the menu.
                var mm = SS.I?.Mm();
                if (npc == null || mm == null) return;
                if (npc.importantData == null)
                {
                    // Having importantData is what the game means by "important": it hides the native
                    // Generate Profile button and stops the automatic "became important" profile offer.
                    // So only a real profile (one with a personality) may create it; a lone background
                    // stays in our own data, which the dossier and GenContext read just the same.
                    if (string.IsNullOrEmpty(personality)) return;
                    npc.importantData = new ImportantCharacterData(mm);
                }
                if (exact)
                {
                    npc.importantData.personality = personality ?? "";
                    npc.importantData.background = background ?? "";
                    // null, not "": the game falls back to the entity's own description on null only.
                    npc.importantData.visualDescription = string.IsNullOrEmpty(visualDescription) ? null : visualDescription;
                    return;
                }
                if (!string.IsNullOrEmpty(personality))
                    npc.importantData.personality = personality;
                if (!string.IsNullOrEmpty(background))
                    npc.importantData.background = background;
                if (!string.IsNullOrEmpty(visualDescription))
                    npc.importantData.visualDescription = visualDescription;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCExpansion] SyncToNativeImportantData failed for {npc?.GetPrettyName()}: {ex.Message}");
            }
        }

        // The save LoreCache was built for. Bundle writes go to that save's folder only, so a
        // late flush cannot put one save's NPCs into the folder the game has since switched to.
        private static string _cacheOwner;

        internal static bool CacheBelongsToCurrentSave =>
            SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg)
            && string.Equals(_cacheOwner, SS.I.saveSubDirAsArg, StringComparison.Ordinal);

        /// <summary>Empties the cache for a game that is starting (new, loaded or joined).</summary>
        public static void ResetSession()
        {
            LoreCache.Clear();
            _loreDirty = false;
            _cacheOwner = SS.I?.saveSubDirAsArg;
        }

        public static void SaveSessionLore(string saveDir)
        {
            if (LoreCache.Count == 0) return;
            try
            {
                string path = Path.Combine(saveDir, "npcexpansion_lore.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(LoreCache, Formatting.Indented));
                _loreDirty = false;
                Debug.Log($"[AIROG_NPCExpansion] Saved {LoreCache.Count} lore entries to save folder.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Failed to save lore bundle: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes the lore bundle to the active save folder right now.
        ///
        /// GenContext's NPCProvider injects from npcexpansion_lore.json, and that file is
        /// otherwise only written on SaveIO.WriteSaveFile — which, depending on the player's
        /// SS.I.autoSaveMode, may not happen for a long time. Call this after any change the
        /// AI should notice on the very next prompt (a revealed secret, a new milestone, a
        /// skill taught, a quest given). No-op when no save is active or nothing has changed
        /// since the last write, so it is safe to call on every turn tick.
        /// </summary>
        public static void FlushSessionLore()
        {
            if (!_loreDirty) return;
            // No live game (main menu, scene change) or a cache built for a different save:
            // a background task finishing late must not write this cache anywhere.
            if (SS.I == null || SS.I.hackyManager == null || !CacheBelongsToCurrentSave) return;
            SaveSessionLore(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
        }

        public static void LoadSessionLore(string saveDir)
        {
            ResetSession(); // Clear existing cache for new session
            string path = Path.Combine(saveDir, "npcexpansion_lore.json");
            if (!File.Exists(path)) return;

            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, NPCData>>(File.ReadAllText(path));
                if (loaded == null) return;
                int migrated = 0;
                foreach (var kvp in loaded)
                {
                    if (kvp.Value == null) continue;
                    if (kvp.Value.MigrateLegacyActivity()) migrated++;
                    LoreCache[kvp.Key] = kvp.Value;
                }
                _loreDirty = migrated > 0; // Otherwise cache matches disk
                if (migrated > 0)
                {
                    // Keep the bundle exactly as it was before the first rewrite in the new shape.
                    try
                    {
                        string backup = Path.Combine(saveDir, "npcexpansion_lore.before_activity_split.json");
                        if (!File.Exists(backup)) File.Copy(path, backup);
                    }
                    catch (Exception ex) { Debug.LogWarning($"[AIROG_NPCExpansion] Could not back up the lore bundle: {ex.Message}"); }
                    Debug.Log($"[AIROG_NPCExpansion] Moved {migrated} activity text(s) out of the background field into the current-activity field.");
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
