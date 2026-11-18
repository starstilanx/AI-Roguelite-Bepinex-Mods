using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AIROG_GenContext.DMNotes
{
    /// <summary>
    /// Finds the hidden &lt;DM_NOTES&gt; block in an AI response and cuts it out. Plain string code with
    /// no Unity or game types, so it can be exercised by a console harness.
    /// </summary>
    public static class DmNotesText
    {
        private const string OpenTag = "<DM_NOTES>";

        // "<\/DM_NOTES>" is the same tag with JSON's optional escaped slash.
        private static readonly Regex CloseTag =
            new Regex(@"<\\?/DM_NOTES>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] NoteKeys =
            { "player_state", "pacing", "engagement", "plot_threads", "preferences", "introduced" };

        private static readonly char[] KeyDecoration = { ' ', '\t', '*', '-', '"', '\'', '`' };

        /// <summary>"- **Plot Threads**" -> "plot_threads".</summary>
        public static string NormalizeKey(string rawKey)
            => rawKey.Trim(KeyDecoration).ToLowerInvariant().Replace(" ", "_").Replace("-", "_");

        /// <summary>
        /// Returns <paramref name="text"/> without its DM_NOTES block(s). <paramref name="notes"/> is
        /// null when there was no block, otherwise the block's lines separated by real newlines
        /// (possibly empty). <paramref name="jsonResponse"/>: the response is a UNIFIED JSON object.
        /// </summary>
        public static string Strip(string text, bool jsonResponse, out string notes)
        {
            notes = null;
            if (string.IsNullOrEmpty(text)) return text;

            StringBuilder all = null;
            for (int pass = 0; pass < 4; pass++)
            {
                int start = text.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                string found;
                text = StripOne(text, start, jsonResponse, out found);
                (all ?? (all = new StringBuilder())).Append(found).Append('\n');
            }
            if (all != null) notes = all.ToString();
            return text;
        }

        private static string StripOne(string text, int start, bool jsonResponse, out string notes)
        {
            // In a UNIFIED response the block normally sits inside the JSON "story" string. There a
            // line break is the two characters \n, and the string ends at the next bare quote.
            bool inJson = jsonResponse && IsInsideJsonString(text, start);
            int body = start + OpenTag.Length;
            int resume;

            Match close = CloseTag.Match(text, body);
            if (close.Success)
            {
                notes = Unescape(text.Substring(body, close.Index - body), inJson);
                resume = close.Index + close.Length;
            }
            else
            {
                resume = TakeNoteLines(text, body, inJson, out notes);
            }

            resume = SkipBreaks(text, resume, inJson);
            return text.Substring(0, start) + text.Substring(resume);
        }

        /// <summary>
        /// No closing tag: the model forgot it, or the response was cut off inside the block. Takes
        /// the "key: value" lines that follow the tag and stops at the first line that is not one,
        /// so story text after the block survives and none of the block is left on screen. Cutting
        /// to the end of the line instead (the old behaviour) left the note lines in a plain story
        /// and, inside a JSON string - where the whole rest of the object is one "line" - deleted
        /// the rest of the response. A last line that was cut off mid-way is removed but not
        /// reported: half a value cannot be trusted. Returns where the remaining text resumes.
        /// </summary>
        private static int TakeNoteLines(string text, int pos, bool inJson, out string notes)
        {
            var sb = new StringBuilder();
            int resume = SkipBreaks(text, pos, inJson);
            while (resume < text.Length)
            {
                int breakLen;
                int end = FindLineEnd(text, resume, inJson, out breakLen);
                string line = text.Substring(resume, end - resume);
                bool complete = breakLen > 0;

                if (IsNoteLine(line))
                {
                    resume = end;
                    if (!complete) break;
                    sb.Append(Unescape(line, inJson)).Append('\n');
                    resume = SkipBreaks(text, end + breakLen, inJson);
                    continue;
                }

                if (!complete && IsCutOffKey(line)) resume = end;
                break;
            }
            notes = sb.ToString();
            return resume;
        }

        /// <summary>Index of the line break (or closing quote, or end of text) that ends the line starting at <paramref name="from"/>.</summary>
        private static int FindLineEnd(string text, int from, bool inJson, out int breakLen)
        {
            for (int i = from; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r') { breakLen = 1; return i; }
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[i + 1];
                    if (n == 'n' || n == 'r') { breakLen = 2; return i; }
                    if (inJson) i++; // an escaped quote or backslash does not end the string
                    continue;
                }
                if (inJson && c == '"') { breakLen = 0; return i; }
            }
            breakLen = 0;
            return text.Length;
        }

        private static int SkipBreaks(string text, int pos, bool inJson)
        {
            while (pos < text.Length)
            {
                char c = text[pos];
                if (c == '\n' || c == '\r' || c == ' ' || c == '\t') { pos++; continue; }
                if (inJson && c == '\\' && pos + 1 < text.Length && (text[pos + 1] == 'n' || text[pos + 1] == 'r'))
                {
                    pos += 2;
                    continue;
                }
                break;
            }
            return pos;
        }

        private static bool IsNoteLine(string line)
        {
            int colon = line.IndexOf(':');
            return colon > 0 && Array.IndexOf(NoteKeys, NormalizeKey(line.Substring(0, colon))) >= 0;
        }

        /// <summary>The start of a key with nothing after it ("pac"): the response ended mid-key.</summary>
        private static bool IsCutOffKey(string line)
        {
            string fragment = NormalizeKey(line);
            if (fragment.Length < 2 || fragment.IndexOf(':') >= 0) return false;
            foreach (string key in NoteKeys)
                if (key.StartsWith(fragment, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>True when <paramref name="pos"/> lies inside a string literal of the JSON object in <paramref name="text"/>.</summary>
        private static bool IsInsideJsonString(string text, int pos)
        {
            int brace = text.IndexOf('{');
            if (brace < 0 || brace > pos) return false;

            bool inString = false, escape = false;
            for (int i = brace; i < pos; i++)
            {
                char c = text[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') inString = !inString;
            }
            return inString;
        }

        private static string Unescape(string s, bool inJson)
        {
            if (s.IndexOf('\\') < 0) return s;
            if (!inJson) return s.Replace("\\n", "\n").Replace("\\r", "");

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }

                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': break;
                    case 't': sb.Append(' '); break;
                    case 'u':
                        ushort code;
                        if (i + 4 < s.Length && ushort.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else sb.Append("\\u");
                        break;
                    default: sb.Append(n); break; // \" \\ \/
                }
            }
            return sb.ToString();
        }
    }
}
