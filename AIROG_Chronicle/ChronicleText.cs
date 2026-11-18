using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIROG_Chronicle
{
    /// <summary>Tunable limits. Filled from the BepInEx config each time they are needed.</summary>
    public class ChronicleSettings
    {
        public int RecentChapters = 5;
        public int ChaptersPerAct = 5;
        public int MaxActsInPrompt = 4;
        public int MaxRecalled = 2;
        public int MaxRecapChars = 400;
        public int MaxTokens = 1200;
        public string TagBlacklist = "";
        public bool FallbackBeats = true;
        public int FallbackBeatTurns = 3;
    }

    /// <summary>
    /// Parsing and matching helpers. Deliberately free of Unity and game types so the logic can be
    /// run outside the game.
    /// </summary>
    public static class ChronicleText
    {
        public const string OPEN_TAG = "<CHRONICLE_BEAT>";
        public const string CLOSE_TAG = "</CHRONICLE_BEAT>";

        public const int MIN_TAG_LENGTH = 3;
        public const int MAX_TAGS = 6;
        private const int MAX_TAG_LENGTH = 40;

        private static readonly Regex WhitespaceRx = new Regex(@"\s+", RegexOptions.Compiled);

        // "<\/CHRONICLE_BEAT>" is the same tag with JSON's optional escaped slash
        private static readonly Regex CloseTagRx = new Regex(@"<\\?/CHRONICLE_BEAT>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---- Hidden block removal ----

        /// <summary>
        /// Removes every &lt;CHRONICLE_BEAT&gt; block from an AI response and returns what is left.
        /// Complete blocks are added to <paramref name="blocks"/>. A block whose closing tag never
        /// arrived (the response hit its token limit) comes back in <paramref name="partial"/>.
        /// <paramref name="json"/>: the response is a JSON document (unified pipeline), so line
        /// breaks inside it are the two characters \n and the block may sit inside a string.
        /// </summary>
        public static string StripBeatBlocks(string text, bool json, List<string> blocks, out string partial)
        {
            partial = null;
            if (string.IsNullOrEmpty(text)) return text;

            for (int guard = 0; guard < 32; guard++)
            {
                int start = text.IndexOf(OPEN_TAG, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                int bodyStart = start + OPEN_TAG.Length;
                Match close = CloseTagRx.Match(text, bodyStart);

                if (close.Success)
                {
                    blocks?.Add(Unescape(text.Substring(bodyStart, close.Index - bodyStart), json));
                    string after = text.Substring(close.Index + close.Length).TrimStart('\n', '\r', ' ');
                    text = text.Substring(0, start) + after;
                    continue;
                }

                // Unclosed. Removing only the tag's own line would leave "event_type: ... / summary: ..."
                // behind as story text, so take every following line that still reads as block content.
                int cut = EndOfUnclosedBlock(text, bodyStart, json);
                partial = Unescape(text.Substring(bodyStart, cut - bodyStart), json);
                text = text.Substring(0, start) + text.Substring(cut);
                break;
            }
            return text;
        }

        // Same shape the game itself treats as a mod's hidden block (Utils.FindModTagBlocks)
        private static readonly Regex AnyModBlockRx = new Regex(@"<([A-Z][A-Z0-9_]{2,})>[\s\S]*?</\1>", RegexOptions.Compiled);

        /// <summary>
        /// Story text without other mods' hidden blocks. Which interceptor sees the reply first is
        /// down to patch order, so the copy Chronicle keeps for its own summaries can still hold them.
        /// </summary>
        public static string WithoutModBlocks(string story)
            => string.IsNullOrEmpty(story) ? story : AnyModBlockRx.Replace(story, "").Trim();

        private static string Unescape(string block, bool json)
        {
            block = block.Replace("\\n", "\n").Replace("\\r", "").Replace("\r", "");
            if (json) block = block.Replace("\\\"", "\"").Replace("\\/", "/");
            return block;
        }

        private static readonly string[] TruncatedTails =
            { "event_type:", "event type:", "summary:", "is_milestone:", "is milestone:", "</chronicle_beat>" };

        private static readonly Regex BeatKeyAtLineStartRx = new Regex(
            @"^[\s\-\*•>""\[]*(event[_ ]?type|summary|is[_ ]?milestone)[ \t]*[:=]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Index just past the last line after <paramref name="pos"/> that belongs to an unclosed block.</summary>
        private static int EndOfUnclosedBlock(string text, int pos, bool json)
        {
            int consumedEnd = pos;
            int i = pos;
            while (i < text.Length)
            {
                int lineEnd = text.Length, next = text.Length;
                bool last = true, hitQuote = false;
                for (int j = i; j < text.Length; j++)
                {
                    char c = text[j];
                    if (c == '\n') { lineEnd = j; next = j + 1; last = false; break; }
                    if (!json) continue;
                    if (c == '\\' && j + 1 < text.Length)
                    {
                        if (text[j + 1] == 'n') { lineEnd = j; next = j + 2; last = false; break; }
                        j++; // an escaped character (\" or \\) is part of the line
                        continue;
                    }
                    // End of the JSON string the block was written into: everything from here on is JSON again.
                    if (c == '"') { lineEnd = j; next = j; last = false; hitQuote = true; break; }
                }

                string line = text.Substring(i, lineEnd - i).Replace("\\r", "").Trim();
                bool blockish = line.Length == 0
                             || BeatKeyAtLineStartRx.IsMatch(line)
                             || ((last || hitQuote) && IsTruncatedTail(line));
                if (!blockish) break;

                // A blank line is only taken along with a block line that follows it.
                if (line.Length > 0) consumedEnd = hitQuote ? lineEnd : next;
                if (last || hitQuote) break;
                i = next;
            }
            return consumedEnd;
        }

        private static bool IsTruncatedTail(string line)
        {
            string l = line.ToLowerInvariant().Replace("\\/", "/");
            foreach (string tail in TruncatedTails)
                if (tail.StartsWith(l, StringComparison.Ordinal)) return true;
            return false;
        }

        // ---- Beat block parsing ----

        private static readonly Regex BeatKeyRx = new Regex(
            @"(?<![\p{L}\p{N}_])(event[_ ]?type|summary|is[_ ]?milestone)[ \t]*[:=]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Reads event_type / summary / is_milestone out of a block, whether the model wrote one key
        /// per line or ran them together on a single line.
        /// <paramref name="summaryComplete"/> is false when the text stops in the middle of the
        /// summary (only possible for a block that was cut off).
        /// </summary>
        public static bool TryParseBeat(string block, out string eventType, out string summary,
            out bool milestone, out bool summaryComplete)
        {
            eventType = null; summary = null; milestone = false; summaryComplete = false;
            if (string.IsNullOrWhiteSpace(block)) return false;
            block = block.Replace("\r", "");

            var keys = new List<Match>();
            var seen = new HashSet<string>();
            foreach (Match m in BeatKeyRx.Matches(block))
            {
                string key = NormalizeKey(m.Groups[1].Value);
                // A key name in the middle of a sentence is prose ("...the council's summary: war"),
                // unless that key has not appeared yet (a one-line block has every key mid-line).
                if (seen.Contains(key) && !AtLineStart(block, m.Index)) continue;
                seen.Add(key);
                keys.Add(m);
            }

            for (int i = 0; i < keys.Count; i++)
            {
                Match m = keys[i];
                int from = m.Index + m.Length;
                int to = i + 1 < keys.Count ? keys[i + 1].Index : block.Length;
                string raw = block.Substring(from, to - from);
                string value = CleanValue(raw);
                switch (NormalizeKey(m.Groups[1].Value))
                {
                    case "eventtype":
                        eventType = value;
                        break;
                    case "ismilestone":
                        milestone = value.StartsWith("true", StringComparison.OrdinalIgnoreCase)
                                 || value.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "summary":
                        summary = value;
                        summaryComplete = i + 1 < keys.Count
                                       || raw.TrimEnd(' ', '\t').EndsWith("\n", StringComparison.Ordinal)
                                       || EndsLikeSentence(value);
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(summary)) { summary = null; return false; }
            // The model copied the format line instead of filling it in.
            if (summary.StartsWith("One sentence", StringComparison.OrdinalIgnoreCase))
            {
                summary = null;
                return false;
            }
            return true;
        }

        /// <summary>True if the text contains any of the block's key labels.</summary>
        public static bool HasBeatKeys(string text) => !string.IsNullOrEmpty(text) && BeatKeyRx.IsMatch(text);

        public static BeatType ParseBeatType(string value)
        {
            string v = new string((value ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant();
            if (v.StartsWith("combat")) return BeatType.Combat;
            if (v.StartsWith("arrival")) return BeatType.Arrival;
            if (v.StartsWith("death")) return BeatType.Death;
            if (v.StartsWith("level")) return BeatType.LevelUp;
            if (v.StartsWith("quest")) return BeatType.Quest;
            return BeatType.Narrative;
        }

        private static string NormalizeKey(string key)
            => key.Replace(" ", "").Replace("_", "").ToLowerInvariant();

        private static bool AtLineStart(string text, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '\n') return true;
                if (!char.IsWhiteSpace(c) && "-*•>\"[".IndexOf(c) < 0) return false;
            }
            return true;
        }

        private static bool EndsLikeSentence(string value)
            => value.Length > 0 && ".!?…\"”')]。！？".IndexOf(value[value.Length - 1]) >= 0;

        /// <summary>First non-empty line of a value, without the brackets/quotes models like to wrap it in.</summary>
        private static string CleanValue(string raw)
        {
            string v = null;
            foreach (string line in raw.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) { v = t; break; }
            }
            if (v == null) return "";
            v = v.Trim('*', ' ', '\t');
            while (v.Length >= 2)
            {
                char a = v[0], b = v[v.Length - 1];
                bool wrapped = (a == '[' && b == ']') || (a == '`' && b == '`')
                            || (a == '"' && b == '"' && v.IndexOf('"', 1) == v.Length - 1)
                            || (a == '“' && b == '”' && v.IndexOf('“', 1) < 0);
                if (!wrapped) break;
                v = v.Substring(1, v.Length - 2).Trim();
            }
            return v;
        }

        // ---- Chapter title / recap / tags response ----

        private static readonly Regex ChapterKeyRx = new Regex(
            @"^[\W\d_]*(title|recap|summary|tags|keywords)\b[\s\*_]*:[\s\*_]*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Parses the "Title: / Recap: / Tags:" reply. Tolerates markdown ("**Title:** ..."), list
        /// numbering, a recap that wraps over several lines, and tags listed one per line.
        /// <paramref name="recap"/> is empty when the reply held nothing usable.
        /// </summary>
        public static void ParseChapterResponse(string resp, int chapterNumber,
            out string title, out string recap, out List<string> tags)
        {
            title = $"Chapter {chapterNumber}";
            recap = "";
            tags = new List<string>();
            if (string.IsNullOrWhiteSpace(resp)) return;

            string current = null, titleVal = null, tagLine = null;
            var recapSb = new StringBuilder();
            var loose = new StringBuilder();

            foreach (string rawLine in resp.Replace("\r", "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                Match m = ChapterKeyRx.Match(line);
                if (m.Success)
                {
                    string key = m.Groups[1].Value.ToLowerInvariant();
                    string val = m.Groups[2].Value.Trim();
                    if (key == "title") { titleVal = val; current = "title"; }
                    else if (key == "tags" || key == "keywords") { tagLine = val; current = "tags"; }
                    else
                    {
                        if (recapSb.Length > 0 && val.Length > 0) recapSb.Append(' ');
                        recapSb.Append(val);
                        current = "recap";
                    }
                    continue;
                }

                if (current == "recap") { if (recapSb.Length > 0) recapSb.Append(' '); recapSb.Append(line); }
                else if (current == "tags") tagLine = string.IsNullOrEmpty(tagLine) ? line : tagLine + ", " + line;
                else if (current == "title" && string.IsNullOrEmpty(titleVal)) titleVal = line;
                else { if (loose.Length > 0) loose.Append(' '); loose.Append(line); }
            }

            string t = StripWrapping(titleVal);
            if (t.Length > 0 && !t.StartsWith("a dramatic chapter title", StringComparison.OrdinalIgnoreCase))
                title = Clamp(t, 80);

            string r = StripWrapping(recapSb.Length > 0 ? recapSb.ToString() : loose.ToString());
            if (!r.StartsWith("a 1-2 sentence summary", StringComparison.OrdinalIgnoreCase))
                recap = WhitespaceRx.Replace(r, " ").Trim();

            tags = ParseTagList(tagLine);
        }

        /// <summary>Cleans a one-paragraph AI reply (act summary) of labels and wrapping quotes.</summary>
        public static string CleanSummaryReply(string resp, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(resp)) return "";
            string s = WhitespaceRx.Replace(resp, " ").Trim();
            s = Regex.Replace(s, @"^[\W_]*(act\s+summary|summary)\b[\s\*_]*:[\s\*_]*", "", RegexOptions.IgnoreCase);
            return Clamp(StripWrapping(s), maxChars);
        }

        private static string StripWrapping(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim().Trim('*', '#', '_', ' ', '\t');
            if (s.Length >= 2)
            {
                char a = s[0], b = s[s.Length - 1];
                if ((a == '[' && b == ']') || (a == '“' && b == '”' && s.IndexOf('“', 1) < 0)
                    || (a == '"' && b == '"' && s.IndexOf('"', 1) == s.Length - 1))
                    s = s.Substring(1, s.Length - 2).Trim();
            }
            return s;
        }

        // ---- Tags ----

        public static List<string> ParseTagList(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(line)) return result;
            foreach (string part in line.Split(',', ';', '|', '\n', '、', '，'))
            {
                string t = CleanTag(part);
                if (t == null) continue;
                if (result.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(t);
                if (result.Count >= MAX_TAGS) break;
            }
            return result;
        }

        public static string CleanTag(string raw)
        {
            if (raw == null) return null;
            string t = raw.Trim().Trim('"', '\'', '“', '”', '‘', '’', '*', '[', ']', '(', ')', '#', '-', '•', '.', ' ', '\t');
            // "the Rusty Anchor" should also match a plain "Rusty Anchor"
            foreach (string article in new[] { "the ", "a ", "an " })
                if (t.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { t = t.Substring(article.Length).Trim(); break; }
            if (t.Length < MIN_TAG_LENGTH || t.Length > MAX_TAG_LENGTH) return null;
            return t;
        }

        /// <summary>
        /// Tags that must never trigger a recall: the player's list, plus the player character's
        /// name and each word of it (the name is in every prompt, so it would match every turn).
        /// </summary>
        public static HashSet<string> BuildBlacklist(string configList, string playerName)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(configList))
                foreach (string part in configList.Split(',', ';', '|', '\n'))
                {
                    string t = part.Trim();
                    if (t.Length > 0) set.Add(t);
                }
            if (!string.IsNullOrWhiteSpace(playerName))
            {
                string name = playerName.Trim();
                set.Add(name);
                foreach (string word in name.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string w = word.Trim(',', '.', '"', '\'');
                    if (w.Length >= MIN_TAG_LENGTH) set.Add(w);
                }
            }
            return set;
        }

        /// <summary>
        /// Whole-word, case-insensitive search. <paramref name="haystackLower"/> must already be
        /// lower-cased (callers scan many tags against one text). Tags under 3 characters never match.
        /// </summary>
        public static bool ContainsTag(string haystackLower, string tag)
        {
            if (string.IsNullOrEmpty(haystackLower) || tag == null) return false;
            string t = tag.Trim().ToLowerInvariant();
            if (t.Length < MIN_TAG_LENGTH) return false;
            return ContainsWholeWord(haystackLower, t);
        }

        /// <summary>Case-sensitive whole-word search.</summary>
        private static bool ContainsWholeWord(string haystack, string word)
        {
            bool checkLeft = NeedsBoundary(word[0]), checkRight = NeedsBoundary(word[word.Length - 1]);
            int idx = 0;
            while ((idx = haystack.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
            {
                int end = idx + word.Length;
                bool leftOk = !checkLeft || idx == 0 || !IsWordChar(haystack[idx - 1]);
                bool rightOk = !checkRight || end >= haystack.Length || !IsWordChar(haystack[end]);
                if (leftOk && rightOk) return true;
                idx++;
            }
            return false;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        // Scripts written without spaces (CJK) have no word boundaries to test.
        private static bool NeedsBoundary(char edge) => IsWordChar(edge) && edge < '⺀';

        private static readonly Regex ProperNounRx = new Regex(
            @"\p{Lu}[\p{L}\p{M}'’\-]*(?:[ ]+(?:(?:of|the|de|del|la|le|van|von|du|da|al|el)[ ]+)*\p{Lu}[\p{L}\p{M}'’\-]*)*",
            RegexOptions.Compiled);

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "you", "your", "yours", "he", "she", "they", "it", "we", "i", "his", "her", "him",
            "them", "their", "its", "our", "my", "in", "on", "at", "to", "from", "for", "by", "with", "as", "of",
            "and", "but", "or", "then", "there", "this", "that", "these", "those", "after", "before", "when",
            "while", "once", "now", "later", "soon", "meanwhile", "suddenly", "finally", "having", "if", "so",
            "yet", "still", "despite", "during", "upon", "into", "inside", "outside", "back", "here", "what",
            "who", "why", "how", "not", "no", "one", "two", "three", "some", "all", "both", "each", "every",
            "another", "reached", "gained", "level", "chapter", "act", "turn", "de", "del", "la", "le", "van",
            "von", "du", "da", "al", "el"
        };

        private class TagCandidate
        {
            public string Text;
            public int Count, MidSentence, First;
        }

        /// <summary>
        /// Backfill for chapters the AI never tagged: the most frequent capitalised names in the
        /// given texts. A single word counts if it was seen capitalised in the middle of a
        /// sentence, or if it opens two or more sentences and is never written in lower case
        /// anywhere ("Marta arrives. Marta leaves." yes; "Night falls ... that night" no), so
        /// ordinary sentence-opening words do not become tags.
        /// </summary>
        public static List<string> HeuristicTags(IEnumerable<string> texts, ICollection<string> exclude, int max = MAX_TAGS)
        {
            var found = new Dictionary<string, TagCandidate>(StringComparer.OrdinalIgnoreCase);
            var all = new StringBuilder();
            int order = 0;
            foreach (string text in texts ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                all.AppendLine(text);
                foreach (Match m in ProperNounRx.Matches(text))
                {
                    var words = m.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    bool sentenceStart = IsSentenceStart(text, m.Index);
                    while (words.Count > 0 && StopWords.Contains(StripPossessive(words[0])))
                    {
                        words.RemoveAt(0);
                        sentenceStart = false; // what is left no longer opens the sentence
                    }
                    while (words.Count > 0 && StopWords.Contains(StripPossessive(words[words.Count - 1])))
                        words.RemoveAt(words.Count - 1);
                    if (words.Count == 0) continue;
                    words[words.Count - 1] = StripPossessive(words[words.Count - 1]);

                    string phrase = string.Join(" ", words).Trim('-', '\'', '’');
                    if (phrase.Length < MIN_TAG_LENGTH || phrase.Length > MAX_TAG_LENGTH) continue;
                    if (exclude != null && exclude.Contains(phrase)) continue;

                    if (!found.TryGetValue(phrase, out TagCandidate c))
                        found[phrase] = c = new TagCandidate { Text = phrase, First = order++ };
                    c.Count++;
                    if (!sentenceStart || words.Count > 1) c.MidSentence++;
                }
            }

            string allText = all.ToString();
            var chosen = new List<string>();
            foreach (TagCandidate c in found.Values
                         .Where(c => c.MidSentence > 0 || (c.Count >= 2 && !ContainsWholeWord(allText, c.Text.ToLowerInvariant())))
                         .OrderByDescending(c => c.Count).ThenBy(c => c.First))
            {
                if (chosen.Count >= max) break;
                // "Old Marta" adds nothing once "Marta" is in (and the other way round)
                if (chosen.Any(x => SharesWord(x, c.Text))) continue;
                chosen.Add(c.Text);
            }
            return chosen;
        }

        private static string StripPossessive(string word)
        {
            if (word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
                return word.Substring(0, word.Length - 2);
            return word;
        }

        private static bool IsSentenceStart(string text, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c == '"' || c == '“' || c == '\'' || c == '(') continue;
                return c == '.' || c == '!' || c == '?' || c == ':' || c == ';' || c == '…';
            }
            return true;
        }

        private static bool SharesWord(string a, string b)
        {
            foreach (string wa in a.Split(' '))
            {
                if (wa.Length < MIN_TAG_LENGTH || StopWords.Contains(wa)) continue;
                foreach (string wb in b.Split(' '))
                    if (string.Equals(wa, wb, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---- Misc ----

        /// <summary>Single-line version of <paramref name="text"/>, cut at a word boundary to at most <paramref name="maxChars"/>.</summary>
        public static string Clamp(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text) || maxChars <= 0) return "";
            string t = WhitespaceRx.Replace(text, " ").Trim();
            if (t.Length <= maxChars) return t;
            if (maxChars < 2) return t.Substring(0, maxChars);
            int cut = t.LastIndexOf(' ', maxChars - 2);
            if (cut < maxChars / 2) cut = maxChars - 1;
            return t.Substring(0, cut).TrimEnd(' ', ',', ';', ':') + "…";
        }
    }

    /// <summary>
    /// Decides what part of the chronicle goes into the prompt. Also free of game types.
    ///
    /// What is sent, in reading order:
    ///   acts      - the newest few act summaries (older chapters, condensed)
    ///   recalled  - recaps of older chapters whose tags show up in the current scene
    ///   recent    - recaps of the newest closed chapters, always
    ///   current   - the latest beats of the open chapter
    ///   the hidden-beat instruction
    /// Every count is capped by a setting and every entry by a character limit, so the size has a
    /// fixed ceiling however long the save runs. The caller's budget applies on top.
    /// </summary>
    public static class ChronicleComposer
    {
        public const int MAX_BEATS_SHOWN = 10;

        public static readonly string BeatInstruction = string.Join("\n", new[]
        {
            "[CHRONICLE INSTRUCTION: At the end of your response, append a hidden block in exactly this format:]",
            "<CHRONICLE_BEAT>",
            "event_type: narrative",
            "summary: [One sentence describing the key event that just happened]",
            "is_milestone: false",
            "</CHRONICLE_BEAT>",
            "[Replace event_type with: combat, arrival, death, levelup, or quest if more fitting. Set is_milestone: true for major plot moments. This block will be stripped before the player sees the response.]"
        });

        private sealed class Plan
        {
            public readonly List<string> Acts = new List<string>();
            public readonly List<KeyValuePair<int, string>> Recalled = new List<KeyValuePair<int, string>>();
            public string Bridge;
            public readonly List<string> Recent = new List<string>();
            public string CurrentHeader;
            public readonly List<string> Beats = new List<string>();
            public bool Instruction;

            public string Render()
            {
                var sb = new StringBuilder();
                if (Acts.Count > 0) Section(sb, "[CHRONICLE — Earlier Acts]", Acts);
                if (Recalled.Count > 0)
                    Section(sb, "[CHRONICLE — Earlier Chapters Tied To This Scene]",
                        Recalled.OrderBy(kv => kv.Key).Select(kv => kv.Value));
                if (Recent.Count > 0 || Bridge != null)
                    Section(sb, "[CHRONICLE — Story So Far]", Bridge != null ? new[] { Bridge }.Concat(Recent) : Recent);
                if (Beats.Count > 0) Section(sb, CurrentHeader, Beats);
                if (Instruction)
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(BeatInstruction);
                }
                return sb.ToString();
            }

            private static void Section(StringBuilder sb, string header, IEnumerable<string> lines)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(header);
                foreach (string line in lines) sb.Append('\n').Append(line);
            }
        }

        /// <summary>
        /// Builds the injected text. Never longer than <paramref name="maxChars"/>: each piece is
        /// added only if the whole still fits, most important first (instruction, current beats,
        /// recent chapters, recalled chapters, acts), newest first inside each group. As soon as
        /// one piece does not fit, everything less important is left out as well, so a tight
        /// budget always sheds the oldest material first.
        /// </summary>
        public static string Compose(ChronicleState state, string sceneText, int maxChars,
            ChronicleSettings s, string playerName)
        {
            if (state == null || s == null || maxChars <= 0) return "";

            var plan = new Plan();
            int gistMax = Math.Max(80, s.MaxRecapChars);

            // All or nothing: half an instruction is worse than none.
            plan.Instruction = true;
            if (plan.Render().Length > maxChars) return "";
            bool full = false;

            Chapter cur = state.CurrentChapter;
            if (cur?.Beats != null)
            {
                plan.CurrentHeader = $"[CHRONICLE — Current Chapter (Turn {cur.StartTurn}+)]";
                for (int i = cur.Beats.Count - 1; i >= 0 && plan.Beats.Count < MAX_BEATS_SHOWN; i--)
                {
                    ChronicleBeat b = cur.Beats[i];
                    if (b == null || string.IsNullOrWhiteSpace(b.Summary)) continue;
                    plan.Beats.Insert(0, $"T{b.Turn}: {ChronicleText.Clamp(b.Summary, gistMax)}{(b.IsMilestone ? " ★" : "")}");
                    if (plan.Render().Length > maxChars) { plan.Beats.RemoveAt(0); full = true; break; }
                }
            }

            List<Chapter> closed = state.ClosedChapters ?? new List<Chapter>();
            int firstRecent = FirstRecentIndex(closed, s.RecentChapters);

            for (int i = closed.Count - 1; i >= firstRecent && !full; i--)
            {
                string line = ChapterLine(closed[i], gistMax);
                if (line == null) continue;
                plan.Recent.Insert(0, line);
                if (plan.Render().Length > maxChars) { plan.Recent.RemoveAt(0); full = true; }
            }

            var recalledSet = new HashSet<Chapter>();
            if (!full)
            {
                var recalled = SelectRecalled(closed, firstRecent, sceneText,
                    ChronicleText.BuildBlacklist(s.TagBlacklist, playerName), s.MaxRecalled);
                foreach (Chapter ch in recalled)
                {
                    string line = ChapterLine(ch, gistMax);
                    if (line == null) continue;
                    var entry = new KeyValuePair<int, string>(closed.IndexOf(ch), line);
                    plan.Recalled.Add(entry);
                    if (plan.Render().Length > maxChars) { plan.Recalled.Remove(entry); full = true; break; }
                    recalledSet.Add(ch);
                }
            }

            if (!full && s.ChaptersPerAct > 0 && s.MaxActsInPrompt > 0 && state.Acts != null)
            {
                int firstRecentNumber = firstRecent < closed.Count ? closed[firstRecent].Number : int.MaxValue;
                foreach (ChronicleAct act in state.Acts.Where(a => a != null && a.LastChapter < firstRecentNumber)
                                                        .OrderByDescending(a => a.Number))
                {
                    if (plan.Acts.Count >= s.MaxActsInPrompt) break;
                    string line = ActLine(act, closed, gistMax);
                    if (line == null) continue;
                    plan.Acts.Insert(0, line);
                    if (plan.Render().Length > maxChars) { plan.Acts.RemoveAt(0); full = true; break; }
                }

                // Chapters that have left the recent window but are still waiting for enough
                // neighbours to form an act: keep at least their titles so the timeline has no hole.
                int lastActChapter = int.MinValue;
                foreach (ChronicleAct a in state.Acts)
                    if (a != null && a.LastChapter > lastActChapter) lastActChapter = a.LastChapter;
                var waiting = closed.Take(firstRecent)
                    .Where(c => c != null && c.Number > lastActChapter && !recalledSet.Contains(c) && HasRealTitle(c))
                    .Select(ChapterLabel).ToList();
                if (waiting.Count > 0 && !full)
                {
                    plan.Bridge = ChronicleText.Clamp("Before these: " + string.Join("; ", waiting.Skip(Math.Max(0, waiting.Count - 8))), gistMax);
                    if (plan.Render().Length > maxChars) plan.Bridge = null;
                }
            }

            string result = plan.Render();
            return result.Length <= maxChars ? result : "";
        }

        /// <summary>Index of the first chapter inside the always-injected window (== Count when the window is empty).</summary>
        public static int FirstRecentIndex(List<Chapter> closed, int recentChapters)
            => closed == null ? 0 : Math.Max(0, closed.Count - Math.Max(0, recentChapters));

        /// <summary>
        /// Older chapters (index below <paramref name="firstRecent"/>) with a tag present in the
        /// scene text, in order of preference: chapters holding a milestone beat first, then the
        /// most recent. At most <paramref name="max"/>.
        /// </summary>
        public static List<Chapter> SelectRecalled(List<Chapter> closed, int firstRecent, string sceneText,
            HashSet<string> blacklist, int max)
        {
            var result = new List<Chapter>();
            if (closed == null || max <= 0 || firstRecent <= 0 || string.IsNullOrEmpty(sceneText)) return result;

            string hay = sceneText.ToLowerInvariant();
            var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            for (int pass = 0; pass < 2 && result.Count < max; pass++)
            {
                for (int i = Math.Min(firstRecent, closed.Count) - 1; i >= 0 && result.Count < max; i--)
                {
                    Chapter ch = closed[i];
                    if (ch?.Tags == null || ch.Tags.Count == 0) continue;
                    if (HasMilestone(ch) != (pass == 0)) continue;
                    if (!HasGist(ch)) continue;

                    foreach (string tag in ch.Tags)
                    {
                        string t = tag?.Trim();
                        if (t == null || t.Length < ChronicleText.MIN_TAG_LENGTH) continue;
                        if (blacklist != null && blacklist.Contains(t)) continue;
                        if (!cache.TryGetValue(t, out bool hit)) cache[t] = hit = ChronicleText.ContainsTag(hay, t);
                        if (hit) { result.Add(ch); break; }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Groups chapters that have aged out of the recent window into acts of
        /// <paramref name="chaptersPerAct"/>, oldest first. Only the grouping is made here; the
        /// summary text is written later by an AI call. Returns how many acts were added.
        /// </summary>
        public static int FormActs(ChronicleState state, int recentChapters, int chaptersPerAct)
        {
            if (state?.ClosedChapters == null || chaptersPerAct <= 0) return 0;
            if (state.Acts == null) state.Acts = new List<ChronicleAct>();

            List<Chapter> closed = state.ClosedChapters;
            int eligible = FirstRecentIndex(closed, recentChapters);
            int formed = 0;
            while (true)
            {
                int lastCovered = int.MinValue, lastNumber = 0;
                foreach (ChronicleAct a in state.Acts)
                {
                    if (a == null) continue;
                    if (a.LastChapter > lastCovered) lastCovered = a.LastChapter;
                    if (a.Number > lastNumber) lastNumber = a.Number;
                }

                int start = closed.FindIndex(c => c != null && c.Number > lastCovered);
                if (start < 0 || start + chaptersPerAct > eligible) break;

                Chapter first = closed[start], last = closed[start + chaptersPerAct - 1];
                if (last == null) break;
                state.Acts.Add(new ChronicleAct
                {
                    Number = lastNumber + 1,
                    FirstChapter = first.Number,
                    // Max: guarantees progress even if a hand-edited file has chapters out of order
                    LastChapter = Math.Max(first.Number, last.Number),
                    StartTurn = first.StartTurn,
                    EndTurn = last.EndTurn
                });
                formed++;
            }
            return formed;
        }

        public static List<Chapter> ChaptersOf(ChronicleAct act, List<Chapter> closed)
        {
            if (act == null || closed == null) return new List<Chapter>();
            return closed.Where(c => c != null && c.Number >= act.FirstChapter && c.Number <= act.LastChapter).ToList();
        }

        // ---- Line building ----

        public static string ChapterLabel(Chapter ch)
            => string.IsNullOrEmpty(ch.Title) ? $"Chapter {ch.Number}" : $"Ch.{ch.Number} \"{ch.Title}\"";

        private static bool HasRealTitle(Chapter ch)
            => !string.IsNullOrWhiteSpace(ch.Title) && ch.Title != $"Chapter {ch.Number}";

        private static bool HasMilestone(Chapter ch)
            => ch.Beats != null && ch.Beats.Any(b => b != null && b.IsMilestone);

        private static bool HasGist(Chapter ch)
            => !string.IsNullOrWhiteSpace(ch.Recap)
            || (ch.Beats != null && ch.Beats.Any(b => b != null && !string.IsNullOrWhiteSpace(b.Summary)));

        /// <summary>
        /// The chapter in one line: its AI recap, or - while that call is still pending, or after
        /// it failed - its milestone beats (failing those, its last three beats). Null if empty.
        /// </summary>
        public static string ChapterGist(Chapter ch, int maxChars)
        {
            if (ch == null) return null;
            if (!string.IsNullOrWhiteSpace(ch.Recap)) return ChronicleText.Clamp(ch.Recap, maxChars);
            if (ch.Beats == null) return null;

            var usable = ch.Beats.Where(b => b != null && !string.IsNullOrWhiteSpace(b.Summary)).ToList();
            if (usable.Count == 0) return null;
            var picked = usable.Where(b => b.IsMilestone).ToList();
            if (picked.Count == 0) picked = usable.Skip(Math.Max(0, usable.Count - 3)).ToList();
            return ChronicleText.Clamp(string.Join(" ", picked.Select(b => b.Summary.Trim())), maxChars);
        }

        public static string ChapterLine(Chapter ch, int maxChars)
        {
            string gist = ChapterGist(ch, maxChars);
            return gist == null ? null : $"{ChapterLabel(ch)} (T{ch.StartTurn}–{ch.EndTurn}): {gist}";
        }

        /// <summary>The act in one line: its AI summary, or its chapter titles until that exists. Null if empty.</summary>
        public static string ActGist(ChronicleAct act, List<Chapter> closed, int maxChars)
        {
            if (act == null) return null;
            if (!string.IsNullOrWhiteSpace(act.Summary)) return ChronicleText.Clamp(act.Summary, maxChars);
            var titles = ChaptersOf(act, closed).Where(HasRealTitle).Select(c => $"\"{c.Title}\"").ToList();
            return titles.Count == 0 ? null : ChronicleText.Clamp(string.Join("; ", titles), maxChars);
        }

        public static string ActLabel(ChronicleAct act)
            => $"Act {act.Number} (Ch.{act.FirstChapter}–{act.LastChapter}, T{act.StartTurn}–{act.EndTurn})";

        public static string ActLine(ChronicleAct act, List<Chapter> closed, int maxChars)
        {
            string gist = ActGist(act, closed, maxChars);
            return gist == null ? null : $"{ActLabel(act)}: {gist}";
        }
    }
}
