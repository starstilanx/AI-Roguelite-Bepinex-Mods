using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Slices the hand-made Assets/SkillSpriteSheet.png (40 circular skill medallions, 10x4)
    /// into individual sprites and picks a thematically fitting one per web node, so every
    /// node — including the thousands of Basic nodes that will never get an AI-generated
    /// icon — shows meaningful art instantly. Bespoke AI icons (NodeIconGen) still replace
    /// these stock medallions when they finish generating.
    ///
    /// The sheet is AI-drawn and its grid drifts a few pixels per cell, so each icon's rect
    /// was measured from the alpha channel (weighted centroid, 132px square) and baked here
    /// rather than derived from uniform cell math. Rect Y is in Unity texture space (bottom-up).
    /// </summary>
    public static class SkillIconAtlas
    {
        public class IconDef
        {
            public readonly string id;
            public readonly Rect rect;
            public readonly string attr;       // primary SS.PlayerAttribute affinity ("" = none)
            public readonly int epic;          // 0 common, 1 notable-flavored, 2 keystone-flavored
            public readonly string[] keywords; // lowercase substrings matched against node text

            public IconDef(string id, float x, float y, string attr, int epic, params string[] keywords)
            {
                this.id = id;
                this.rect = new Rect(x, y, 132, 132);
                this.attr = attr;
                this.epic = epic;
                this.keywords = keywords;
            }
        }

        public static readonly IconDef[] Icons =
        {
            // ── Row 1: weapons & combat basics ─────────────────────────────────────
            new IconDef("sword",     29,  582, "Strength",  0, "sword", "blade", "slash", "strike", "impact", "breaker", "weapon", "edge"),
            new IconDef("flame",     158, 582, "",          0, "fire", "flame", "burn", "blaze", "ember", "scorch", "cinder", "torch"),
            new IconDef("shield",    299, 582, "Strength",  0, "shield", "guard", "block", "bulwark", "bastion", "aegis", "vigil", "defen", "protect", "stance", "stalwart"),
            new IconDef("axe",       438, 582, "Strength",  0, "axe", "cleave", "crush", "chop", "hew", "brutal", "shatter", "smash"),
            new IconDef("bow",       574, 582, "Dexterity", 0, "bow", "arrow", "archer", "shot", "ranged", "hunt", "quiver"),
            new IconDef("heal",      710, 581, "",          0, "heal", "mend", "restor", "regen", "cure", "salve", "medic", "renewal"),
            new IconDef("haste",     844, 582, "Dexterity", 0, "swift", "speed", "haste", "fleet", "quick", "rush", "dash", "sprint", "zephyr", "wind", "flicker"),
            new IconDef("bolt",      980, 582, "Intellect", 0, "spark", "surge", "charge", "shock", "bolt", "energy", "jolt", "power", "caster", "spell"),
            new IconDef("mind",      1120, 582, "Intellect", 0, "mind", "psychic", "mental", "focus", "vision", "astral", "whisper", "aura", "dream", "thought"),
            new IconDef("berserker", 1251, 582, "Strength",  1, "berserk", "rage", "fury", "wrath", "war", "savage", "helm", "barbarian", "vigorous", "titanic"),

            // ── Row 2: attributes & fortune ────────────────────────────────────────
            new IconDef("strength",  29,  409, "Strength",  0, "strength", "might", "muscle", "brawn", "iron", "fortitude", "coloss", "unyielding", "hardened", "grip", "stout"),
            new IconDef("agility",   159, 409, "Dexterity", 0, "agil", "acrobat", "nimble", "evas", "dodge", "glide", "stride", "step", "balance", "reflex", "finesse"),
            new IconDef("intellect", 300, 408, "Intellect", 0, "intellect", "knowledge", "logic", "sage", "academic", "lore", "study", "memory", "wit", "brain", "scholar"),
            new IconDef("heart",     438, 409, "Strength",  0, "heart", "health", "vitality", "vigor", "blood", "courage", "mortal", "coil", "life", "feast"),
            new IconDef("nature",    574, 408, "",          0, "nature", "leaf", "herb", "growth", "verdant", "grove", "thorn", "bloom", "wild"),
            new IconDef("armor",     716, 409, "Strength",  0, "armor", "plate", "steel", "mail", "carapace", "weightless", "encumbrance"),
            new IconDef("critical",  850, 409, "Cunning",   0, "critical", "crit", "deadly", "execut", "rupture", "burst", "carnage", "bleed"),
            new IconDef("pierce",    982, 409, "Dexterity", 0, "pierc", "dart", "projectile", "thrust", "javelin", "lance", "deflect", "velocity", "thread"),
            new IconDef("potion",    1118, 409, "Intellect", 0, "potion", "flask", "elixir", "alchem", "brew", "mana", "tonic"),
            new IconDef("luck",      1251, 409, "Cunning",   0, "luck", "fortune", "chance", "gamble", "fate", "clover", "serendip", "trick"),

            // ── Row 3: subterfuge & specialist skills ──────────────────────────────
            new IconDef("stealth",   28,  226, "Cunning",   0, "stealth", "sneak", "silent", "hidden", "guise", "shroud", "covert", "murky", "prowl", "shadow"),
            new IconDef("trap",      159, 226, "Cunning",   0, "trap", "snare", "ambush", "trigger", "bait"),
            new IconDef("poison",    300, 226, "Cunning",   0, "poison", "venom", "toxi", "viper", "blight", "coating"),
            new IconDef("waypoint",  440, 226, "",          0, "path", "scout", "travel", "journey", "track", "map", "wayfar", "navigat", "explor", "beacon"),
            new IconDef("assassin",  579, 226, "Cunning",   1, "assassin", "rogue", "cloak", "hood", "devious", "treacher", "scheme", "subtle", "ghost", "phantom", "shadowstep", "dagger"),
            new IconDef("frost",     720, 226, "Intellect", 0, "frost", "ice", "cold", "freez", "chill", "winter", "glacial", "snow"),
            new IconDef("insight",   857, 226, "Intellect", 0, "insight", "idea", "clarity", "luminous", "bright", "revelation", "illuminat", "epiphany", "genius"),
            new IconDef("lock",      987, 226, "Cunning",   0, "lock", "key", "vault", "seal", "secret", "cipher", "secure"),
            new IconDef("aim",       1119, 226, "Dexterity", 0, "aim", "precis", "accura", "mark", "target", "flawless", "sharpshoot", "deadeye"),
            new IconDef("ward",      1251, 226, "Intellect", 0, "ward", "barrier", "rune", "enchant", "mystic", "arcane", "abjur", "eldritch"),

            // ── Row 4: epic & elemental forces ─────────────────────────────────────
            new IconDef("dualswords", 28,  33, "Strength",  1, "duel", "dual", "twin", "blades", "crossed", "combat", "flurry"),
            new IconDef("meteor",     159, 33, "Intellect", 2, "meteor", "comet", "star", "celestial", "cataclysm", "heavens", "cosmic", "falling"),
            new IconDef("grave",      300, 33, "Cunning",   1, "death", "grave", "tomb", "necro", "undead", "spirit", "haunt", "reaper", "wraith"),
            new IconDef("inferno",    442, 33, "",          1, "inferno", "immolat", "phoenix", "flare", "conflagr", "wildfire", "blazing", "pyre"),
            new IconDef("void",       583, 33, "",          2, "void", "abyss", "dark", "black", "chaos", "entropy", "oblivion", "eclipse", "umbral"),
            new IconDef("resonance",  721, 33, "",          1, "resonance", "echo", "ripple", "pulse", "harmonic", "portal", "nexus", "attun", "confluence", "rift"),
            new IconDef("sigil",      858, 34, "Charisma",  1, "command", "authority", "imperial", "radiant", "tyrant", "sun", "sigil", "emblem", "majestic", "banner", "rally", "presence", "inspir"),
            new IconDef("crystal",    989, 34, "",          1, "crystal", "shard", "gem", "prism", "amber", "geode", "earth", "stone"),
            new IconDef("storm",      1119, 33, "Intellect", 2, "storm", "lightning", "thunder", "tempest", "maelstrom"),
            new IconDef("crown",      1251, 33, "Charisma",  1, "crown", "king", "queen", "regal", "noble", "ascend", "gilded", "grand", "sovereign", "vow", "leader", "speech", "voice", "eloquent", "diplomat", "charm", "barter", "bargain"),
        };

        const string SheetFile = "SkillSpriteSheet.png";

        static Texture2D _sheet;
        static bool _loadAttempted;
        static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

        static Texture2D Sheet
        {
            get
            {
                if (_loadAttempted) return _sheet;
                _loadAttempted = true;
                try
                {
                    string path = Path.Combine(Application.streamingAssetsPath, "SkillWeb", SheetFile);
                    if (!File.Exists(path))
                    {
                        Debug.LogWarning($"[SkillWeb] Icon sprite sheet not found at {path} — node medallions disabled.");
                        return null;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    ImageConversion.LoadImage(tex, File.ReadAllBytes(path));
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _sheet = tex;
                }
                catch (Exception ex)
                {
                    Debug.LogError("[SkillWeb] Failed to load icon sprite sheet: " + ex.Message);
                }
                return _sheet;
            }
        }

        public static bool Available =>
            Sheet != null && (SkillWebPlugin.Instance?.SkillConfig?.UseSpriteSheetIcons ?? true);

        /// <summary>Deterministically picks the best-fitting medallion sprite for this node. Null if the sheet is missing/disabled.</summary>
        public static Sprite PickForNode(WebNode node)
        {
            if (node == null || !Available) return null;
            IconDef def = PickDef(node);
            return def == null ? null : GetSprite(def);
        }

        static Sprite GetSprite(IconDef def)
        {
            if (_spriteCache.TryGetValue(def.id, out Sprite cached)) return cached;
            var sprite = Sprite.Create(Sheet, def.rect, new Vector2(0.5f, 0.5f));
            _spriteCache[def.id] = sprite;
            return sprite;
        }

        static IconDef PickDef(WebNode node)
        {
            string name = (node.name ?? "").ToLowerInvariant();
            string rest = ((node.description ?? "") + " " +
                           (node.traits != null ? string.Join(" ", node.traits) : "") + " " +
                           (node.keystoneRule ?? "")).ToLowerInvariant();

            // Dominant attribute by highest-magnitude stat (mirrors GlyphFileForNode).
            string dominant = null;
            float best = 0f;
            if (node.stats != null)
            {
                foreach (var kv in node.stats)
                {
                    float mag = Math.Abs(kv.Value);
                    if (mag > best) { best = mag; dominant = kv.Key; }
                }
            }

            IconDef bestDef = null;
            long bestScore = long.MinValue;
            bool anyTextHit = false;

            foreach (var def in Icons)
            {
                // A direct name match must outweigh attribute affinity + rarity fit combined,
                // so "Blood Magic" gets the heart, not whichever epic coin shares its attribute.
                int score = 0;
                foreach (var kw in def.keywords)
                {
                    if (name.Contains(kw)) { score += 6; anyTextHit = true; }
                    else if (rest.Contains(kw)) { score += 2; anyTextHit = true; }
                }

                if (dominant != null && string.Equals(def.attr, dominant, StringComparison.OrdinalIgnoreCase))
                    score += 2;

                // Rarity fit: reserve the dramatic art for the nodes that earn it.
                if (node.type == WebNodeType.Keystone || node.type == WebNodeType.Confluence)
                    score += def.epic;
                else if (node.type == WebNodeType.Notable && def.epic == 1)
                    score += 1;
                else if (node.type == WebNodeType.Basic && def.epic == 2)
                    score -= 3;

                // Stable per-node jitter spreads ties across the candidate set so a Strength
                // sector isn't a wall of identical bicep coins.
                long ranked = ((long)score << 8) | (Fnv1a(node.id + "|" + def.id) & 0xFF);
                if (ranked > bestScore)
                {
                    bestScore = ranked;
                    bestDef = def;
                }
            }

            // Nothing in the text matched at all: fall back to the dominant-attribute icon
            // family (or the whole common set) so the hash jitter picks among sensible art
            // instead of crowning a mule.
            if (!anyTextHit)
            {
                bestDef = null;
                bestScore = long.MinValue;
                foreach (var def in Icons)
                {
                    bool attrFit = dominant != null && string.Equals(def.attr, dominant, StringComparison.OrdinalIgnoreCase);
                    if (!attrFit && def.epic != 0) continue;
                    if (!attrFit && dominant != null && def.attr.Length > 0) continue;
                    long ranked = ((attrFit ? 1L : 0L) << 8) | (Fnv1a(node.id + "|" + def.id) & 0xFF);
                    if (ranked > bestScore)
                    {
                        bestScore = ranked;
                        bestDef = def;
                    }
                }
            }

            return bestDef;
        }

        static uint Fnv1a(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < s.Length; i++)
                {
                    hash ^= s[i];
                    hash *= 16777619;
                }
                return hash;
            }
        }
    }
}
