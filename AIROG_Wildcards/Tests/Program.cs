using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIROG_Wildcards.Engine;

namespace WildcardEngineTests
{
    /// <summary>
    /// Console harness for the wildcard engine and file store. Exit code = number of failed checks.
    /// Run: dotnet run --project AIROG_Wildcards\Tests\WildcardEngineTests.csproj
    /// </summary>
    internal static class Program
    {
        private static int passed;
        private static int failed;
        private static string group = "";

        private sealed class MemorySource : IWildcardSource
        {
            private readonly Dictionary<string, List<WildcardEntry>> files =
                new Dictionary<string, List<WildcardEntry>>(StringComparer.OrdinalIgnoreCase);

            public MemorySource Add(string name, string content)
            {
                files[name] = WildcardFileStore.Parse(content);
                return this;
            }

            public IList<WildcardEntry> Get(string name)
            {
                List<WildcardEntry> entries;
                return files.TryGetValue(name, out entries) ? entries : null;
            }
        }

        private static int Main()
        {
            Stopwatch clock = Stopwatch.StartNew();
            Run("pass-through", PassThrough);
            Run("variants", Variants);
            Run("weights", Weights);
            Run("pick-N", PickN);
            Run("wildcards", Wildcards);
            Run("nesting", Nesting);
            Run("literal braces and JSON", LiteralBraces);
            Run("unbalanced braces", Unbalanced);
            Run("escapes", Escapes);
            Run("recursion limits", Recursion);
            Run("file parsing", FileParsing);
            Run("file store and globs", FileStoreAndGlobs);
            Run("repeat suppression", RepeatSuppression);
            Run("placeholder-marker guard", MarkerGuard);
            Run("reporting", Reporting);
            Run("determinism and threads", DeterminismAndThreads);

            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed ({2} checks, {3} ms)", passed, failed, passed + failed, clock.ElapsedMilliseconds);
            return failed;
        }

        // ---- tiny test kit -------------------------------------------------------------------

        private static void Run(string name, Action body)
        {
            group = name;
            int before = failed;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("  FAIL [{0}] threw: {1}", name, ex);
            }
            Console.WriteLine("{0} {1}", failed == before ? "ok  " : "FAIL", name);
        }

        private static void Check(bool condition, string what)
        {
            if (condition)
            {
                passed++;
                return;
            }
            failed++;
            Console.WriteLine("  FAIL [{0}] {1}", group, what);
        }

        private static void Eq(string expected, string actual, string what)
        {
            Check(string.Equals(expected, actual, StringComparison.Ordinal),
                what + "\n         expected: " + Show(expected) + "\n         actual:   " + Show(actual));
        }

        private static string Show(string s)
        {
            return s == null ? "<null>" : "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static WildcardEngine Engine(IWildcardSource source = null, bool suppression = true)
        {
            return new WildcardEngine(source ?? new MemorySource()) { RepeatSuppression = suppression };
        }

        /// <summary>Expands the template once per seed and counts each distinct result.</summary>
        private static Dictionary<string, int> Tally(WildcardEngine engine, string template, int draws, int seed = 1)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            Random rng = new Random(seed);
            for (int i = 0; i < draws; i++)
            {
                string result = engine.Expand(template, rng);
                int n;
                counts.TryGetValue(result, out n);
                counts[result] = n + 1;
            }
            return counts;
        }

        private static double Share(Dictionary<string, int> counts, string key)
        {
            int n;
            counts.TryGetValue(key, out n);
            return (double)n / counts.Values.Sum();
        }

        private static bool Near(double actual, double expected, double tolerance)
        {
            return Math.Abs(actual - expected) <= tolerance;
        }

        private static bool SameKeys(Dictionary<string, int> counts, params string[] keys)
        {
            return counts.Count == keys.Length && keys.All(counts.ContainsKey);
        }

        // ---- tests ---------------------------------------------------------------------------

        private static void PassThrough()
        {
            WildcardEngine engine = Engine();
            Random rng = new Random(1);

            Check(engine.Expand(null, rng) == null, "null stays null");
            Check(engine.Expand("", rng) == "", "empty stays empty");

            string plain = "A tall woman with long hair, no syntax here: 100% (plain) [text] a_b snake_case.";
            Check(!WildcardEngine.MayContainSyntax(plain), "pre-check is false for plain text");
            Check(ReferenceEquals(engine.Expand(plain, rng), plain), "plain text returns the same instance");
            Check(!WildcardEngine.MayContainSyntax("one _ underscore and a | bar and a } brace"), "single _, | and } are not syntax");
            Check(WildcardEngine.MayContainSyntax("a { b"), "pre-check is true for {");
            Check(WildcardEngine.MayContainSyntax("a __ b"), "pre-check is true for __");
            Check(!WildcardEngine.MayContainSyntax("snake\\_case and C:\\dir"), "a backslash that escapes nothing is not syntax");

            // The game's own default narrative rules: one ${placeholder}, nothing to expand.
            string rules = "- Use ${perspective_inst}, present tense.\n- Use double line breaks (\\n\\n) occasionally for pacing.";
            Check(ReferenceEquals(engine.Expand(rules, rng), rules), "text whose only brace is a ${placeholder} returns the same instance");

            bool threw = false;
            try
            {
                engine.Expand("{a|b}", null);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }
            Check(threw, "a null Random is refused rather than silently unseeded");
        }

        private static void Variants()
        {
            WildcardEngine engine = Engine();

            Dictionary<string, int> counts = Tally(engine, "{a|b|c}", 3000);
            Check(SameKeys(counts, "a", "b", "c"), "{a|b|c} yields exactly a, b and c");
            Check(Near(Share(counts, "a"), 1.0 / 3, 0.04) && Near(Share(counts, "b"), 1.0 / 3, 0.04), "{a|b|c} is uniform");

            counts = Tally(engine, "She has {long|short} hair.", 400);
            Check(SameKeys(counts, "She has long hair.", "She has short hair."), "text around a variant is kept");

            counts = Tally(engine, "{red|blue|}", 3000);
            Check(SameKeys(counts, "red", "blue", ""), "an empty option is allowed");
            Check(Near(Share(counts, ""), 1.0 / 3, 0.04), "the empty option gets its fair share");

            counts = Tally(engine, "{ red | blue }", 400);
            Check(SameKeys(counts, "red", "blue"), "whitespace around options is ignored");

            counts = Tally(engine, "{\n  red shirt\n  | blue shirt\n  | green shirt\n}", 600);
            Check(SameKeys(counts, "red shirt", "blue shirt", "green shirt"), "a variant may be written one option per line");

            counts = Tally(engine, "{a|b} and {c|d}", 2000);
            Check(SameKeys(counts, "a and c", "a and d", "b and c", "b and d"), "two variants roll independently");

            counts = Tally(engine, "{costs $$5|costs $$10}", 400);
            Check(SameKeys(counts, "costs $$5", "costs $$10"), "$$ that is not a pick header is ordinary text");
        }

        private static void Weights()
        {
            WildcardEngine engine = Engine();
            const int draws = 30000;

            Dictionary<string, int> counts = Tally(engine, "{2::a|1::b}", draws);
            Check(SameKeys(counts, "a", "b"), "weights are not printed");
            Check(Near(Share(counts, "a"), 2.0 / 3, 0.02), "{2::a|1::b} gives a about 2/3 of the time (got " + Share(counts, "a").ToString("0.000") + ")");

            counts = Tally(engine, "{0.5::a|1.5::b}", draws);
            Check(Near(Share(counts, "a"), 0.25, 0.02), "decimal weights work (got " + Share(counts, "a").ToString("0.000") + ")");

            counts = Tally(engine, "{3::a|b|c}", draws);
            Check(Near(Share(counts, "a"), 0.6, 0.02), "an option without a weight counts as 1 (got " + Share(counts, "a").ToString("0.000") + ")");

            counts = Tally(engine, "{ 2::red | 3::blue }", draws);
            Check(SameKeys(counts, "red", "blue") && Near(Share(counts, "blue"), 0.6, 0.02), "weights may have spaces around them");

            counts = Tally(engine, "{0::a|1::b}", 500);
            Check(SameKeys(counts, "b"), "a zero weight is never picked");
            counts = Tally(engine, "x{0::a|0::b}y", 100);
            Check(SameKeys(counts, "xy"), "all-zero weights yield nothing");

            counts = Tally(engine, "{2::|1::b}", draws);
            Check(SameKeys(counts, "", "b") && Near(Share(counts, ""), 2.0 / 3, 0.02), "an empty option can carry a weight");

            // Weighted wildcard file: plain weighted chance, not cycled.
            MemorySource source = new MemorySource().Add("w", "3::common\n1::rare\n");
            counts = Tally(Engine(source), "__w__", draws);
            Check(Near(Share(counts, "common"), 0.75, 0.02), "a weight::line in a wildcard file is honoured (got " + Share(counts, "common").ToString("0.000") + ")");

            source = new MemorySource().Add("dup", "a\na\na\nb\n");
            counts = Tally(Engine(source, suppression: false), "__dup__", draws);
            Check(Near(Share(counts, "a"), 0.75, 0.02), "a line listed three times is three times as likely");
        }

        private static void PickN()
        {
            WildcardEngine engine = Engine();
            Random rng = new Random(7);

            HashSet<string> seen = new HashSet<string>();
            bool allDistinct = true;
            for (int i = 0; i < 600; i++)
            {
                string result = engine.Expand("{2$$a|b|c}", rng);
                string[] parts = result.Split(new[] { ", " }, StringSplitOptions.None);
                if (parts.Length != 2 || parts[0] == parts[1] || !parts.All(p => p == "a" || p == "b" || p == "c")) allDistinct = false;
                seen.Add(result);
            }
            Check(allDistinct, "{2$$a|b|c} always gives two different options joined by \", \"");
            Check(seen.Count == 6, "all six ordered pairs turn up (got " + seen.Count + ")");

            Dictionary<int, int> sizes = Sizes(engine, "{1-3$$a|b|c}", 900, ", ");
            Check(sizes.Keys.OrderBy(k => k).SequenceEqual(new[] { 1, 2, 3 }), "{1-3$$...} gives one, two or three options");
            sizes = Sizes(engine, "{-2$$a|b|c}", 600, ", ");
            Check(sizes.Keys.OrderBy(k => k).SequenceEqual(new[] { 1, 2 }), "{-2$$...} means 1-2");
            sizes = Sizes(engine, "{2-$$a|b|c}", 600, ", ");
            Check(sizes.Keys.OrderBy(k => k).SequenceEqual(new[] { 2, 3 }), "{2-$$...} means 2 up to all");
            sizes = Sizes(engine, "{3-1$$a|b|c}", 600, ", ");
            Check(sizes.Keys.OrderBy(k => k).SequenceEqual(new[] { 1, 2, 3 }), "a reversed range is read as its two ends");

            Dictionary<string, int> counts = Tally(engine, "{2$$ and $$a|b|c}", 600);
            Check(counts.Count == 6 && counts.Keys.All(k => k.Split(new[] { " and " }, StringSplitOptions.None).Length == 2),
                "{2$$ and $$a|b|c} joins with the custom separator");
            counts = Tally(engine, "{2$$$$a|b}", 200);
            Check(SameKeys(counts, "ab", "ba"), "an empty custom separator joins with nothing");
            counts = Tally(engine, "{2$$ \\| $$a|b}", 200);
            Check(SameKeys(counts, "a | b", "b | a"), "the separator may contain an escaped |");

            counts = Tally(engine, "{5$$a|b}", 200);
            Check(SameKeys(counts, "a, b", "b, a"), "asking for more than there are gives them all");
            counts = Tally(engine, "x{0$$a|b}y", 50);
            Check(SameKeys(counts, "xy"), "{0$$...} gives nothing");
            counts = Tally(engine, "{2$$a|}", 200);
            Check(SameKeys(counts, "a"), "an empty pick is dropped from the join, with no stray separator");
            counts = Tally(engine, "{1$$a|b}", 200);
            Check(SameKeys(counts, "a", "b"), "{1$$a|b} is the same as {a|b}");

            counts = Tally(engine, "{2$$ {a|b} | c}", 400);
            Check(SameKeys(counts, "a, c", "b, c", "c, a", "c, b"), "an option of a pick can be a nested choice");
            counts = Tally(engine, "{2$${a|b}|c}", 400);
            Check(SameKeys(counts, "a, c", "b, c", "c, a", "c, b"), "... also with no space after the $$");

            // Weighted pick-N without replacement: the heavy option is almost always among the two.
            counts = Tally(engine, "{2$$20::a|1::b|1::c}", 4000);
            double withA = counts.Where(kv => kv.Key.Contains("a")).Sum(kv => kv.Value) / 4000.0;
            Check(withA > 0.97, "weights apply to pick-N (a present in " + withA.ToString("0.000") + " of results)");

            MemorySource source = new MemorySource().Add("colour", "red\ngreen\nblue\nwhite\n");
            WildcardEngine withFiles = Engine(source);
            bool ok = true;
            for (int i = 0; i < 300; i++)
            {
                string[] parts = withFiles.Expand("{2$$__colour__}", rng).Split(new[] { ", " }, StringSplitOptions.None);
                if (parts.Length != 2 || parts[0] == parts[1]) ok = false;
            }
            Check(ok, "{2$$__colour__} gives two different lines of the wildcard");
            sizes = Sizes(withFiles, "{1-3$$ / $$__colour__}", 600, " / ");
            Check(sizes.Keys.OrderBy(k => k).SequenceEqual(new[] { 1, 2, 3 }), "ranges and separators work when picking from a wildcard");
            Eq("{2$$__nothere__}", withFiles.Expand("{2$$__nothere__}", rng), "picking from an unknown wildcard leaves the text as typed");
        }

        private static Dictionary<int, int> Sizes(WildcardEngine engine, string template, int draws, string separator)
        {
            Dictionary<int, int> sizes = new Dictionary<int, int>();
            Random rng = new Random(11);
            for (int i = 0; i < draws; i++)
            {
                string result = engine.Expand(template, rng);
                string[] parts = result.Split(new[] { separator }, StringSplitOptions.None);
                int size = result.Length == 0 ? 0 : parts.Length;
                if (parts.Distinct().Count() != parts.Length) size = -1;
                int n;
                sizes.TryGetValue(size, out n);
                sizes[size] = n + 1;
            }
            return sizes;
        }

        private static void Wildcards()
        {
            MemorySource source = new MemorySource()
                .Add("colour", "red\ngreen\nblue\n")
                .Add("hair/bangs", "blunt bangs\nside-swept bangs\n")
                .Add("my-file_2", "x\ny\n")
                .Add("hair styles/long", "waist-length\n")
                .Add("one", "only\n")
                .Add("empty", "# nothing but a comment\n\n");
            WildcardEngine engine = Engine(source);
            Random rng = new Random(3);

            Dictionary<string, int> counts = Tally(engine, "__colour__", 600);
            Check(SameKeys(counts, "red", "green", "blue"), "__name__ picks a line of name.txt");
            counts = Tally(engine, "a __colour__ hat", 300);
            Check(SameKeys(counts, "a red hat", "a green hat", "a blue hat"), "text around a wildcard is kept");
            counts = Tally(engine, "__hair/bangs__", 300);
            Check(SameKeys(counts, "blunt bangs", "side-swept bangs"), "subfolders work");
            counts = Tally(engine, "__my-file_2__", 300);
            Check(SameKeys(counts, "x", "y"), "names may contain -, _ and digits");
            Eq("waist-length", engine.Expand("__hair styles/long__", rng), "names may contain spaces");
            Eq("only", engine.Expand("__ONE__", rng), "names are not case sensitive");
            counts = Tally(engine, "__colour____colour__", 600);
            Check(counts.Count >= 6 && counts.Keys.All(k => !k.Contains("_")), "two wildcards back to back both expand");
            Eq("only_", engine.Expand("__one___", rng), "the name ends at the first closing __");

            List<string> warnings = new List<string>();
            engine.Warn = warnings.Add;
            string unknown = "a __nope__ b";
            bool sameInstance = true;
            for (int i = 0; i < 5; i++) sameInstance &= ReferenceEquals(engine.Expand(unknown, rng), unknown);
            Check(sameInstance, "an unknown wildcard is left untouched (same string instance)");
            Check(warnings.Count == 1 && warnings[0].Contains("nope"), "an unknown wildcard is reported once, not every time (got " + warnings.Count + ")");
            Eq("x __nope__ red", engine.Expand("x __nope__ __colour__", new Random(0)).Replace("green", "red").Replace("blue", "red"),
                "an unknown wildcard does not stop the rest of the text expanding");
            Eq("__empty__", engine.Expand("__empty__", rng), "a file with no options is treated like a missing one");

            foreach (string text in new[]
            {
                "__", "____", "__a", "a__b", "__ a __", "__a b:c__", "x __ y __ z", "___", "_ _a_ _",
                "__../secret__", "__/etc/passwd__", "__a//b__", "__a..b__", "__colour/__", "__-colour__"
            })
            {
                Check(ReferenceEquals(engine.Expand(text, rng), text), "not a wildcard token, left alone: " + text);
            }
            Eq("__init__", engine.Expand("__init__", rng), "a dunder with no file of that name is left alone");
        }

        private static void Nesting()
        {
            MemorySource source = new MemorySource()
                .Add("colour", "red\nblue\n")
                .Add("garment", "coat\nhat\n")
                .Add("outfit", "a {plain|striped} __garment__\n")
                .Add("look", "__outfit__ in __colour__\n");
            WildcardEngine engine = Engine(source);

            Dictionary<string, int> counts = Tally(engine, "{a|{b|c}}", 3000);
            Check(SameKeys(counts, "a", "b", "c"), "a variant inside a variant");
            Check(Near(Share(counts, "a"), 0.5, 0.04) && Near(Share(counts, "b"), 0.25, 0.04), "the inner variant only rolls when its option is chosen");

            counts = Tally(engine, "{__colour__|plain}", 1200);
            Check(SameKeys(counts, "red", "blue", "plain"), "a wildcard inside a variant");

            counts = Tally(engine, "__outfit__", 1200);
            Check(SameKeys(counts, "a plain coat", "a plain hat", "a striped coat", "a striped hat"),
                "a wildcard line containing a variant and another wildcard");

            counts = Tally(engine, "__look__", 2400);
            Check(counts.Count == 8 && counts.ContainsKey("a striped hat in blue"), "wildcards nest three files deep");

            counts = Tally(engine, "{{a|b}{1|2}|z}", 3000);
            Check(SameKeys(counts, "a1", "a2", "b1", "b2", "z"), "adjacent nested variants inside one option");

            counts = Tally(engine, "{x {\"k\": 1}|y}", 300);
            Check(SameKeys(counts, "x {\"k\": 1}", "y"), "a literal {..} inside a chosen option survives");
        }

        private static void LiteralBraces()
        {
            MemorySource source = new MemorySource().Add("colour", "red\n");
            WildcardEngine engine = Engine(source);
            Random rng = new Random(5);

            foreach (string text in new[]
            {
                "{\"name\": \"x\", \"tags\": [\"a\", \"b\"]}",
                "{placeholder}",
                "${prompt}",
                "${maybe_entity_generation_rules}Background: ${BACKGROUND_WO_NEWLINES}",
                "{\"a\": {\"b\": [1, 2, {\"c\": null}]}}",
                "{}",
                "{ }",
                "{{double}}",
                "{\"type\": \"attack|defend\"}",
                "  {  \"type\" : \"a|b\", \"n\": 2 }",
                "[{\"role\": \"system\", \"content\": \"Reply as {\\\"story\\\": \\\"...\\\"}\"}]",
                "{abc$$def}",
                "{$$}",
                "${a|b}",
                "US${a|b} dollars",
                "{name}: {desc} ({num})"
            })
            {
                Check(ReferenceEquals(engine.Expand(text, rng), text), "left untouched: " + text);
            }

            Dictionary<string, int> counts = Tally(engine, "Format: {\"name\": \"x\"} then {a|b}", 400);
            Check(SameKeys(counts, "Format: {\"name\": \"x\"} then a", "Format: {\"name\": \"x\"} then b"),
                "JSON is kept while a real variant beside it expands");
            Eq("{\"hair\": \"red\"}", engine.Expand("{\"hair\": \"__colour__\"}", rng), "a wildcard inside literal braces still expands");
            counts = Tally(engine, "{\"hair\": \"{long|short}\"}", 400);
            Check(SameKeys(counts, "{\"hair\": \"long\"}", "{\"hair\": \"short\"}"), "a variant inside literal braces still expands");
            counts = Tally(engine, "${prompt}, {oil painting|ink sketch}", 400);
            Check(SameKeys(counts, "${prompt}, oil painting", "${prompt}, ink sketch"), "the game's ${prompt} placeholder survives next to a variant");
            counts = Tally(engine, "{${prompt}, oil|${prompt}, ink}", 400);
            Check(SameKeys(counts, "${prompt}, oil", "${prompt}, ink"), "... and inside a variant's options");
            counts = Tally(engine, "{\"red\"|\"blue\"}", 400);
            Check(SameKeys(counts, "\"red\"", "\"blue\""), "quoted options are still a variant when no \"key\": follows");
            Eq("xy", engine.Expand("x{2$$}y", rng), "a pick header with nothing to pick from gives nothing");
        }

        private static void Unbalanced()
        {
            WildcardEngine engine = Engine();
            Random rng = new Random(9);

            foreach (string text in new[] { "{a|b", "a|b}", "}{", "{", "}", "{{a|b", "a } b { c", "}}", "{\"open\": [" })
            {
                Check(ReferenceEquals(engine.Expand(text, rng), text), "left untouched: " + text);
            }
            Dictionary<string, int> counts = Tally(engine, "{ {a|b}", 300);
            Check(SameKeys(counts, "{ a", "{ b"), "a stray { before a real variant stays, the variant expands");
            counts = Tally(engine, "{a|b} }", 300);
            Check(SameKeys(counts, "a }", "b }"), "a stray } after a real variant stays");
            counts = Tally(engine, "{a|b}}", 300);
            Check(SameKeys(counts, "a}", "b}"), "an extra closing brace is text");
        }

        private static void Escapes()
        {
            MemorySource source = new MemorySource().Add("colour", "red\n").Add("esc", "\\{keep|me\\}\n");
            WildcardEngine engine = Engine(source);
            Random rng = new Random(13);

            Eq("{a|b}", engine.Expand("\\{a|b\\}", rng), "escaped braces are literal and the backslashes go");
            Eq("{a|b}", engine.Expand("\\{a\\|b\\}", rng), "escaped | outside a variant is literal");
            Dictionary<string, int> counts = Tally(engine, "{a\\|b|c}", 400);
            Check(SameKeys(counts, "a|b", "c"), "an escaped | does not split a variant");
            counts = Tally(engine, "{x\\}y|z}", 400);
            Check(SameKeys(counts, "x}y", "z"), "an escaped } does not close a variant");
            counts = Tally(engine, "{\\{a\\}|b}", 400);
            Check(SameKeys(counts, "{a}", "b"), "escaped braces inside an option");

            Eq("__colour__", engine.Expand("\\_\\_colour\\_\\_", rng), "\\_\\_name\\_\\_ is a literal __name__");
            Eq("__colour__", engine.Expand("\\__colour__", rng), "one backslash before the opening __ is enough");
            Eq("red and __colour__", engine.Expand("__colour__ and \\_\\_colour\\_\\_", rng), "escaped and live wildcards side by side");

            string kept = "snake\\_case, a\\b, C:\\dir and \\n stay as typed {a|a}";
            Eq("snake\\_case, a\\b, C:\\dir and \\n stay as typed a", engine.Expand(kept, rng), "a backslash that escapes nothing is kept");
            Eq("\"quote\\\"d\" a", engine.Expand("\"quote\\\"d\" {a|a}", rng), "JSON-style \\\" is not an escape of ours");

            Eq("{keep|me}", engine.Expand("__esc__", rng), "escapes work inside wildcard files");
            // Output is never re-scanned: an escaped token produced once stays literal.
            Eq("{a|b} __colour__", engine.Expand("{\\{a\\|b\\} \\_\\_colour\\_\\_|\\{a\\|b\\} \\_\\_colour\\_\\_}", rng), "expanded text is not parsed a second time");
        }

        private static void Recursion()
        {
            MemorySource source = new MemorySource()
                .Add("loop", "x __loop__\n")
                .Add("ping", "p __pong__\n")
                .Add("pong", "q __ping__\n")
                .Add("fan", "__fan__ __fan__ __fan__ __fan__\n")
                .Add("deep", "{a|{b|{c|{d|__deep__}}}}\n");
            WildcardEngine engine = Engine(source);
            Random rng = new Random(17);

            ExpansionInfo info = new ExpansionInfo();
            string result = engine.Expand("__loop__", rng, info);
            Eq(string.Join(" ", Enumerable.Repeat("x", 10)) + " __loop__", result, "a self-referencing file stops at depth 10 and leaves the rest as written");
            Check(info.HitLimit, "hitting the depth limit is reported");

            engine.MaxDepth = 3;
            Eq("x x x __loop__", engine.Expand("__loop__", rng), "MaxDepth is respected");
            engine.MaxDepth = WildcardEngine.DefaultMaxDepth;

            result = engine.Expand("__ping__", rng);
            Check(result.StartsWith("p q p q") && (result.EndsWith("__ping__") || result.EndsWith("__pong__")), "two files referencing each other terminate: " + result);

            Stopwatch clock = Stopwatch.StartNew();
            info = new ExpansionInfo();
            result = engine.Expand("__fan__", rng, info);
            clock.Stop();
            Check(result.Length < 400000 && clock.ElapsedMilliseconds < 2000 && info.HitLimit,
                "a file that references itself four times per line is cut off (" + result.Length + " chars, " + clock.ElapsedMilliseconds + " ms)");

            for (int i = 0; i < 200; i++) result = engine.Expand("__deep__", rng);
            Check(true, "variants and wildcards recursing through each other terminate");

            // Ordinary depth is nowhere near the limit.
            info = new ExpansionInfo();
            engine.Expand("{a|{b|{c|{d|e}}}}", rng, info);
            Check(!info.HitLimit, "four nested variants do not hit the limit");
        }

        private static void FileParsing()
        {
            List<WildcardEntry> entries = WildcardFileStore.Parse("\uFEFF# comment\r\nfirst\r\n\r\n   \r\n  second  \r\n#another\r\n2::third\r\n0.5 :: fourth\r\nlast");
            Check(entries.Count == 5, "CRLF, BOM, blank lines and comments are handled (" + entries.Count + " entries)");
            Eq("first", entries[0].Text, "the BOM does not stick to the first line and the comment is skipped");
            Eq("second", entries[1].Text, "lines are trimmed");
            Check(entries[2].Text == "third" && entries[2].Weight == 2.0, "a weight:: prefix is read");
            Check(entries[3].Text == "fourth" && entries[3].Weight == 0.5, "a decimal weight with spaces is read");
            Check(entries[4].Text == "last" && entries[4].Weight == 1.0, "a last line without a newline is read");

            entries = WildcardFileStore.Parse("a\rb\rc");
            Check(entries.Count == 3, "old Mac line endings are handled");
            entries = WildcardFileStore.Parse("10:30 in the morning\nratio 2::1\n::odd\n3::\n");
            Check(entries[0].Text == "10:30 in the morning" && entries[0].Weight == 1.0, "a single colon is not a weight");
            Check(entries[1].Text == "ratio 2::1", ":: later in the line is not a weight");
            Check(entries[2].Text == "::odd", ":: with no number is not a weight");
            Check(entries[3].Text == "" && entries[3].Weight == 3.0, "a weight with nothing after it is a weighted empty option");
            Check(WildcardFileStore.Parse("").Count == 0 && WildcardFileStore.Parse(null).Count == 0 && WildcardFileStore.Parse("\n\n# x\n").Count == 0,
                "empty and comment-only content has no options");

            Eq("caf\u00E9", WildcardFileStore.Decode(new byte[] { 0xEF, 0xBB, 0xBF, 0x63, 0x61, 0x66, 0xC3, 0xA9 }), "UTF-8 with a BOM");
            Eq("caf\u00E9", WildcardFileStore.Decode(new byte[] { 0x63, 0x61, 0x66, 0xC3, 0xA9 }), "UTF-8 without a BOM");
            Eq("caf\u00E9", WildcardFileStore.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("caf\u00E9")).ToArray()), "UTF-16 with a BOM");
            Eq("caf\u00E9", WildcardFileStore.Decode(new byte[] { 0x63, 0x61, 0x66, 0xE9 }), "a Latin-1 / Windows-1252 file is not mangled");
            Eq("", WildcardFileStore.Decode(new byte[0]), "an empty file");
        }

        private static void FileStoreAndGlobs()
        {
            string root = Path.Combine(Path.GetTempPath(), "airog_wildcard_tests_" + Guid.NewGuid().ToString("N"));
            string wildcards = Path.Combine(root, "wildcards");
            try
            {
                Directory.CreateDirectory(Path.Combine(wildcards, "hair", "long"));
                File.WriteAllText(Path.Combine(root, "secret.txt"), "outside\n");
                File.WriteAllText(Path.Combine(wildcards, "colours_warm.txt"), "red\r\norange\r\n");
                File.WriteAllText(Path.Combine(wildcards, "colours_cool.txt"), "blue\n");
                File.WriteAllText(Path.Combine(wildcards, "other.txt"), "other\n");
                File.WriteAllText(Path.Combine(wildcards, "notes.txt_old"), "should not be listed\n");
                File.WriteAllText(Path.Combine(wildcards, "hair", "bangs.txt"), "\uFEFF# bangs\nblunt\ncurtain\n", new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(wildcards, "hair", "style.txt"), "bob\n");
                File.WriteAllText(Path.Combine(wildcards, "hair", "long", "braids.txt"), "twin braids\n");

                List<string> log = new List<string>();
                WildcardFileStore store = new WildcardFileStore(wildcards) { Log = log.Add, ListingTtlSeconds = 0 };
                WildcardEngine engine = Engine(store, suppression: false);

                Dictionary<string, int> counts = Tally(engine, "__hair/bangs__", 300);
                Check(SameKeys(counts, "blunt", "curtain"), "a real file is read (BOM and comment handled)");
                counts = Tally(engine, "__HAIR/Bangs__", 100);
                Check(SameKeys(counts, "blunt", "curtain"), "file names are not case sensitive");
                counts = Tally(engine, "__colours_warm__", 300);
                Check(SameKeys(counts, "red", "orange"), "CRLF file is read");

                counts = Tally(engine, "__hair/*__", 900);
                Check(SameKeys(counts, "blunt", "curtain", "bob"), "__hair/*__ is the union of the files directly in hair/");
                counts = Tally(engine, "__hair/**__", 1200);
                Check(SameKeys(counts, "blunt", "curtain", "bob", "twin braids"), "__hair/**__ also takes subfolders");
                counts = Tally(engine, "__colours*__", 900);
                Check(SameKeys(counts, "red", "orange", "blue"), "__colours*__ matches by name prefix");
                counts = Tally(engine, "__*__", 2000);
                Check(SameKeys(counts, "red", "orange", "blue", "other"), "__*__ is every file in the top folder only");
                counts = Tally(engine, "__**__", 4000);
                Check(counts.Count == 8 && !counts.ContainsKey("should not be listed") && !counts.ContainsKey("outside"),
                    "__**__ is every .txt under the folder and nothing else (" + counts.Count + " options)");
                Eq("__nothing/*__", engine.Expand("__nothing/*__", new Random(1)), "a glob that matches nothing is an unknown wildcard");
                counts = Tally(engine, "{2$$__hair/*__}", 300);
                Check(counts.Keys.All(k => k.Split(new[] { ", " }, StringSplitOptions.None).Distinct().Count() == 2), "pick-N works on a glob");

                Check(store.Get("../secret") == null && store.Get("..\\secret") == null && store.Get("/secret") == null
                    && store.Get(Path.Combine(root, "secret")) == null, "the store refuses names that leave its folder");
                Eq("__../secret__", engine.Expand("__../secret__", new Random(1)), "the engine does not even treat a path-climbing name as a wildcard");
                Check(store.Get("missing") == null && store.Get("") == null && store.Get(null) == null, "missing and empty names give null, no exception");

                // Cached until the file changes, then re-read with no restart.
                int loadsBefore = log.Count(l => l.Contains("'other'"));
                for (int i = 0; i < 5; i++) store.Get("other");
                Check(log.Count(l => l.Contains("'other'")) == loadsBefore, "an unchanged file is served from the cache");
                string otherPath = Path.Combine(wildcards, "other.txt");
                File.WriteAllText(otherPath, "changed one\nchanged two\n");
                File.SetLastWriteTimeUtc(otherPath, DateTime.UtcNow.AddSeconds(5));
                counts = Tally(engine, "__other__", 200);
                Check(SameKeys(counts, "changed one", "changed two"), "an edited file is picked up without a restart");

                File.WriteAllText(Path.Combine(wildcards, "brand_new.txt"), "fresh\n");
                Eq("fresh", engine.Expand("__brand_new__", new Random(1)), "a file added while running is found");
                counts = Tally(engine, "__brand*__", 50);
                Check(SameKeys(counts, "fresh"), "... and by a glob too");
                File.Delete(Path.Combine(wildcards, "brand_new.txt"));
                Eq("__brand_new__", engine.Expand("__brand_new__", new Random(1)), "a deleted file becomes unknown again");

                store.Clear();
                Check(store.CountFiles() == 6, "CountFiles counts the .txt files (" + store.CountFiles() + ")");

                WildcardFileStore nowhere = new WildcardFileStore(Path.Combine(root, "does_not_exist"));
                Check(nowhere.Get("anything") == null && nowhere.Get("*") == null && nowhere.CountFiles() == 0, "a missing folder is not an error");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (IOException)
                {
                }
            }

            Check(WildcardFileStore.GlobToRegex("hair/*").IsMatch("hair/bangs") && !WildcardFileStore.GlobToRegex("hair/*").IsMatch("hair/long/braids"),
                "* does not cross a folder");
            Check(WildcardFileStore.GlobToRegex("**/bangs").IsMatch("bangs") && WildcardFileStore.GlobToRegex("**/bangs").IsMatch("a/b/bangs"),
                "**/ matches zero or more folders");
            Check(!WildcardFileStore.GlobToRegex("a.b*").IsMatch("axb1"), "other characters in a pattern are literal");
        }

        private static List<string> Picks(WildcardEngine engine, string name, int count, int seed)
        {
            Random rng = new Random(seed);
            List<string> picks = new List<string>(count);
            for (int i = 0; i < count; i++) picks.Add(engine.Expand("__" + name + "__", rng));
            return picks;
        }

        private static MemorySource Lists()
        {
            MemorySource source = new MemorySource();
            for (int n = 1; n <= 12; n++)
            {
                source.Add("n" + n, string.Join("\n", Enumerable.Range(1, n).Select(i => "opt" + i)));
            }
            return source;
        }

        private static void RepeatSuppression()
        {
            // Every pass through the list is a full permutation.
            bool passesOk = true;
            for (int n = 2; n <= 12; n++)
            {
                List<string> picks = Picks(Engine(Lists()), "n" + n, n * 40, 100 + n);
                for (int start = 0; start < picks.Count; start += n)
                {
                    if (picks.Skip(start).Take(n).Distinct().Count() != n) passesOk = false;
                }
            }
            Check(passesOk, "lists of 2 to 12 options: every option is used once before any repeats");

            // No option among the last min(5, (n-1)/2) picks comes straight back.
            bool windowOk = true;
            bool noImmediateRepeat = true;
            for (int n = 3; n <= 12; n++)
            {
                int window = Math.Min(5, (n - 1) / 2);
                List<string> picks = Picks(Engine(Lists()), "n" + n, n * 60, 200 + n);
                for (int i = 1; i < picks.Count; i++)
                {
                    if (picks[i] == picks[i - 1]) noImmediateRepeat = false;
                    for (int back = 1; back <= window && i - back >= 0; back++)
                    {
                        if (picks[i] == picks[i - back]) windowOk = false;
                    }
                }
            }
            Check(noImmediateRepeat, "lists of 3 or more options never give the same option twice running");
            Check(windowOk, "no pick repeats one of the last min(5, (n-1)/2) picks");
            List<string> eleven = Picks(Engine(Lists()), "n11", 660, 31);
            bool sixDistinct = true;
            for (int i = 5; i < eleven.Count; i++)
            {
                if (eleven.Skip(i - 5).Take(6).Distinct().Count() != 6) sixDistinct = false;
            }
            Check(sixDistinct, "with 11 or more options any six consecutive picks are all different (window of five)");

            // The order is random per pass, not a fixed cycle...
            foreach (int n in new[] { 2, 3, 4, 6 })
            {
                List<string> picks = Picks(Engine(Lists()), "n" + n, n * 200, 300 + n);
                HashSet<string> orders = new HashSet<string>();
                for (int start = 0; start < picks.Count; start += n) orders.Add(string.Join(">", picks.Skip(start).Take(n)));
                Check(orders.Count > 1, "a list of " + n + " does not settle into one fixed order (" + orders.Count + " different pass orders)");
            }
            // ... which matters because the game expands the rules for every request, not only the
            // one that uses the pick. Reading every n-th pick must not always give the same option.
            foreach (int n in new[] { 2, 3, 4, 5, 6 })
            {
                List<string> picks = Picks(Engine(Lists()), "n" + n, n * 300, 400 + n);
                List<string> everyNth = picks.Where((p, i) => i % n == 0).ToList();
                int repeats = 0;
                for (int i = 1; i < everyNth.Count; i++)
                {
                    if (everyNth[i] == everyNth[i - 1]) repeats++;
                }
                double rate = (double)repeats / (everyNth.Count - 1);
                Check(everyNth.Distinct().Count() == n && rate < 0.75,
                    "list of " + n + ": taking every " + n + "th pick still varies (same-as-previous rate " + rate.ToString("0.00") + ")");
            }

            // Long-run fairness is exact: every option the same number of times.
            Dictionary<string, int> counts = Tally(Engine(Lists()), "__n7__", 700);
            Check(counts.Count == 7 && counts.Values.All(v => v == 100), "over whole passes every option is picked equally often");

            // Switched off: independent draws, so repeats do happen.
            List<string> free = Picks(Engine(Lists(), suppression: false), "n3", 600, 55);
            int immediate = 0;
            for (int i = 1; i < free.Count; i++)
            {
                if (free[i] == free[i - 1]) immediate++;
            }
            Check(immediate > 120 && immediate < 280, "with suppression off, back-to-back repeats occur at the chance rate (" + immediate + " of 599)");

            // Each wildcard has its own history.
            WildcardEngine engine = Engine(Lists());
            Random rng = new Random(77);
            bool independent = true;
            for (int i = 0; i < 40; i++)
            {
                string[] parts = engine.Expand("__n2__ __n3__", rng).Split(' ');
                if (parts.Length != 2) independent = false;
            }
            List<string> afterMix = Picks(engine, "n3", 3, 78);
            Check(independent && afterMix.Distinct().Count() >= 2, "histories are kept per wildcard name");

            // One option: nothing to suppress.
            Check(Picks(Engine(Lists()), "n1", 5, 1).All(p => p == "opt1"), "a one-line wildcard always gives its line");

            // Pick-N from a wildcard is distinct within the call and also walks the pass.
            engine = Engine(Lists());
            rng = new Random(91);
            bool distinctWithin = true;
            List<string> flat = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                string[] parts = engine.Expand("{2$$__n6__}", rng).Split(new[] { ", " }, StringSplitOptions.None);
                if (parts.Length != 2 || parts[0] == parts[1]) distinctWithin = false;
                flat.AddRange(parts);
            }
            bool flatPasses = true;
            for (int start = 0; start < flat.Count; start += 6)
            {
                if (flat.Skip(start).Take(6).Distinct().Count() != 6) flatPasses = false;
            }
            Check(distinctWithin && flatPasses, "{2$$__name__} gives distinct lines and still covers the list before repeating");
            bool threeOfFour = true;
            for (int i = 0; i < 80; i++)
            {
                string[] parts = engine.Expand("{3$$__n4__}", rng).Split(new[] { ", " }, StringSplitOptions.None);
                if (parts.Distinct().Count() != 3) threeOfFour = false;
            }
            Check(threeOfFour, "pick-N stays distinct when a pass ends in the middle of it");

            // ResetState forgets the history.
            engine = Engine(Lists());
            Picks(engine, "n5", 3, 5);
            engine.ResetState();
            Check(Picks(engine, "n5", 5, 6).Distinct().Count() == 5, "after ResetState a fresh pass starts");

            // Weighted lists are left to chance (cycling would cancel the weights).
            MemorySource weighted = new MemorySource().Add("w", "9::common\n1::rare\n");
            counts = Tally(Engine(weighted), "__w__", 20000);
            Check(Near(Share(counts, "common"), 0.9, 0.02), "a weighted file keeps its weights with suppression on (got " + Share(counts, "common").ToString("0.000") + ")");

            // Inline variants are not cycled.
            List<string> inline = new List<string>();
            rng = new Random(123);
            engine = Engine();
            for (int i = 0; i < 600; i++) inline.Add(engine.Expand("{a|b|c}", rng));
            immediate = 0;
            for (int i = 1; i < inline.Count; i++)
            {
                if (inline[i] == inline[i - 1]) immediate++;
            }
            Check(immediate > 120, "{a|b|c} choices are independent draws (" + immediate + " back-to-back repeats of 599)");
        }

        private static void MarkerGuard()
        {
            MemorySource source = new MemorySource()
                .Add("var", "${colour} coat\n")
                .Add("price", "$5\n")
                .Add("brace", "{x}\n")
                .Add("dollar", "$\n")
                .Add("rev", "{$weird}\n")
                .Add("loop", "{2$${a|__loop__}|c}\n");
            WildcardEngine guarded = new WildcardEngine(source) { GuardPlaceholderMarkers = true };
            WildcardEngine plain = new WildcardEngine(source);
            Random rng = new Random(21);

            Eq("${colour} coat", plain.Expand("__var__", rng), "without the guard a wildcard line is passed on as written");
            Eq("$ {colour} coat", guarded.Expand("__var__", rng), "a ${...} coming out of a wildcard file is broken up");
            Eq("{ $weird}", guarded.Expand("__rev__", rng), "... and so is {$");
            Eq("{ $5}", guarded.Expand("{__price__}", rng), "a marker formed where template and wildcard text meet is broken up");
            Eq("$ {x}", guarded.Expand("$__brace__", rng), "... in the other order too");
            Eq("$ {name}", guarded.Expand("__dollar__{name}", rng), "... and after the wildcard");
            Eq("$ {x}", guarded.Expand("{$|$}{x}", rng), "a marker formed by two separate pieces of the template is broken up");
            Eq("$ {x}", guarded.Expand("$\\{x}", rng), "an escaped brace does not recreate a placeholder");

            foreach (string text in new[] { "${prompt}", "(masterpiece) ${prompt}", "Use ${perspective_inst}, present tense. {a|a}", "{$", "a ${ b" })
            {
                string expected = text.Replace("{a|a}", "a");
                Eq(expected, guarded.Expand(text, rng), "the template's own markers are kept: " + text);
            }
            Dictionary<string, int> counts = Tally(guarded, "${prompt}, {oil|ink}", 300);
            Check(SameKeys(counts, "${prompt}, oil", "${prompt}, ink"), "${prompt} survives beside a variant with the guard on");
            counts = Tally(guarded, "{${prompt}, oil|${prompt}, ink}", 300);
            Check(SameKeys(counts, "${prompt}, oil", "${prompt}, ink"), "${prompt} survives inside a variant's options with the guard on");
            counts = Tally(guarded, "{$5|$10} off", 300);
            Check(SameKeys(counts, "$5 off", "$10 off"), "options that start with $ are fine");

            // Whatever the depth limit leaves unexpanded must not contain a marker either.
            bool clean = true;
            for (int i = 0; i < 200; i++)
            {
                string result = guarded.Expand("__loop__", rng);
                if (result.Contains("${") || result.Contains("{$")) clean = false;
            }
            Check(clean, "text left unexpanded by the depth limit never contains \"${\" or \"{$\"");
        }

        private static void Reporting()
        {
            MemorySource source = new MemorySource().Add("colour", "red\n").Add("outfit", "{plain|plain} coat\n");
            WildcardEngine engine = Engine(source);
            ExpansionInfo info = new ExpansionInfo();
            string result = engine.Expand("a __colour__ __outfit__, {x|x}, __nope__ and {\"k\": 1}", new Random(1), info);
            Eq("a red plain coat, x, __nope__ and {\"k\": 1}", result, "mixed template");
            Check(info.Rolls.Count == 3, "one roll is reported per top-level token (" + info.Rolls.Count + ")");
            Check(info.Rolls[0].Key == "__colour__" && info.Rolls[0].Value == "red", "a roll names the token and what it became");
            Check(info.Rolls[1].Key == "__outfit__" && info.Rolls[1].Value == "plain coat", "a nested roll is reported as its final text");
            Check(info.Rolls[2].Key == "{x|x}" && info.Rolls[2].Value == "x", "variants are reported too");
            Check(info.UnknownWildcards.Count == 1 && info.UnknownWildcards[0] == "nope", "unknown wildcards are listed");
            Check(!info.HitLimit, "no limit was hit");
        }

        private static void DeterminismAndThreads()
        {
            MemorySource source = new MemorySource()
                .Add("colour", "red\ngreen\nblue\nwhite\nblack\n")
                .Add("outfit", "a {plain|striped} __colour__ {coat|hat|scarf}\n2::a __colour__ cloak\n");
            const string template = "__outfit__ and {1-3$$ / $$__colour__} with {3::gold|silver|} trim, {\"json\": true}";

            List<string> first = new List<string>();
            List<string> second = new List<string>();
            WildcardEngine a = Engine(source);
            WildcardEngine b = Engine(source);
            Random ra = new Random(4242);
            Random rb = new Random(4242);
            for (int i = 0; i < 200; i++)
            {
                first.Add(a.Expand(template, ra));
                second.Add(b.Expand(template, rb));
            }
            Check(first.SequenceEqual(second), "the same seed gives the same sequence of expansions");
            Check(first.Distinct().Count() > 20, "different draws give different text (" + first.Distinct().Count() + " distinct of 200)");
            Check(first.All(s => s.EndsWith("{\"json\": true}")), "the JSON tail is intact in every expansion");

            // One shared engine, many threads, each with its own Random - as in the game.
            WildcardEngine shared = Engine(source);
            int errors = 0;
            Parallel.For(0, 8, worker =>
            {
                Random rng = new Random(1000 + worker);
                for (int i = 0; i < 3000; i++)
                {
                    try
                    {
                        string result = shared.Expand(template, rng);
                        if (result.Contains("__") || result.Contains("|") || !result.EndsWith("{\"json\": true}")) Interlocked.Increment(ref errors);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            });
            Check(errors == 0, "a shared engine survives concurrent use (" + errors + " bad results of 24000)");

            // Cost of the common case: text with braces but nothing to expand.
            string json = string.Concat(Enumerable.Repeat("{\"name\": \"x\", \"tags\": [\"a\", \"b\"], \"n\": {\"deep\": [1, 2, 3]}} plain text ${placeholder}; ", 40));
            WildcardEngine engine = Engine();
            Random r = new Random(1);
            Stopwatch clock = Stopwatch.StartNew();
            bool same = true;
            for (int i = 0; i < 2000; i++) same &= ReferenceEquals(engine.Expand(json, r), json);
            clock.Stop();
            Check(same, "a " + json.Length + "-character JSON-heavy text comes back as the same instance");
            Check(clock.ElapsedMilliseconds < 3000, "... at " + (clock.ElapsedMilliseconds / 2000.0).ToString("0.000") + " ms per call");
        }
    }
}
