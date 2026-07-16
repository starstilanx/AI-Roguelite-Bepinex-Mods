using System.Collections.Generic;

namespace AIROG_SkillWeb
{
    public static class ThemeLexicon
    {
        public static readonly Dictionary<string, string[]> Prefixes = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "Iron", "Heavy", "Titanic", "Stout", "Brawny", "Unyielding", "Hardened", "Colossal", "Fortified", "Brutal", "Vigorous", "Shattering" },
            ["Dexterity"] = new[] { "Swift", "Nimble", "Fleet", "Ghostly", "Precise", "Flashing", "Flickering", "Sly", "Silken", "Zephyr", "Acrobatic", "Evasive" },
            ["Intellect"] = new[] { "Arcane", "Sage", "Mental", "Runic", "Eldritch", "Mystic", "Luminous", "Logical", "Lorebound", "Academic", "Astral", "Whispering" },
            ["Cunning"] = new[] { "Shadowy", "Venomous", "Subtle", "Silent", "Devious", "Guileful", "Stealthy", "Sly", "Treacherous", "Murky", "Covert", "Viper" },
            ["Charisma"] = new[] { "Grand", "Charming", "Inspiring", "Noble", "Regal", "Commanding", "Majestic", "Diplomatic", "Eloquent", "Radiant", "Imperial", "Gilded" }
        };

        public static readonly Dictionary<string, string[]> Nouns = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "Vigil", "Bulwark", "Colossus", "Grip", "Might", "Aegis", "Crusher", "Fortitude", "Bastion", "Stance", "Breaker", "Impact" },
            ["Dexterity"] = new[] { "Step", "Stride", "Finesse", "Reflex", "Wind", "Dagger", "Flicker", "Thread", "Glide", "Arrow", "Shadowstep", "Guise" },
            ["Intellect"] = new[] { "Mind", "Focus", "Rune", "Insight", "Knowledge", "Aura", "Enchantment", "Beacon", "Caster", "Spark", "Nexus", "Vision" },
            ["Cunning"] = new[] { "Ambush", "Poison", "Trick", "Shroud", "Venom", "Snare", "Scheme", "Gossip", "Doubt", "Whisper", "Viper", "Dagger" },
            ["Charisma"] = new[] { "Presence", "Crown", "Barter", "Voice", "Leader", "Speech", "Shield", "Rally", "Bargain", "Feast", "Command", "Banner" }
        };

        public static readonly Dictionary<string, string[]> Flavors = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "A hard-tested technique that shapes the body into an immovable obstacle.", "A surge of physical power that demands respect from all.", "An unyielding posture that turns bone and muscle into iron." },
            ["Dexterity"] = new[] { "A fluid movement that leaves behind nothing but a fading afterimage.", "A precise adjustment of weight and speed to outpace any reaction.", "A light-footed step that ignores friction and gravity." },
            ["Intellect"] = new[] { "A focusing technique that taps into hidden fonts of mental acuity.", "An arcane realization that aligns the mind with universal laws.", "A deep study of historical patterns that unlocks hidden knowledge." },
            ["Cunning"] = new[] { "A quiet strategy developed in dark corners and hushed rooms.", "A venomous tactic that strikes when the opponent is most blind.", "An underhanded maneuver designed to subvert normal rules." },
            ["Charisma"] = new[] { "An imposing presence that demands absolute attention and respect.", "A gilded word that bridges differences and opens locked vaults.", "A natural aura of authority that rallies friends and disarms foes." }
        };

        public static readonly Dictionary<string, string[]> Traits = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "Passive Regeneration", "Stun Resistance", "Weightless Armor", "Encumbrance Immunity" },
            ["Dexterity"] = new[] { "Flawless Balance", "Silent Movement", "Catlike Landing", "Projectile Deflection" },
            ["Intellect"] = new[] { "Mana Flow", "Arcane Shield", "Enhanced Scans", "Spell Echo" },
            ["Cunning"] = new[] { "Poison Coating", "Trap Awareness", "Smoke Evade", "Lockpick Mastery" },
            ["Charisma"] = new[] { "Bribe Reduction", "Negotiator", "Inspiring Shout", "Follower Shield" }
        };

        public static readonly string[] GenericKeystones = new string[]
        {
            "Unbreakable Vow", "Chaos Attunement", "Mortal Coil", "Blood Magic", "Ghost Walk", "Perfect Clarity", "Tyrant Command"
        };

        public static readonly string[] GenericKeystoneRules = new string[]
        {
            "You cannot heal naturally, but health is restored when defeating an enemy.",
            "Spells cost life instead of mana, and mana is added to maximum health.",
            "You cannot retreat from combat once engaged, and NPCs know this about you.",
            "All physical damage received is split and absorbed by your mana pool.",
            "Your critical strikes never miss but deal normal damage, and apply bleeding.",
            "Your speech options can never be ignored, but shop prices are doubled.",
            "You gain immune-frames during quick-dodges, but item defense values are halved."
        };
    }
}
