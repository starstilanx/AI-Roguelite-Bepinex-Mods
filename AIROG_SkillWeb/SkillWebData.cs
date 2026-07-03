using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// A lightweight snapshot of a native PerkNode's relevant runtime state, passed from the
    /// plugin (which can read the live game) into the data layer for recomputation.
    /// </summary>
    public struct PerkSnapshot
    {
        public string uuid;
        public bool isActivated;
    }

    /// <summary>The derived mechanical bonus attached to a single native perk (keyed by perk UUID).</summary>
    [Serializable]
    public class PerkBonus
    {
        public string perkUuid;
        public string perkName;

        /// <summary>Attribute name (Strength/Dexterity/Intellect/Cunning/Charisma) → base amount when learned.</summary>
        public Dictionary<string, float> stats = new Dictionary<string, float>();

        /// <summary>True once stats have been computed (heuristic or AI). Prevents re-derivation.</summary>
        public bool derived = false;

        /// <summary>True once the AI pass has refined the heuristic result.</summary>
        public bool aiRefined = false;
    }

    /// <summary>
    /// Per-save sidecar that stores attribute bonuses for native perks. The native perk tree is the
    /// source of truth for which perks exist / are learned / are active; this only adds the mechanical
    /// stat layer that the native (purely narrative) system lacks.
    /// </summary>
    public class SkillWebData
    {
        /// <summary>perk UUID → derived bonus. The only persisted state.</summary>
        public Dictionary<string, PerkBonus> perkBonuses = new Dictionary<string, PerkBonus>();

        /// <summary>Accumulated attribute bonuses from all currently-learned perks. Rebuilt on sync; not serialized.</summary>
        [JsonIgnore]
        public Dictionary<SS.PlayerAttribute, float> CachedStats = new Dictionary<SS.PlayerAttribute, float>();

        // ── Persistence ─────────────────────────────────────────────────────────

        public static SkillWebData Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    var data = JsonConvert.DeserializeObject<SkillWebData>(File.ReadAllText(path));
                    if (data != null)
                    {
                        if (data.perkBonuses == null) data.perkBonuses = new Dictionary<string, PerkBonus>();
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SkillWeb] Load error: {ex.Message}");
                }
            }
            return new SkillWebData();
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

        // ── Bonus bookkeeping ─────────────────────────────────────────────────────

        public PerkBonus GetOrCreate(string uuid, string name)
        {
            if (!perkBonuses.TryGetValue(uuid, out PerkBonus pb))
            {
                pb = new PerkBonus { perkUuid = uuid, perkName = name };
                perkBonuses[uuid] = pb;
            }
            else if (!string.IsNullOrEmpty(name))
            {
                pb.perkName = name;
            }
            return pb;
        }

        /// <summary>
        /// Rebuilds <see cref="CachedStats"/> from the supplied set of currently-learned perks.
        /// Active perks have their bonus scaled by <see cref="SkillWebConfig.ActiveBonusMultiplier"/>.
        /// </summary>
        public void RecalculateStats(IEnumerable<PerkSnapshot> learnedPerks, SkillWebConfig cfg)
        {
            if (CachedStats == null) CachedStats = new Dictionary<SS.PlayerAttribute, float>();
            CachedStats.Clear();
            if (learnedPerks == null) return;

            foreach (var snap in learnedPerks)
            {
                if (snap.uuid == null) continue;
                if (!perkBonuses.TryGetValue(snap.uuid, out PerkBonus pb) || pb.stats == null) continue;

                float mult = snap.isActivated ? cfg.ActiveBonusMultiplier : 1f;
                foreach (var kvp in pb.stats)
                {
                    if (!Enum.TryParse(kvp.Key, true, out SS.PlayerAttribute attr) ||
                        attr == SS.PlayerAttribute.Unknown) continue;
                    if (!CachedStats.ContainsKey(attr)) CachedStats[attr] = 0f;
                    CachedStats[attr] += kvp.Value * mult;
                }
            }

            // Clamp each attribute's total to the configured ceiling.
            var keys = new List<SS.PlayerAttribute>(CachedStats.Keys);
            foreach (var k in keys)
                CachedStats[k] = Mathf.Clamp(CachedStats[k], -cfg.MaxBonusPerAttribute, cfg.MaxBonusPerAttribute);
        }
    }
}
