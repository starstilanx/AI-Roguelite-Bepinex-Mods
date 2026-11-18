using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIROG_TavernImport
{
    public enum ConstantEntryMode
    {
        /// <summary>Always-on entries go into the scenario's Extra Lore, which the game injects
        /// into every story prompt. Closest match to ST's "constant" behaviour.</summary>
        ExtraLore,
        /// <summary>Treat constant entries like keyed ones (dropped if they have no keys).</summary>
        Keyed,
        Skip,
    }

    public class LoreMergeResult
    {
        public string BookName = "";
        public int Added, Updated, Constants, SkippedDisabled, SkippedNoKeys, SecondaryIgnored;
        public bool ConstantsTruncated;
        /// <summary>Content of entries this import newly added, for the per-save undo ledger.</summary>
        public List<string> AddedVals = new List<string>();

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"Lorebook '{BookName}': {Added} entr{(Added == 1 ? "y" : "ies")} added");
            if (Updated > 0) sb.Append($", {Updated} already present (keys merged)");
            if (Constants > 0) sb.Append($", {Constants} always-on entr{(Constants == 1 ? "y" : "ies")} written to Extra Lore");
            sb.Append('.');
            if (ConstantsTruncated) sb.Append($"\n  Always-on text exceeded {TavernImportPlugin.MaxConstantChars.Value} chars and was truncated.");
            if (SkippedDisabled > 0) sb.Append($"\n  Skipped {SkippedDisabled} disabled entr{(SkippedDisabled == 1 ? "y" : "ies")}.");
            if (SkippedNoKeys > 0) sb.Append($"\n  Skipped {SkippedNoKeys} entr{(SkippedNoKeys == 1 ? "y" : "ies")} with no trigger keys.");
            if (SecondaryIgnored > 0) sb.Append($"\n  {SecondaryIgnored} entr{(SecondaryIgnored == 1 ? "y uses" : "ies use")} secondary keys; the game has no AND/NOT logic, so they trigger on primary keys alone.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Merges a TavernBook into a native keyword lorebook plus an Extra Lore string. Operates on
    /// plain lists and strings so the same code serves the in-game journal (live universe) and
    /// the new-game screen (UI-staged lorebook).
    /// </summary>
    public static class LoreMerger
    {
        public static LoreMergeResult Merge(List<Lorebook.LoreEntry> target, ref string extraLore,
            TavernBook book, string charName, string playerName)
        {
            var r = new LoreMergeResult { BookName = book.Name };
            var constants = new List<TavernLoreEntry>();

            foreach (var e in book.Entries)
            {
                if (!e.Enabled && TavernImportPlugin.SkipDisabledEntries.Value) { r.SkippedDisabled++; continue; }
                string content = CardMapper.Macros(e.Content, charName, playerName).Trim();
                if (content.Length == 0) continue;

                var keys = e.Keys.Select(k => CardMapper.Macros(k, charName, playerName).Trim())
                    .Where(k => k.Length > 0).Distinct().ToList();

                if (e.Constant)
                {
                    var mode = TavernImportPlugin.ConstantEntries.Value;
                    if (mode == ConstantEntryMode.Skip) continue;
                    if (mode == ConstantEntryMode.ExtraLore) { constants.Add(new TavernLoreEntry { Content = content, Order = e.Order }); continue; }
                }

                if (keys.Count == 0) { r.SkippedNoKeys++; continue; }
                if (e.SecondaryKeys.Count > 0) r.SecondaryIgnored++;

                var existing = target.FirstOrDefault(x => x.val == content);
                if (existing != null)
                {
                    var merged = existing.strKeys.Concat(existing.regKeys).Concat(keys).Distinct().ToList();
                    existing.ParseKeys(string.Join("\n", merged));
                    r.Updated++;
                    continue;
                }
                target.Add(new Lorebook.LoreEntry(string.Join("\n", keys), content));
                r.AddedVals.Add(content);
                r.Added++;
            }

            if (constants.Count > 0)
            {
                // ST inserts lower "order" first, so keep that reading order.
                string body = string.Join("\n\n", constants.OrderBy(c => c.Order).Select(c => c.Content));
                int max = TavernImportPlugin.MaxConstantChars.Value;
                if (max > 0 && body.Length > max) { body = CardMapper.Truncate(body, max); r.ConstantsTruncated = true; }
                extraLore = SetBlock(extraLore, book.Name, body);
                r.Constants = constants.Count;
            }
            return r;
        }

        // ---- Extra Lore blocks ------------------------------------------------------------
        // Each imported book owns one delimited block, so a re-import replaces it in place and
        // TAVERN REMOVE can strip it without touching the player's own Extra Lore text.

        static string Header(string name) => $"=== Lorebook: {name} ===";
        static string Footer(string name) => $"=== End of {name} ===";

        static Regex BlockRegex(string name) => new Regex(
            @"\n*" + Regex.Escape(Header(name)) + @"[\s\S]*?" + Regex.Escape(Footer(name)) + @"\n*");

        public static string SetBlock(string extraLore, string name, string body)
        {
            string stripped = RemoveBlock(extraLore, name);
            string block = Header(name) + "\n" + body.Trim() + "\n" + Footer(name);
            return stripped.Length == 0 ? block : stripped.TrimEnd() + "\n\n" + block;
        }

        public static string RemoveBlock(string extraLore, string name)
        {
            if (string.IsNullOrEmpty(extraLore)) return "";
            return BlockRegex(name).Replace(extraLore, "\n\n").Trim();
        }

        public static bool HasBlock(string extraLore, string name) =>
            !string.IsNullOrEmpty(extraLore) && BlockRegex(name).IsMatch(extraLore);
    }
}
