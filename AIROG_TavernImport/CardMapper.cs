using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIROG_TavernImport
{
    /// <summary>The three native ImportantCharacterData fields a card maps onto.</summary>
    public class MappedProfile
    {
        public string Visual = "";
        public string Personality = "";
        public string Background = "";
    }

    /// <summary>
    /// Verbatim card-to-profile mapping. ST cards have no separate appearance field, so the
    /// visual description is pulled from a labelled section (plain "Appearance:" lines or W++
    /// "Appearance(...)") when one exists, else the opening paragraph of the description.
    /// </summary>
    public static class CardMapper
    {
        static readonly string[] VisualLabels =
        {
            "appearance", "physical appearance", "physical description", "looks", "body",
            "features", "physical features", "clothing", "clothes", "outfit", "attire", "wearing",
        };

        static readonly string[] PersonalityLabels = { "personality", "traits", "personality traits", "mind" };

        public static MappedProfile Map(TavernCard card, string playerName)
        {
            string name = card.Name;
            string desc = Macros(card.Description, name, playerName).Trim();
            string personality = Macros(card.Personality, name, playerName).Trim();
            string scenario = Macros(card.Scenario, name, playerName).Trim();
            string examples = CleanExamples(Macros(card.MesExample, name, playerName));

            var p = new MappedProfile();

            string visual = ExtractSections(desc, VisualLabels);
            if (visual.Length == 0) visual = FirstParagraph(desc);
            p.Visual = Truncate(visual, TavernImportPlugin.MaxVisualChars.Value);

            if (personality.Length == 0) personality = ExtractSections(desc, PersonalityLabels);
            if (TavernImportPlugin.IncludeExampleDialogue.Value && examples.Length > 0)
            {
                personality = (personality.Length > 0 ? personality + "\n\n" : "")
                    + "Speech style examples:\n" + Truncate(examples, TavernImportPlugin.MaxExampleChars.Value);
            }
            p.Personality = personality;

            var bg = new StringBuilder(desc);
            if (scenario.Length > 0)
                bg.Append(bg.Length > 0 ? "\n\n" : "").Append("Current situation: ").Append(scenario);
            p.Background = Truncate(bg.ToString(), TavernImportPlugin.MaxBackgroundChars.Value);
            return p;
        }

        /// <summary>A flat text rendering of the card, handed to the game's own profile generator
        /// in AI-adapted mode.</summary>
        public static string ToPromptText(TavernCard card, string playerName)
        {
            string n = card.Name;
            var sb = new StringBuilder();
            void Add(string label, string raw)
            {
                string v = Macros(raw, n, playerName).Trim();
                if (v.Length > 0) sb.Append(label).Append(":\n").Append(v).Append("\n\n");
            }
            Add("Name", n);
            Add("Description", card.Description);
            Add("Personality", card.Personality);
            Add("Scenario", card.Scenario);
            Add("Example dialogue", CleanExamples(card.MesExample));
            return Truncate(sb.ToString().Trim(), 8000);
        }

        /// <summary>SillyTavern macro substitution. {{user}} becomes the current player name so the
        /// text reads naturally in this game; ST comment macros {{// ...}} are dropped.</summary>
        public static string Macros(string s, string charName, string playerName)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string user = string.IsNullOrWhiteSpace(playerName) ? "the player" : playerName;
            string ch = string.IsNullOrWhiteSpace(charName) ? "the character" : charName;
            s = Regex.Replace(s, @"\{\{//[\s\S]*?\}\}", "");
            s = Regex.Replace(s, @"\{\{\s*(char|bot)\s*\}\}|<BOT>|<CHAR>", ch, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\{\{\s*user\s*\}\}|<USER>", user, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\\([\[\]\-\*_#()])", "$1"); // markdown escapes: \[ \- \* ...
            return s.Replace("\r\n", "\n");
        }

        /// <summary>Card sites decorate names ("-= Starflight =- ( Wings of Fire )"). Strips
        /// parenthetical/bracketed asides and edge punctuation; keeps the raw name if nothing is left.</summary>
        public static string CleanName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = Regex.Replace(raw, @"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", " ");
            s = Regex.Replace(s, @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}.']+$", "");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s.Length > 0 ? s : raw.Trim();
        }

        static string CleanExamples(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = Regex.Replace(s, @"<START>", "", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        }

        /// <summary>Collects labelled sections: "Label: text" (until a blank line or the next
        /// "Label:" line) and W++ "Label(\"a\" + \"b\")" / "Label = [a, b]" forms.</summary>
        static string ExtractSections(string text, string[] labels)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var found = new List<string>();
            string alt = string.Join("|", labels.Select(Regex.Escape));

            foreach (Match m in Regex.Matches(text, $@"(?im)\b({alt})\s*(\(|=\s*\[)\s*([^)\]]*)[)\]]"))
            {
                string inner = Regex.Replace(m.Groups[3].Value, "\"\\s*\\+\\s*\"", ", ").Replace("\"", "").Trim();
                if (inner.Length > 0) found.Add(Capitalise(m.Groups[1].Value) + ": " + inner);
            }

            // "Label: text" and "Label=text" lines (but not W++ "Label = [..]", handled above).
            const string sep = @"\s*(?::|=(?!\s*\[))\s*";
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], $@"^\s*[\*#\-\[]*\s*({alt})\s*[\*\]]*{sep}(.*)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var sb = new StringBuilder(m.Groups[2].Value.Trim());
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string next = lines[j].Trim();
                    if (next.Length == 0 || Regex.IsMatch(next, $@"^[\*#\-\[]*\s*[A-Za-z][A-Za-z ']{{0,30}}[\*\]]*{sep}")) break;
                    sb.Append(sb.Length > 0 ? " " : "").Append(next);
                    i = j;
                }
                if (sb.Length > 0) found.Add(Capitalise(m.Groups[1].Value) + ": " + sb);
            }
            return string.Join("\n", found.Distinct());
        }

        static string FirstParagraph(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string para = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            return para;
        }

        static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

        /// <summary>Truncates at a sentence end when one falls in the last third, else a word.</summary>
        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || max <= 0 || s.Length <= max) return s ?? "";
            string cut = s.Substring(0, max);
            int sentence = Math.Max(cut.LastIndexOf(". "), Math.Max(cut.LastIndexOf(".\n"), cut.LastIndexOf("\n")));
            if (sentence > max * 2 / 3) return cut.Substring(0, sentence + 1).TrimEnd();
            int space = cut.LastIndexOf(' ');
            return (space > max / 2 ? cut.Substring(0, space) : cut).TrimEnd() + "...";
        }
    }
}
