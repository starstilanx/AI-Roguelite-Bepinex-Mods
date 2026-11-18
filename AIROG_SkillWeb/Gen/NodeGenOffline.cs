using System;
using System.Collections.Generic;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Instant, deterministic node content from the theme lexicon. Every node gets offline content
    /// when the web is generated; the AI refiner later rewrites Notables, Keystones, Masteries and
    /// Confluences ring by ring as they are charted.
    /// </summary>
    public static class NodeGenOffline
    {
        private static readonly string[] Attributes = ThemeLexicon.Attributes;

        private static readonly Dictionary<string, string[]> Keywords = new Dictionary<string, string[]>
        {
            ["Strength"]  = new[] { "strength", "strong", "might", "mighty", "power", "force", "muscle", "brawn", "tough", "endurance", "stamina", "physical", "crush", "smash", "heavy", "carry", "fortitude", "constitution", "vigor", "iron", "warrior", "berserk", "brute" },
            ["Dexterity"] = new[] { "dexterity", "dexterous", "agile", "agility", "nimble", "speed", "swift", "quick", "reflex", "evasion", "dodge", "balance", "acrobat", "finesse", "precision", "aim", "archery", "footwork", "marksman", "blade" },
            ["Intellect"] = new[] { "intellect", "intelligence", "intelligent", "magic", "magical", "arcane", "spell", "mana", "wisdom", "knowledge", "lore", "scholar", "study", "mind", "mental", "logic", "reason", "rune", "enchant", "mystic", "wit", "tech", "science", "engineer" },
            ["Cunning"]   = new[] { "cunning", "stealth", "sneak", "guile", "trick", "deceit", "deception", "shadow", "thief", "steal", "lockpick", "trap", "ambush", "subterfuge", "sly", "sabotage", "poison", "assassin", "scheme", "rogue", "spy" },
            ["Charisma"]  = new[] { "charisma", "charm", "charming", "social", "persuade", "persuasion", "leader", "leadership", "diplomacy", "diplomat", "intimidate", "speech", "rhetoric", "inspire", "command", "presence", "negotiat", "barter", "seduc", "noble", "bard" },
        };

        /// <summary>Analyzes a sector to identify its best thematic attribute mapping.</summary>
        public static string GetSectorAttribute(WebSector sector)
        {
            if (sector == null) return "Strength";
            if (!string.IsNullOrEmpty(sector.attr)) return sector.attr;
            return RankAttributes((sector.name ?? "") + " " + (sector.purpose ?? ""))[0];
        }

        /// <summary>Primary and secondary attribute for a free-text theme (e.g. a native perk tree's name).</summary>
        public static (string attr, string alt) PickAttributes(string text)
        {
            var ranked = RankAttributes(text);
            string attr = ranked[0];
            string alt = ranked[1];
            // No second keyword hit: fall back to the attribute's natural partner.
            if (CountHits(text, alt) == 0) alt = ThemeLexicon.DefaultAlt[attr];
            return (attr, alt);
        }

        static List<string> RankAttributes(string text)
        {
            string t = (text ?? "").ToLowerInvariant();
            var list = new List<string>(Attributes);
            // Stable: ties keep the canonical order so an empty theme lands on Strength.
            var hits = new Dictionary<string, int>();
            foreach (var a in Attributes) hits[a] = CountHits(t, a);
            list.Sort((x, y) =>
            {
                int c = hits[y].CompareTo(hits[x]);
                return c != 0 ? c : Array.IndexOf(Attributes, x).CompareTo(Array.IndexOf(Attributes, y));
            });
            return list;
        }

        static int CountHits(string text, string attr)
        {
            string t = (text ?? "").ToLowerInvariant();
            int count = 0;
            foreach (var kw in Keywords[attr]) if (t.Contains(kw)) count++;
            return count;
        }

        static T Pick<T>(System.Random r, IList<T> list) => list[r.Next(list.Count)];

        static string UniqueName(System.Random r, HashSet<string> used, Func<string> make)
        {
            for (int i = 0; i < 12; i++)
            {
                string s = make();
                if (used.Add(s)) return s;
            }
            string fallback = make();
            used.Add(fallback);
            return fallback;
        }

        /// <summary>
        /// Writes name, description, stats and traits onto a structurally-placed node.
        /// <paramref name="other"/> is the second sector for Confluences, otherwise null.
        /// </summary>
        public static void Fill(WebNode node, WebSector sector, WebSector other, System.Random r,
                                HashSet<string> usedNames, HashSet<string> usedKeystones)
        {
            string attr = sector.attr, alt = sector.altAttr;
            string[] adj = ThemeLexicon.Prefixes[attr];
            string[] noun = ThemeLexicon.Nouns[attr];
            node.stats.Clear();
            node.traits.Clear();
            node.keystoneRule = null;
            node.masteryOptions = null;
            node.masteryChoice = -1;
            node.abilityName = null;
            node.aiRefined = false;
            node.tier = node.IsTiered ? 1 : 0;

            switch (node.type)
            {
                case WebNodeType.Travel:
                {
                    string a = r.NextDouble() < 0.65 ? attr : alt;
                    node.name = a;
                    node.description = "A minor star on the paths of " + sector.name + ".";
                    node.stats[a] = 1;
                    break;
                }
                case WebNodeType.Basic:
                {
                    node.name = UniqueName(r, usedNames, () => Pick(r, adj) + " " + Pick(r, noun));
                    node.description = Pick(r, ThemeLexicon.Flavors[attr]);
                    node.stats[r.NextDouble() < 0.7 ? attr : alt] = r.NextDouble() < 0.3 ? 2 : 1;
                    break;
                }
                case WebNodeType.Notable:
                {
                    node.name = UniqueName(r, usedNames, () => r.NextDouble() < 0.5
                        ? "The " + Pick(r, adj) + " " + Pick(r, noun)
                        : Pick(r, noun) + " of the " + Pick(r, ThemeLexicon.Prefixes[alt]));
                    node.description = Pick(r, ThemeLexicon.Flavors[attr]);
                    node.stats[attr] = r.Next(2, 4);   // +2..+3
                    node.stats[alt] = r.Next(1, 3);    // +1..+2
                    node.traits.Add(Pick(r, TraitPool(attr, alt)));
                    break;
                }
                case WebNodeType.Keystone:
                {
                    var def = PickKeystone(r, attr, alt, usedKeystones);
                    node.name = def.name;
                    usedNames.Add(def.name);
                    node.description = "A keystone of " + sector.name + ". It rewrites how the world treats you.";
                    node.keystoneRule = def.rule;
                    node.abilityName = def.ability;
                    node.stats[attr] = 5;
                    var others = new List<string>();
                    foreach (var a in Attributes) if (a != attr && a != alt) others.Add(a);
                    node.stats[Pick(r, others)] = -2;
                    break;
                }
                case WebNodeType.Mastery:
                {
                    node.name = UniqueName(r, usedNames, () => Pick(r, noun) + " Mastery");
                    node.description = "Choose one of these effects. You can change your choice at any time.";
                    var pool = new List<string>(TraitPool(attr, alt));
                    Shuffle(r, pool);
                    node.masteryOptions = pool.GetRange(0, Math.Min(3, pool.Count));
                    break;
                }
                case WebNodeType.Confluence:
                {
                    var o = other ?? sector;
                    node.name = UniqueName(r, usedNames, () => Pick(r, adj) + " " + Pick(r, ThemeLexicon.Nouns[o.attr]));
                    node.description = "Where " + sector.name + " meets " + o.name + ".";
                    node.stats[attr] = 2;
                    node.stats[o.attr] = node.stats.TryGetValue(o.attr, out float v) ? v + 2 : 2;
                    node.traits.Add(Pick(r, r.NextDouble() < 0.5 ? ThemeLexicon.NarrativeTraits[attr] : ThemeLexicon.NarrativeTraits[o.attr]));
                    break;
                }
            }
        }

        static List<string> TraitPool(string attr, string alt)
        {
            var pool = new List<string>(ThemeLexicon.NarrativeTraits[attr]);
            if (alt != attr) pool.AddRange(ThemeLexicon.NarrativeTraits[alt]);
            return pool;
        }

        static ThemeLexicon.KeystoneDef PickKeystone(System.Random r, string attr, string alt, HashSet<string> used)
        {
            foreach (var a in new[] { attr, alt })
            {
                var defs = new List<ThemeLexicon.KeystoneDef>(ThemeLexicon.Keystones[a]);
                Shuffle(r, defs);
                foreach (var d in defs)
                    if (used.Add(d.name)) return d;
            }
            // Every themed keystone is taken (tiny chance on 8-sector vast webs): reuse one.
            return Pick(r, ThemeLexicon.Keystones[attr]);
        }

        static void Shuffle<T>(System.Random r, List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = r.Next(i + 1);
                var t = list[i]; list[i] = list[j]; list[j] = t;
            }
        }
    }
}
