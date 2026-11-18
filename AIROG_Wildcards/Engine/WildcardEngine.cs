using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AIROG_Wildcards.Engine
{
    /// <summary>One option of a wildcard file.</summary>
    public struct WildcardEntry
    {
        public string Text;
        public double Weight;

        public WildcardEntry(string text, double weight)
        {
            Text = text ?? "";
            Weight = weight;
        }
    }

    public interface IWildcardSource
    {
        /// <summary>
        /// The options of a wildcard (the name may contain * and ** globs), or null when no
        /// file matches. Must not throw.
        /// </summary>
        IList<WildcardEntry> Get(string name);
    }

    /// <summary>Optional report of one <see cref="WildcardEngine.Expand"/> call.</summary>
    public sealed class ExpansionInfo
    {
        /// <summary>Each top-level token of the template and the text it became.</summary>
        public readonly List<KeyValuePair<string, string>> Rolls = new List<KeyValuePair<string, string>>();

        /// <summary>Wildcard names that had no file (left untouched in the output).</summary>
        public readonly List<string> UnknownWildcards = new List<string>();

        /// <summary>True when the depth or size limit stopped part of the expansion.</summary>
        public bool HitLimit;
    }

    /// <summary>
    /// The commonly used core of the sd-dynamic-prompts template syntax: __wildcards__ (with
    /// subfolders and globs), {a|b} variants, weights, and pick-N with ranges and separators.
    /// No Unity or game types, so it runs headless under the test harness in Tests\.
    ///
    /// Two rules shape everything here:
    ///   - Output is never re-scanned. Each piece of source text is parsed exactly once, so text
    ///     that an expansion produces cannot be re-interpreted as syntax.
    ///   - Anything that is not unmistakably syntax is copied through unchanged. The host game's
    ///     prompts are full of JSON and {placeholder} tokens, so a {...} is only a variant when
    ///     it has a top-level | or $$, ${...} is never touched, and unbalanced braces are text.
    /// </summary>
    public sealed class WildcardEngine
    {
        public const int DefaultMaxDepth = 10;

        private const int MaxNameLength = 128;
        private const int MaxRecent = 5;

        // One Expand call may not resolve more tokens or produce more text than this. A wildcard
        // file that references itself several times per line would otherwise grow exponentially
        // inside the depth limit.
        private const int MaxResolutionsPerCall = 5000;
        private const int MaxOutputChars = 200000;

        private readonly IWildcardSource source;
        private readonly object stateGate = new object();
        private readonly Dictionary<string, History> history = new Dictionary<string, History>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> warnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public WildcardEngine(IWildcardSource source)
        {
            this.source = source;
        }

        /// <summary>How deep wildcards and variants may nest before the rest is left as written.</summary>
        public int MaxDepth = DefaultMaxDepth;

        /// <summary>
        /// When on, an unweighted wildcard hands out every one of its options once, in a fresh
        /// random order, before any option comes round again, and never repeats one of its last
        /// few picks. See <see cref="Draw"/>.
        /// </summary>
        public bool RepeatSuppression = true;

        /// <summary>Joins the picks of {2$$a|b|c} when no custom separator is given.</summary>
        public string Separator = ", ";

        /// <summary>
        /// When on, expansion never leaves a "${" or "{$" in the output that was not already
        /// written that way in the template. AI Roguelite rejects any prompt containing either
        /// sequence as an unreplaced variable, so a wildcard line such as "${colour}" (the A1111
        /// extension's variable syntax) would otherwise fail the whole request. A space is
        /// inserted between the two characters.
        /// </summary>
        public bool GuardPlaceholderMarkers;

        /// <summary>Receives one-time warnings (unknown wildcard names). May be null.</summary>
        public Action<string> Warn;

        /// <summary>Forgets repeat-suppression history and which unknown names were reported.</summary>
        public void ResetState()
        {
            lock (stateGate)
            {
                history.Clear();
                warnedNames.Clear();
            }
        }

        /// <summary>
        /// Cheap pre-check: false means <see cref="Expand"/> would return the text unchanged.
        /// True for any text containing "{", "__" or a backslash escape.
        /// </summary>
        public static bool MayContainSyntax(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{') return true;
                if (c == '_' && i + 1 < s.Length && s[i + 1] == '_') return true;
                if (c == '\\' && IsEscapeAt(s, i)) return true;
            }
            return false;
        }

        /// <summary>
        /// Expands a template. Returns the same string instance when nothing changed. Never
        /// writes to the template and never throws for bad syntax, missing files or recursion.
        /// </summary>
        public string Expand(string template, Random rng, ExpansionInfo info = null)
        {
            if (string.IsNullOrEmpty(template) || !MayContainSyntax(template)) return template;
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            Run run = new Run(this, rng, info, template.Length);
            run.ExpandRange(new Source(template, true), 0, template.Length, 0);
            string result = run.Result();
            return string.Equals(result, template, StringComparison.Ordinal) ? template : result;
        }

        // ---- source text ---------------------------------------------------------------------

        private sealed class Source
        {
            public readonly string S;

            /// <summary>True for the caller's template, false for a line of a wildcard file.</summary>
            public readonly bool IsRoot;

            private int[] match;

            public Source(string s, bool isRoot)
            {
                S = s;
                IsRoot = isRoot;
            }

            /// <summary>Index of the "}" closing the "{" at <paramref name="open"/>, or -1.</summary>
            public int Match(int open)
            {
                if (match == null) match = ComputeMatches(S);
                return match[open];
            }
        }

        private static int[] ComputeMatches(string s)
        {
            int[] match = new int[s.Length];
            Stack<int> open = new Stack<int>();
            for (int i = 0; i < s.Length; i++)
            {
                match[i] = -1;
                char c = s[i];
                if (c == '\\')
                {
                    if (IsEscapeAt(s, i))
                    {
                        i++;
                        match[i] = -1;
                    }
                }
                else if (c == '{')
                {
                    open.Push(i);
                }
                else if (c == '}' && open.Count > 0)
                {
                    match[open.Pop()] = i;
                }
            }
            return match;
        }

        /// <summary>
        /// A backslash before { } or | always escapes it. A backslash before _ only counts where
        /// that underscore touches another underscore, which is the only place it could form a
        /// wildcard token; "snake\_case" and similar keep their backslash.
        /// </summary>
        private static bool IsEscapeAt(string s, int i)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) return false;
            char next = s[i + 1];
            if (next == '{' || next == '}' || next == '|') return true;
            if (next != '_') return false;
            if (i > 0 && s[i - 1] == '_') return true;
            if (i + 2 < s.Length)
            {
                if (s[i + 2] == '_') return true;
                if (s[i + 2] == '\\' && i + 3 < s.Length && s[i + 3] == '_') return true;
            }
            return false;
        }

        private static bool IsNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '/' || c == '*' || c == '.'
                || c == ' ' || c == '\'' || c == '+' || c == '&';
        }

        private static bool IsNameEdge(char c)
        {
            return char.IsLetterOrDigit(c) || c == '*';
        }

        /// <summary>
        /// Reads a __name__ token starting at <paramref name="i"/>. The name ends at the first
        /// "__" after it, must start and end with a letter, digit or *, and cannot contain "..",
        /// so a token can never address a file outside the wildcard folder.
        /// </summary>
        private static bool TryReadWildcard(string s, int i, int end, out string name, out int tokenEnd)
        {
            name = null;
            tokenEnd = -1;
            int nameStart = i + 2;
            int j = nameStart;
            while (true)
            {
                if (j >= end) return false;
                char c = s[j];
                if (c == '_' && j + 1 < end && s[j + 1] == '_') break;
                if (!IsNameChar(c)) return false;
                j++;
                if (j - nameStart > MaxNameLength) return false;
            }
            int length = j - nameStart;
            if (length == 0) return false;
            if (!IsNameEdge(s[nameStart]) || !IsNameEdge(s[j - 1])) return false;
            string raw = s.Substring(nameStart, length);
            if (raw.IndexOf("..", StringComparison.Ordinal) >= 0 || raw.IndexOf("//", StringComparison.Ordinal) >= 0) return false;
            name = raw;
            tokenEnd = j + 2;
            return true;
        }

        /// <summary>
        /// True when the braces hold a JSON object ("key": ...), which is left alone even if one
        /// of its strings contains a | character.
        /// </summary>
        private static bool LooksLikeJsonObject(string s, int a, int b)
        {
            int k = a;
            while (k < b && char.IsWhiteSpace(s[k])) k++;
            if (k >= b || s[k] != '"') return false;
            k++;
            while (k < b && s[k] != '"')
            {
                if (s[k] == '\n' || s[k] == '|') return false;
                if (s[k] == '\\' && k + 1 < b) k++;
                k++;
            }
            if (k >= b) return false;
            k++;
            while (k < b && char.IsWhiteSpace(s[k])) k++;
            return k < b && s[k] == ':';
        }

        private static string Unescape(string s, int from, int to)
        {
            StringBuilder sb = new StringBuilder(to - from);
            for (int i = from; i < to; i++)
            {
                if (s[i] == '\\' && i + 1 < to && IsEscapeAt(s, i)) i++;
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>Parses the "2", "1-3", "-2" or "2-" in front of the first "$$".</summary>
        private static bool TryParseBound(string s, int from, int to, out int lo, out int hi, out bool hiOpen)
        {
            lo = 1;
            hi = 1;
            hiOpen = false;
            string text = s.Substring(from, to - from).Trim();
            if (text.Length == 0 || text.Length > 9) return false;
            int dash = text.IndexOf('-');
            if (dash < 0)
            {
                if (!TryParseCount(text, out lo)) return false;
                hi = lo;
                return true;
            }
            string left = text.Substring(0, dash).Trim();
            string right = text.Substring(dash + 1).Trim();
            if (left.Length == 0 && right.Length == 0) return false;
            if (left.Length > 0 && !TryParseCount(left, out lo)) return false;
            if (right.Length == 0) hiOpen = true;
            else if (!TryParseCount(right, out hi)) return false;
            return true;
        }

        private static bool TryParseCount(string text, out int value)
        {
            value = 0;
            if (text.Length == 0 || text.Length > 4) return false;
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private struct Option
        {
            public int A;
            public int B;
            public double Weight;
        }

        /// <summary>One option of a variant: surrounding whitespace dropped, optional "weight::" read.</summary>
        private static Option ParseOption(string s, int a, int b)
        {
            while (a < b && char.IsWhiteSpace(s[a])) a++;
            while (b > a && char.IsWhiteSpace(s[b - 1])) b--;
            Option option = new Option { A = a, B = b, Weight = 1.0 };

            int k = a;
            while (k < b && s[k] >= '0' && s[k] <= '9') k++;
            if (k < b && s[k] == '.')
            {
                k++;
                while (k < b && s[k] >= '0' && s[k] <= '9') k++;
            }
            if (k == a) return option;
            int w = k;
            while (w < b && (s[w] == ' ' || s[w] == '\t')) w++;
            if (w + 1 >= b || s[w] != ':' || s[w + 1] != ':') return option;
            double weight;
            if (!double.TryParse(s.Substring(a, k - a), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out weight)) return option;

            option.Weight = weight;
            a = w + 2;
            while (a < b && char.IsWhiteSpace(s[a])) a++;
            option.A = a;
            return option;
        }

        // ---- one Expand call -----------------------------------------------------------------

        private sealed class Run
        {
            private readonly WildcardEngine engine;
            private readonly Random rng;
            private readonly ExpansionInfo info;
            private readonly StringBuilder sb;
            private readonly bool guard;
            private int resolutions;

            // Where the last output character came from, for the placeholder-marker guard.
            private char lastChar;
            private Source lastSource;
            private int lastIndex = -1;

            public Run(WildcardEngine engine, Random rng, ExpansionInfo info, int capacityHint)
            {
                this.engine = engine;
                this.rng = rng;
                this.info = info;
                guard = engine.GuardPlaceholderMarkers;
                sb = new StringBuilder(capacityHint + 32);
            }

            public string Result()
            {
                return sb.ToString();
            }

            private bool Spend()
            {
                if (++resolutions <= MaxResolutionsPerCall && sb.Length <= MaxOutputChars) return true;
                if (info != null) info.HitLimit = true;
                return false;
            }

            /// <summary>
            /// Appends one character. <paramref name="src"/>/<paramref name="index"/> say where it
            /// was copied from; null means it did not come straight from source text.
            /// </summary>
            private void Emit(char c, Source src, int index)
            {
                if (guard && ((lastChar == '$' && c == '{') || (lastChar == '{' && c == '$')))
                {
                    // Kept only when the pair is a straight copy of two neighbouring characters
                    // of the caller's own template (its ${prompt}-style placeholders).
                    bool original = src != null && src.IsRoot && ReferenceEquals(src, lastSource) && index == lastIndex + 1;
                    if (!original) sb.Append(' ');
                }
                sb.Append(c);
                lastChar = c;
                lastSource = src;
                lastIndex = index;
            }

            private void EmitText(string text)
            {
                for (int i = 0; i < text.Length; i++) Emit(text[i], null, -1);
            }

            /// <summary>Copies source text through untouched (an unknown wildcard, a non-variant).</summary>
            private void EmitVerbatim(Source src, int a, int b)
            {
                for (int i = a; i < b; i++) Emit(src.S[i], src, i);
            }

            /// <summary>Copies source text that a limit stopped us from expanding.</summary>
            private void EmitUnexpanded(Source src, int a, int b)
            {
                if (info != null) info.HitLimit = true;
                for (int i = a; i < b; i++) Emit(src.S[i], null, -1);
            }

            private void Rewind(int length)
            {
                sb.Length = length;
                lastChar = length > 0 ? sb[length - 1] : '\0';
                lastSource = null;
                lastIndex = -1;
            }

            private void Record(Source src, int a, int b, int depth, int mark)
            {
                if (info == null || depth != 0 || !src.IsRoot) return;
                info.Rolls.Add(new KeyValuePair<string, string>(src.S.Substring(a, b - a), sb.ToString(mark, sb.Length - mark)));
            }

            public void ExpandRange(Source src, int start, int end, int depth)
            {
                string s = src.S;
                int i = start;
                while (i < end)
                {
                    char c = s[i];
                    if (c == '\\' && i + 1 < end && IsEscapeAt(s, i))
                    {
                        Emit(s[i + 1], src, i + 1);
                        i += 2;
                        continue;
                    }
                    // "${" is the host game's own placeholder syntax (${prompt}), never a variant.
                    if (c == '{' && !(i > start && s[i - 1] == '$'))
                    {
                        int close = src.Match(i);
                        if (close > i && close < end && TryVariant(src, i, close, depth))
                        {
                            i = close + 1;
                            continue;
                        }
                    }
                    else if (c == '_' && i + 1 < end && s[i + 1] == '_')
                    {
                        string name;
                        int tokenEnd;
                        if (TryReadWildcard(s, i, end, out name, out tokenEnd))
                        {
                            Wildcard(src, i, tokenEnd, name, depth);
                            i = tokenEnd;
                            continue;
                        }
                    }
                    Emit(c, src, i);
                    i++;
                }
            }

            private void ExpandLine(string line, int depth)
            {
                ExpandRange(new Source(line, false), 0, line.Length, depth);
            }

            private void Wildcard(Source src, int a, int b, string name, int depth)
            {
                if (depth >= engine.MaxDepth || !Spend())
                {
                    EmitUnexpanded(src, a, b);
                    return;
                }
                IList<WildcardEntry> entries = engine.Lookup(name);
                List<string> picks = entries == null ? null : engine.Draw(name, entries, 1, rng);
                if (picks == null || picks.Count == 0)
                {
                    if (entries == null) Unknown(name);
                    EmitVerbatim(src, a, b);
                    return;
                }
                int mark = sb.Length;
                ExpandLine(picks[0], depth + 1);
                Record(src, a, b, depth, mark);
            }

            private void Unknown(string name)
            {
                if (info != null && !info.UnknownWildcards.Contains(name)) info.UnknownWildcards.Add(name);
                engine.WarnUnknown(name);
            }

            /// <summary>
            /// Resolves the {...} spanning [open, close]. Returns false when it is not a variant,
            /// in which case the caller emits the "{" as text and keeps scanning inside it.
            /// </summary>
            private bool TryVariant(Source src, int open, int close, int depth)
            {
                string s = src.S;
                int a = open + 1;
                int b = close;

                // Top level only: nested {...} groups and escaped characters are skipped. The
                // "$$" markers of a pick header can only come before the first "|".
                List<int> bars = null;
                int dollars1 = -1;
                int dollars2 = -1;
                for (int k = a; k < b; k++)
                {
                    char c = s[k];
                    if (c == '\\')
                    {
                        if (k + 1 < b && IsEscapeAt(s, k)) k++;
                    }
                    else if (c == '{')
                    {
                        int m = src.Match(k);
                        if (m > k && m < b) k = m;
                    }
                    else if (c == '|')
                    {
                        if (bars == null) bars = new List<int>();
                        bars.Add(k);
                    }
                    else if (c == '$' && bars == null && k + 1 < b && s[k + 1] == '$')
                    {
                        if (dollars1 < 0) dollars1 = k;
                        else if (dollars2 < 0) dollars2 = k;
                        k++;
                    }
                }
                if (bars == null && dollars1 < 0) return false;
                if (LooksLikeJsonObject(s, a, b)) return false;

                int optionsStart = a;
                bool pickHeader = false;
                int lo = 1;
                int hi = 1;
                bool hiOpen = false;
                string separator = null;
                if (dollars1 >= 0 && TryParseBound(s, a, dollars1, out lo, out hi, out hiOpen))
                {
                    pickHeader = true;
                    if (dollars2 >= 0)
                    {
                        separator = Unescape(s, dollars1 + 2, dollars2);
                        optionsStart = dollars2 + 2;
                    }
                    else
                    {
                        optionsStart = dollars1 + 2;
                    }
                }
                else if (bars == null)
                {
                    return false;
                }

                List<Option> options = new List<Option>();
                int segmentStart = optionsStart;
                int barCount = bars == null ? 0 : bars.Count;
                for (int n = 0; n <= barCount; n++)
                {
                    int segmentEnd = n < barCount ? bars[n] : b;
                    options.Add(ParseOption(s, segmentStart, segmentEnd));
                    segmentStart = segmentEnd + 1;
                }

                if (depth >= engine.MaxDepth || !Spend())
                {
                    EmitUnexpanded(src, open, close + 1);
                    return true;
                }

                if (separator == null) separator = engine.Separator ?? "";
                int mark = sb.Length;

                // {2$$__name__}: the picks come from the wildcard's own options.
                if (pickHeader && options.Count == 1)
                {
                    string name;
                    int tokenEnd;
                    Option only = options[0];
                    if (only.B - only.A > 4 && s[only.A] == '_' && s[only.A + 1] == '_'
                        && TryReadWildcard(s, only.A, only.B, out name, out tokenEnd) && tokenEnd == only.B)
                    {
                        IList<WildcardEntry> entries = engine.Lookup(name);
                        if (entries == null)
                        {
                            Unknown(name);
                            EmitVerbatim(src, open, close + 1);
                            return true;
                        }
                        int available = CountDistinct(entries);
                        List<string> picks = engine.Draw(name, entries, ChooseCount(lo, hi, hiOpen, available), rng);
                        bool firstLine = true;
                        foreach (string line in picks)
                        {
                            int before = sb.Length;
                            if (!firstLine) EmitText(separator);
                            int afterSeparator = sb.Length;
                            ExpandLine(line, depth + 1);
                            if (sb.Length == afterSeparator) Rewind(before);
                            else firstLine = false;
                        }
                        Record(src, open, close + 1, depth, mark);
                        return true;
                    }
                }

                List<int> pool = new List<int>(options.Count);
                for (int n = 0; n < options.Count; n++)
                {
                    if (options[n].Weight > 0) pool.Add(n);
                }
                int count = pickHeader ? ChooseCount(lo, hi, hiOpen, pool.Count) : Math.Min(1, pool.Count);
                bool first = true;
                for (int n = 0; n < count; n++)
                {
                    double total = 0;
                    foreach (int index in pool) total += options[index].Weight;
                    double roll = rng.NextDouble() * total;
                    int chosen = pool.Count - 1;
                    for (int p = 0; p < pool.Count; p++)
                    {
                        roll -= options[pool[p]].Weight;
                        if (roll < 0)
                        {
                            chosen = p;
                            break;
                        }
                    }
                    Option option = options[pool[chosen]];
                    pool.RemoveAt(chosen);

                    int before = sb.Length;
                    if (!first) EmitText(separator);
                    int afterSeparator = sb.Length;
                    ExpandRange(src, option.A, option.B, depth + 1);
                    if (count > 1 && sb.Length == afterSeparator) Rewind(before);
                    else first = false;
                }
                Record(src, open, close + 1, depth, mark);
                return true;
            }

            private int ChooseCount(int lo, int hi, bool hiOpen, int available)
            {
                int low = Math.Max(0, Math.Min(lo, available));
                int high = hiOpen ? available : Math.Max(0, Math.Min(hi, available));
                if (high < low)
                {
                    int swap = low;
                    low = high;
                    high = swap;
                }
                return low == high ? low : rng.Next(low, high + 1);
            }

            private static int CountDistinct(IList<WildcardEntry> entries)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (WildcardEntry entry in entries)
                {
                    if (entry.Weight > 0) seen.Add(entry.Text ?? "");
                }
                return seen.Count;
            }
        }

        // ---- wildcard lookup and picking -------------------------------------------------------

        private IList<WildcardEntry> Lookup(string name)
        {
            if (source == null) return null;
            try
            {
                IList<WildcardEntry> entries = source.Get(name);
                return entries == null || entries.Count == 0 ? null : entries;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void WarnUnknown(string name)
        {
            bool isNew;
            lock (stateGate)
            {
                isNew = warnedNames.Add(name);
            }
            Action<string> warn = Warn;
            if (isNew && warn != null)
            {
                warn("Unknown wildcard __" + name + "__ (no matching .txt file with options); left as written.");
            }
        }

        private sealed class History
        {
            /// <summary>Options already handed out in the current pass through the list.</summary>
            public readonly HashSet<string> Used = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>The most recent picks, newest last.</summary>
            public readonly List<string> Recent = new List<string>();

            public void Push(string text)
            {
                Used.Add(text);
                Recent.Add(text);
                if (Recent.Count > MaxRecent) Recent.RemoveAt(0);
            }

            public bool IsRecent(string text, int window)
            {
                for (int i = Recent.Count - 1; i >= 0 && i >= Recent.Count - window; i--)
                {
                    if (string.Equals(Recent[i], text, StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Picks up to <paramref name="count"/> different options of a wildcard.
        ///
        /// With repeat suppression on, an unweighted list is dealt like a shuffled deck: every
        /// option is handed out once, in a fresh random order, before any comes round again, and
        /// an option among the last min(5, (n-1)/2) picks is skipped while another is available
        /// (so a new pass cannot open with what the last one closed on).
        ///
        /// The window is deliberately smaller than the list. A window of n-1 would leave exactly
        /// one legal pick each time, i.e. a fixed repeating order - and the game expands the same
        /// rules text for every generation request, not only the one that uses the pick, so a
        /// fixed order can fall into step with the request sequence and hand every new character
        /// the same option. The random order per pass makes that impossible.
        ///
        /// A list with unequal weights is left to plain weighted chance: dealing every option
        /// once per pass would cancel the weights.
        /// </summary>
        private List<string> Draw(string name, IList<WildcardEntry> entries, int count, Random rng)
        {
            List<string> result = new List<string>();
            if (count <= 0) return result;

            // Distinct option texts; a line listed twice counts double.
            List<string> texts = new List<string>();
            List<double> weights = new List<double>();
            Dictionary<string, int> indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            bool uniform = true;
            double firstWeight = -1;
            foreach (WildcardEntry entry in entries)
            {
                if (!(entry.Weight > 0)) continue;
                if (firstWeight < 0) firstWeight = entry.Weight;
                else if (entry.Weight != firstWeight) uniform = false;
                string text = entry.Text ?? "";
                int at;
                if (indexOf.TryGetValue(text, out at))
                {
                    weights[at] += entry.Weight;
                }
                else
                {
                    indexOf[text] = texts.Count;
                    texts.Add(text);
                    weights.Add(entry.Weight);
                }
            }
            int n = texts.Count;
            if (n == 0) return result;
            if (count > n) count = n;

            if (!RepeatSuppression || !uniform || n == 1)
            {
                for (int c = 0; c < count; c++)
                {
                    double total = 0;
                    for (int i = 0; i < texts.Count; i++) total += weights[i];
                    double roll = rng.NextDouble() * total;
                    int chosen = texts.Count - 1;
                    for (int i = 0; i < texts.Count; i++)
                    {
                        roll -= weights[i];
                        if (roll < 0)
                        {
                            chosen = i;
                            break;
                        }
                    }
                    result.Add(texts[chosen]);
                    texts.RemoveAt(chosen);
                    weights.RemoveAt(chosen);
                }
                return result;
            }

            int window = Math.Min(MaxRecent, (n - 1) / 2);
            lock (stateGate)
            {
                History past;
                if (!history.TryGetValue(name, out past))
                {
                    past = new History();
                    history[name] = past;
                }
                HashSet<string> takenNow = new HashSet<string>(StringComparer.Ordinal);
                for (int c = 0; c < count; c++)
                {
                    List<string> pool = new List<string>();
                    foreach (string text in texts)
                    {
                        if (!past.Used.Contains(text) && !takenNow.Contains(text)) pool.Add(text);
                    }
                    if (pool.Count == 0)
                    {
                        // Every option has had its turn: start the next pass.
                        past.Used.Clear();
                        foreach (string text in texts)
                        {
                            if (!takenNow.Contains(text)) pool.Add(text);
                        }
                        if (pool.Count == 0) break;
                    }
                    if (window > 0 && pool.Count > 1)
                    {
                        List<string> fresh = new List<string>();
                        foreach (string text in pool)
                        {
                            if (!past.IsRecent(text, window)) fresh.Add(text);
                        }
                        if (fresh.Count > 0) pool = fresh;
                    }
                    string pick = pool[rng.Next(pool.Count)];
                    past.Push(pick);
                    takenNow.Add(pick);
                    result.Add(pick);
                }
            }
            return result;
        }
    }
}
