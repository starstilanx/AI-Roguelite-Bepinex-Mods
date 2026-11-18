using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AIROG_Wildcards.Engine;
using BepInEx.Configuration;

namespace AIROG_Wildcards
{
    /// <summary>
    /// The one engine and file store the patches and console commands share, plus the single
    /// entry point they expand text through. <see cref="Expand"/> never throws and never returns
    /// null for non-null input: on any problem the caller gets its own text back.
    /// </summary>
    internal static class WildcardRuntime
    {
        private const int LOG_TEXT_CHARS = 600;
        private const int LOG_MAX_ROLLS = 40;

        private static WildcardFileStore store;
        private static WildcardEngine engine;
        private static int seedCounter;
        private static readonly HashSet<string> warned = new HashSet<string>();

        public static void Init()
        {
            store = new WildcardFileStore(WildcardFiles.ResolveWildcardFolder(WildcardsPlugin.WildcardFolder?.Value))
            {
                Log = WildcardsPlugin.LogInfo
            };
            engine = new WildcardEngine(store)
            {
                // The game throws UnreplacedVariableException on any prompt containing "${" or
                // "{$" (AIAsker.GenerateTxtNoTryStrStyle, Utils.GetPreamble), so text pulled in
                // from a wildcard file must never introduce one.
                GuardPlaceholderMarkers = true,
                Warn = WildcardsPlugin.LogWarn
            };
        }

        public static string WildcardFolder => store?.Root ?? WildcardFiles.DefaultWildcardFolder;

        /// <summary>Master switch and the given area switch are both on.</summary>
        public static bool AreaOn(ConfigEntry<bool> area)
        {
            ConfigEntry<bool> enabled = WildcardsPlugin.Enabled;
            return enabled != null && enabled.Value && area != null && area.Value;
        }

        /// <summary>
        /// Expands player-authored text for one AI request. Returns the same string instance
        /// when there is nothing to expand.
        /// </summary>
        public static string Expand(string text, string area)
        {
            if (string.IsNullOrEmpty(text) || !WildcardEngine.MayContainSyntax(text)) return text;
            WildcardEngine current = engine;
            if (current == null) return text;
            try
            {
                ApplyConfig(current);
                bool log = WildcardsPlugin.LogExpansions != null && WildcardsPlugin.LogExpansions.Value;
                ExpansionInfo info = log ? new ExpansionInfo() : null;
                string result = current.Expand(text, NewRandom(), info);
                if (log && !ReferenceEquals(result, text)) LogExpansion(area, text, result, info);
                return result ?? text;
            }
            catch (Exception ex)
            {
                WarnOnce("expand:" + area, "Expansion failed for " + area + "; the text was sent unchanged. " + ex);
                return text;
            }
        }

        /// <summary>One sample expansion for WILDCARD_TEST, with the report of what happened.</summary>
        public static string Sample(string template, ExpansionInfo info)
        {
            WildcardEngine current = engine;
            if (current == null) return template;
            ApplyConfig(current);
            return current.Expand(template, NewRandom(), info);
        }

        /// <summary>Drops cached files and pick history. Returns the number of .txt files found.</summary>
        public static int Reload()
        {
            WildcardEngine currentEngine = engine;
            WildcardFileStore currentStore = store;
            if (currentEngine != null) ApplyConfig(currentEngine);
            if (currentStore != null) currentStore.Clear();
            if (currentEngine != null) currentEngine.ResetState();
            lock (warned) warned.Clear();
            return currentStore != null ? currentStore.CountFiles() : 0;
        }

        public static void WarnOnce(string key, string message)
        {
            lock (warned)
            {
                if (!warned.Add(key)) return;
            }
            WildcardsPlugin.LogWarn(message);
        }

        // Config values are read on every call, so an edit made through a config manager applies
        // to the next request.
        private static void ApplyConfig(WildcardEngine current)
        {
            if (WildcardsPlugin.MaxDepth != null) current.MaxDepth = Math.Max(1, Math.Min(50, WildcardsPlugin.MaxDepth.Value));
            if (WildcardsPlugin.RepeatSuppression != null) current.RepeatSuppression = WildcardsPlugin.RepeatSuppression.Value;
            WildcardFileStore currentStore = store;
            if (currentStore != null && WildcardsPlugin.WildcardFolder != null)
            {
                currentStore.SetRoot(WildcardFiles.ResolveWildcardFolder(WildcardsPlugin.WildcardFolder.Value));
            }
        }

        // A new generator per expansion: System.Random is not thread-safe, and image prompts are
        // not guaranteed to be formatted on the main thread.
        private static Random NewRandom()
        {
            int salt = Interlocked.Increment(ref seedCounter);
            return new Random(unchecked(Guid.NewGuid().GetHashCode() ^ (salt * 486187739)));
        }

        private static void LogExpansion(string area, string before, string after, ExpansionInfo info)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[').Append(area).Append("] ").Append(info.Rolls.Count).Append(" roll(s)");
            for (int i = 0; i < info.Rolls.Count && i < LOG_MAX_ROLLS; i++)
            {
                sb.Append(i == 0 ? ": " : "; ");
                sb.Append(Shorten(info.Rolls[i].Key, 60)).Append(" => \"").Append(Shorten(info.Rolls[i].Value, 120)).Append('"');
            }
            if (info.Rolls.Count > LOG_MAX_ROLLS) sb.Append("; ...");
            if (info.HitLimit) sb.Append(" (depth or size limit reached; some text was left as written)");
            sb.Append("\n    before: ").Append(Shorten(before, LOG_TEXT_CHARS));
            sb.Append("\n    after:  ").Append(Shorten(after, LOG_TEXT_CHARS));
            WildcardsPlugin.LogInfo(sb.ToString());
        }

        internal static string Shorten(string text, int max)
        {
            string flat = (text ?? "").Replace("\r", "").Replace("\n", "\\n");
            if (flat.Length <= max) return flat;
            return flat.Substring(0, max) + "... (+" + (flat.Length - max) + " chars)";
        }
    }
}
