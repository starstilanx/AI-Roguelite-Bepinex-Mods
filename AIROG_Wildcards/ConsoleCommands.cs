using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using AIROG_Wildcards.Engine;

namespace AIROG_Wildcards
{
    /// <summary>
    /// Console commands:
    ///   WILDCARD_TEST &lt;template&gt;  - show three sample expansions of a template
    ///   WILDCARD_RELOAD           - drop cached wildcard files and pick history
    /// They work even when the mod is switched off in the config, so files can be checked first.
    /// </summary>
    internal static class ConsoleCommands
    {
        private const string TEST = "WILDCARD_TEST";
        private const string RELOAD = "WILDCARD_RELOAD";
        private const int SAMPLES = 3;
        private const int MODAL_SAMPLE_CHARS = 500;

        /// <summary>
        /// Prefix on GameplayManager.ProcessConsoleCommand. Every mod's console commands hang off
        /// this one method, so this must return true for anything that is not ours and must never
        /// throw, or it takes the other mods' commands down with it.
        /// </summary>
        public static bool Prefix(string txt)
        {
            string raw = (txt ?? "").Trim();
            bool isReload = string.Equals(raw, RELOAD, StringComparison.OrdinalIgnoreCase);
            bool isTest = raw.StartsWith(TEST, StringComparison.OrdinalIgnoreCase)
                && (raw.Length == TEST.Length || char.IsWhiteSpace(raw[TEST.Length]));
            if (!isReload && !isTest) return true;

            try
            {
                List<string> lines = isReload ? Reload() : Test(raw.Substring(TEST.Length).Trim());
                WildcardsPlugin.LogInfo(string.Join("\n", lines.ToArray()));
                Show(lines);
            }
            catch (Exception ex)
            {
                WildcardsPlugin.LogWarn("Console command '" + raw + "' failed: " + ex);
            }
            return false;
        }

        private static List<string> Reload()
        {
            int count = WildcardRuntime.Reload();
            return new List<string>
            {
                "Wildcard cache cleared.",
                count + " wildcard file(s) in " + WildcardRuntime.WildcardFolder
            };
        }

        private static List<string> Test(string template)
        {
            List<string> lines = new List<string>();
            if (template.Length == 0)
            {
                lines.Add("Usage: WILDCARD_TEST <text>");
                lines.Add("Example: WILDCARD_TEST a {tall|short} woman with __examples/hair_style__");
                lines.Add("Wildcard files are read from " + WildcardRuntime.WildcardFolder);
                return lines;
            }
            if (!WildcardEngine.MayContainSyntax(template))
            {
                lines.Add("No wildcard syntax in that text. It would reach the AI exactly as typed.");
                return lines;
            }

            lines.Add("Template: " + template);
            ExpansionInfo last = null;
            bool changed = false;
            for (int i = 1; i <= SAMPLES; i++)
            {
                last = new ExpansionInfo();
                string result = WildcardRuntime.Sample(template, last);
                changed |= !string.Equals(result, template, StringComparison.Ordinal);
                lines.Add(i + ") " + result);
            }
            if (!changed) lines.Add("Nothing in that text was expanded.");
            if (last != null && last.UnknownWildcards.Count > 0)
            {
                lines.Add("Not found (left as typed): __" + string.Join("__, __", last.UnknownWildcards.ToArray()) + "__");
                lines.Add("Files are read from " + WildcardRuntime.WildcardFolder);
            }
            if (last != null && last.HitLimit)
            {
                lines.Add("The nesting or size limit was reached, so part of the text was left as written.");
            }
            return lines;
        }

        /// <summary>
        /// Shown in a message box rather than the scrolling game log: the log runs every line
        /// through Utils.MarkdownToTmp, which turns __name__ into underlined text and *...* into
        /// italics - exactly the characters this command exists to show. The full, uncut text is
        /// always in BepInEx/LogOutput.log as well.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Show(List<string> lines)
        {
            MessageModal modal = MessageModal.I;
            if (modal == null) return;
            StringBuilder sb = new StringBuilder("<noparse>");
            foreach (string line in lines)
            {
                string shown = line.Length > MODAL_SAMPLE_CHARS
                    ? line.Substring(0, MODAL_SAMPLE_CHARS) + "... (full text in BepInEx/LogOutput.log)"
                    : line;
                // noparse keeps tag-like options such as <lora:x:1> visible instead of parsed away.
                sb.Append(shown.Replace("</noparse>", "")).Append("\n\n");
            }
            sb.Append("</noparse>");
            modal.ShowModal(sb.ToString(), isBig: sb.Length > 300, justified: true);
        }
    }
}
