using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    public enum WebNodeType
    {
        Basic,
        Notable,
        Keystone,
        Anchor,
        Confluence
    }

    [Serializable]
    public class WebNode
    {
        public string id;                       // GUID; for Anchors: "anchor:" + native perk uuid
        public WebNodeType type;
        public string name;                     // procedural name
        public string description;              // lore description
        public string sectorId;                 // owning sector/discipline id
        public int ring;                        // ring index (0 = origin, 1..N outward)
        public float angle;                     // polar angle in radians
        public float radius;                    // distance from center
        public float x;                         // Cartesian X
        public float y;                         // Cartesian Y
        public List<string> edges = new List<string>(); // adjacent node IDs (undirected)
        public Dictionary<string, float> stats = new Dictionary<string, float>(); // attribute -> bonus
        public List<string> traits = new List<string>(); // non-numerical narrative traits
        public string keystoneRule;             // rule directive for keystones
        public bool unlocked;
        public int tier;                        // 0 locked, 1..3 mastery
        public bool aiRefined;                  // true if AI refined
        public string originHook;               // provenance info e.g. "chronicle:<beat>"

        // ── Usable ability grant (Keystone / Confluence nodes) ────────────────────
        public string grantedAbilityUuid;       // stable uuid of the minted usable GameAbility
        public string grantedAbilityDesc;       // AI-generated ability flavor/rules text (cached)
        public int abilityCooldownRemaining;    // persisted cooldown so it survives save/load

        [JsonIgnore]
        public Vector2 Position => new Vector2(x, y);
    }

    [Serializable]
    public class WebSector
    {
        public string id;
        public string name;
        public string purpose;
        public string colorHex;
        public float angleCenter;
        public float angleSpan;
        public int deepestGeneratedRing;
        public string anchorPerkTreeUuid;       // native PerkTree UUID this grew from
    }

    public struct PerkSnapshot
    {
        public string uuid;
        public bool isActivated;
    }

    [Serializable]
    public class PerkBonus
    {
        public string perkUuid;
        public string perkName;
        public Dictionary<string, float> stats = new Dictionary<string, float>();
        public bool derived = false;
        public bool aiRefined = false;
    }

    public class SkillWebData
    {
        public int schemaVersion = 4;
        public long layoutSeed;
        /// <summary>Which WebLayout packing revision produced the stored coordinates (0 = pre-packing saves).</summary>
        public int layoutVersion;
        public List<WebNode> nodes = new List<WebNode>();
        public List<WebSector> sectors = new List<WebSector>();
        public int resonance;
        public int resonanceEarnedTotal;
        public int turnsSurvived;
        public Dictionary<string, long> economyLedger = new Dictionary<string, long>();
        public Dictionary<string, PerkBonus> perkBonuses = new Dictionary<string, PerkBonus>();

        [JsonIgnore]
        public Dictionary<SS.PlayerAttribute, float> CachedStats = new Dictionary<SS.PlayerAttribute, float>();

        // ── Persistence ─────────────────────────────────────────────────────────

        public static SkillWebData Load(string path)
        {
            if (File.Exists(path))
            {
                try
                 {
                    string json = File.ReadAllText(path);
                    var data = JsonConvert.DeserializeObject<SkillWebData>(json);
                    if (data != null)
                    {
                        if (data.nodes == null) data.nodes = new List<WebNode>();
                        if (data.sectors == null) data.sectors = new List<WebSector>();
                        if (data.economyLedger == null) data.economyLedger = new Dictionary<string, long>();
                        if (data.perkBonuses == null) data.perkBonuses = new Dictionary<string, PerkBonus>();

                        // Handle migration
                        if (data.schemaVersion < 4)
                        {
                            Debug.Log($"[SkillWeb] Migrating save data to schema version 4...");
                            var random = new System.Random();
                            data.layoutSeed = ((long)random.Next() << 32) | (uint)random.Next();
                            data.schemaVersion = 4;
                            // Clean existing nodes list for fresh generation, but keep perkBonuses to recreate them as anchors
                            data.nodes.Clear();
                            data.sectors.Clear();
                        }
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SkillWeb] Load error: {ex.Message}");
                }
            }

            // Create new game data
            var newData = new SkillWebData();
            var r = new System.Random();
            newData.layoutSeed = ((long)r.Next() << 32) | (uint)r.Next();
            return newData;
        }

        public void Save(string path)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(this, settings));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkillWeb] Save error: {ex.Message}");
            }
        }

        // ── Collision / Query Helper ──────────────────────────────────────────────

        public bool CheckCollision(Vector2 pos, float minDistance = 50f)
        {
            foreach (var node in nodes)
            {
                if (Vector2.Distance(node.Position, pos) < minDistance)
                    return true;
            }
            return false;
        }

        public WebNode GetNode(string id)
        {
            return nodes.Find(n => n.id == id);
        }

        public WebSector GetSector(string id)
        {
            return sectors.Find(s => s.id == id);
        }
    }
}
