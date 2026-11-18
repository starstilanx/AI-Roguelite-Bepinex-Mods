using System.Collections.Generic;

namespace AIROG_SkillWeb
{
    public static class ThemeLexicon
    {
        public static readonly string[] Attributes = { "Strength", "Dexterity", "Intellect", "Cunning", "Charisma" };

        /// <summary>Second attribute a sector leans on when its name gives no other hint.</summary>
        public static readonly Dictionary<string, string> DefaultAlt = new Dictionary<string, string>
        {
            ["Strength"] = "Charisma",
            ["Dexterity"] = "Cunning",
            ["Intellect"] = "Cunning",
            ["Cunning"] = "Dexterity",
            ["Charisma"] = "Intellect",
        };

        /// <summary>Muted sector palette; readable on the dark web background and distinct in lightness.</summary>
        public static readonly string[] SectorColors =
        {
            "#D8704F", "#6FBF7A", "#6F95E0", "#D9B454", "#A887D6", "#AEB6C2", "#4FB3A0", "#C86A9A"
        };

        public class FallbackSector
        {
            public string name, purpose, attr, alt;
            public FallbackSector(string n, string p, string a, string b) { name = n; purpose = p; attr = a; alt = b; }
        }

        /// <summary>Sectors used when there are fewer native perk trees than sector slots.</summary>
        public static readonly FallbackSector[] FallbackSectors =
        {
            new FallbackSector("Vigor",      "Strength and physical endurance.",          "Strength",  "Charisma"),
            new FallbackSector("Agility",    "Speed, precision, and quick movement.",     "Dexterity", "Cunning"),
            new FallbackSector("Reason",     "Study, insight, and applied knowledge.",    "Intellect", "Cunning"),
            new FallbackSector("Subterfuge", "Stealth, deception, and dirty tricks.",     "Cunning",   "Dexterity"),
            new FallbackSector("Presence",   "Diplomacy, command, and bargaining.",       "Charisma",  "Intellect"),
            new FallbackSector("Bulwark",    "Protection, resolve, and holding the line.", "Strength", "Intellect"),
            new FallbackSector("Wildcraft",  "Survival, tracking, and the wild places.",  "Dexterity", "Intellect"),
            new FallbackSector("Renown",     "Reputation, courage, and leadership.",      "Charisma",  "Strength"),
        };

        public static readonly Dictionary<string, string[]> Prefixes = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "Iron", "Heavy", "Titanic", "Stout", "Brawny", "Unyielding", "Hardened", "Colossal", "Fortified", "Brutal", "Vigorous", "Shattering" },
            ["Dexterity"] = new[] { "Swift", "Nimble", "Fleet", "Ghostly", "Precise", "Flashing", "Flickering", "Sly", "Silken", "Zephyr", "Acrobatic", "Evasive" },
            ["Intellect"] = new[] { "Arcane", "Sage", "Mental", "Runic", "Eldritch", "Mystic", "Luminous", "Logical", "Lorebound", "Academic", "Astral", "Whispering" },
            ["Cunning"] = new[] { "Shadowy", "Venomous", "Subtle", "Silent", "Devious", "Guileful", "Stealthy", "Crooked", "Treacherous", "Murky", "Covert", "Viper" },
            ["Charisma"] = new[] { "Grand", "Charming", "Inspiring", "Noble", "Regal", "Commanding", "Majestic", "Diplomatic", "Eloquent", "Radiant", "Imperial", "Gilded" }
        };

        public static readonly Dictionary<string, string[]> Nouns = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "Vigil", "Bulwark", "Colossus", "Grip", "Might", "Aegis", "Crusher", "Fortitude", "Bastion", "Stance", "Breaker", "Impact" },
            ["Dexterity"] = new[] { "Step", "Stride", "Finesse", "Reflex", "Wind", "Dagger", "Flicker", "Thread", "Glide", "Arrow", "Shadowstep", "Guise" },
            ["Intellect"] = new[] { "Mind", "Focus", "Rune", "Insight", "Knowledge", "Aura", "Enchantment", "Beacon", "Caster", "Spark", "Nexus", "Vision" },
            ["Cunning"] = new[] { "Ambush", "Poison", "Trick", "Shroud", "Venom", "Snare", "Scheme", "Gossip", "Doubt", "Whisper", "Viper", "Ledger" },
            ["Charisma"] = new[] { "Presence", "Crown", "Barter", "Voice", "Leader", "Speech", "Shield", "Rally", "Bargain", "Feast", "Command", "Banner" }
        };

        public static readonly Dictionary<string, string[]> Flavors = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "A hard-tested technique that shapes the body into an immovable obstacle.", "A surge of physical power that demands respect from all.", "An unyielding posture that turns bone and muscle into iron." },
            ["Dexterity"] = new[] { "A fluid movement that leaves behind nothing but a fading afterimage.", "A precise adjustment of weight and speed to outpace any reaction.", "A light-footed step that ignores friction and gravity." },
            ["Intellect"] = new[] { "A focusing technique that taps into hidden fonts of mental acuity.", "A realization that aligns the mind with the laws of the world.", "A deep study of historical patterns that unlocks hidden knowledge." },
            ["Cunning"] = new[] { "A quiet strategy developed in dark corners and hushed rooms.", "A venomous tactic that strikes when the opponent is most blind.", "An underhanded maneuver designed to subvert normal rules." },
            ["Charisma"] = new[] { "An imposing presence that demands absolute attention and respect.", "A gilded word that bridges differences and opens locked vaults.", "A natural aura of authority that rallies friends and disarms foes." }
        };

        /// <summary>Short trait labels; the AI refiner's fallback when a response omits one.</summary>
        public static readonly Dictionary<string, string[]> Traits = new Dictionary<string, string[]>
        {
            ["Strength"] = new[] { "Passive Regeneration", "Stun Resistance", "Weightless Armor", "Encumbrance Immunity" },
            ["Dexterity"] = new[] { "Flawless Balance", "Silent Movement", "Catlike Landing", "Projectile Deflection" },
            ["Intellect"] = new[] { "Mana Flow", "Arcane Shield", "Enhanced Scans", "Spell Echo" },
            ["Cunning"] = new[] { "Poison Coating", "Trap Awareness", "Smoke Evade", "Lockpick Mastery" },
            ["Charisma"] = new[] { "Bribe Reduction", "Negotiator", "Inspiring Shout", "Follower Shield" }
        };

        /// <summary>
        /// Full-sentence traits for Notables and Masteries. These go straight to the narrator via
        /// GenContext, so they describe what the world does, not a number.
        /// </summary>
        public static readonly Dictionary<string, string[]> NarrativeTraits = new Dictionary<string, string[]>
        {
            ["Strength"] = new[]
            {
                "Heavy weapons and tools feel light in your hands.",
                "Pain sharpens your focus instead of breaking it.",
                "Your blows knock opponents off their footing.",
                "You recover your breath quickly after a hard fight.",
                "Doors, bars and chains give way when you put your weight into them.",
                "You can carry a wounded companion without slowing down.",
                "Soldiers and labourers recognise you as one of their own.",
                "Intimidation comes easily when you stand at full height.",
                "Fire, cold and hunger wear on you more slowly than on others.",
                "You keep your footing on any ground, however rough.",
            },
            ["Dexterity"] = new[]
            {
                "You act first when a fight turns sudden.",
                "Waiting one more breath always improves your aim.",
                "Falls and tumbles rarely hurt you.",
                "You cross rooftops, ledges and rigging without a second thought.",
                "Pursuers lose you at the first corner.",
                "Your hands are steady enough for the most delicate work.",
                "Thrown and fired weapons fly true for you even in bad weather.",
                "You slip out of grapples and bindings with ease.",
                "Crowds part around you without noticing you passed.",
                "You catch what others drop before it hits the ground.",
            },
            ["Intellect"] = new[]
            {
                "Written texts and codes yield their meaning to you quickly.",
                "You remember every text you have ever studied.",
                "Wards, locks and seals show you where they are weakest.",
                "You understand strange devices after a moment's study.",
                "Scholars and experts treat you as a peer.",
                "You spot the flaw in a plan before it is carried out.",
                "Unfamiliar languages become clear after a few exchanges.",
                "Powers you have witnessed once, you can describe perfectly.",
                "You rarely make the same mistake twice.",
                "Maps and routes stay fixed in your memory.",
            },
            ["Cunning"] = new[]
            {
                "Rumours find their way to you within a day.",
                "You remember who owes you, and so do they.",
                "Locks open for you given a quiet moment.",
                "Your lies hold up under a second question.",
                "You notice when you are being followed.",
                "Thieves and smugglers deal with you honestly.",
                "Traps and ambushes rarely catch you unawares.",
                "Small objects hidden on your person escape any search.",
                "You read a bluff before it is finished.",
                "You know poisons and drugs by smell alone.",
            },
            ["Charisma"] = new[]
            {
                "Crowds lean toward you before you speak.",
                "Allies who hear you take heart for the rest of the scene.",
                "Merchants give you a fair price on the first offer.",
                "Guards are slow to draw weapons on you.",
                "You can talk your way into most gatherings.",
                "People remember your name kindly.",
                "Strangers tell you more than they meant to.",
                "Your threats are believed, even when they should not be.",
                "Rivals find it hard to stay angry with you.",
                "Leaders listen when you ask for a moment of their time.",
            },
        };

        public class KeystoneDef
        {
            public string name, rule, ability;
            public KeystoneDef(string n, string r, string a) { name = n; rule = r; ability = a; }
        }

        /// <summary>
        /// Keystones: a large upside paired with a real constraint the narrator enforces. The
        /// ability name seeds the usable GameAbility minted when the keystone is allocated.
        /// </summary>
        public static readonly Dictionary<string, KeystoneDef[]> Keystones = new Dictionary<string, KeystoneDef[]>
        {
            ["Strength"] = new[]
            {
                new KeystoneDef("Pyre Oath", "While wounded, your strikes land as decisive successes, but you can never choose to flee a fight.", "Ignite the Oath"),
                new KeystoneDef("Anvil Born", "Nothing short of a killing blow moves you from where you stand, and you can no longer move quietly.", "Stand Unbroken"),
                new KeystoneDef("Last Wall", "Allies beside you cannot be struck first, but you can never leave a companion behind.", "Hold the Line"),
                new KeystoneDef("Unchained", "You can break any restraint that holds you, but delicate work is beyond your hands.", "Break Free"),
            },
            ["Dexterity"] = new[]
            {
                new KeystoneDef("Never Where They Strike", "Attacks against you miss unless you are cornered, but heavy armour slows you as though it weighed twice as much.", "Gale Step"),
                new KeystoneDef("Hawk's Vow", "You never miss a target you have watched for a full turn, but you cannot fight at arm's length.", "Marked Shot"),
                new KeystoneDef("Quicksilver", "You always act before anyone else in a fight, but you cannot hold still long enough to rest.", "Blur"),
                new KeystoneDef("Thread the Needle", "You can pass through any gap your body fits, but you cannot carry anything bulky.", "Slip Through"),
            },
            ["Intellect"] = new[]
            {
                new KeystoneDef("Unbound Theorem", "Your talents succeed on intent rather than method, but every use leaves a lasting mark on the world.", "Recite the Theorem"),
                new KeystoneDef("Perfect Recall", "You forget nothing you have witnessed, including every wound and every insult.", "Replay the Moment"),
                new KeystoneDef("Cold Logic", "Fear and charm have no hold on you, but you cannot read other people's feelings.", "Calculate"),
                new KeystoneDef("Open Book", "Any text or device reveals its purpose to you, but you cannot keep a secret of your own.", "Decipher"),
            },
            ["Cunning"] = new[]
            {
                new KeystoneDef("Nobody's Shadow", "Nobody remembers your face once you leave a scene, including the people you would rather be remembered by.", "Vanish"),
                new KeystoneDef("Loaded Fate", "Games of chance always go your way, but luck turns against you in any fair fight.", "Crooked Coin"),
                new KeystoneDef("Web of Debts", "Everyone you have helped owes you a favour they must repay, but you can never refuse a favour asked of you.", "Call in a Debt"),
                new KeystoneDef("Serpent's Tongue", "Your lies are always believed, but your plain truths are always doubted.", "Silver Lie"),
            },
            ["Charisma"] = new[]
            {
                new KeystoneDef("Voice of Crowns", "Strangers treat your word as binding, and so must you: you can no longer tell an outright lie.", "Royal Decree"),
                new KeystoneDef("Beloved", "Common folk will shelter and hide you, but any betrayal of them follows you everywhere.", "Call in a Favour"),
                new KeystoneDef("Magnetic", "Everyone in a room notices you the moment you enter, whether you want them to or not.", "Command the Room"),
                new KeystoneDef("Banner Bearer", "Allies fight on while you stand, but if you fall, they break and flee.", "Rally"),
            },
        };

        /// <summary>Legacy (v4) keystone names; still the AI refiner's fallback rule source.</summary>
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
