using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AIROG_Core;

namespace AIROG_TavernImport
{
    /// <summary>
    /// Per-save record of which native lore entries each import added, so a whole book can be
    /// removed again. Entries are matched by content: if the player edited an entry's text
    /// afterwards it is theirs now and removal leaves it alone.
    /// </summary>
    public static class ImportLedger
    {
        const string FileName = "tavern_import.json";

        class Data
        {
            public Dictionary<string, List<string>> books = new Dictionary<string, List<string>>();
        }

        static Data _data;
        static string _loadedFor;

        static Data Current
        {
            get
            {
                string dir = ModSaveFile.Dir();
                if (_data == null || _loadedFor != dir)
                {
                    _data = ModSaveFile.LoadJson<Data>(FileName) ?? new Data();
                    _loadedFor = dir;
                }
                return _data;
            }
        }

        public static void Record(string book, IEnumerable<string> addedVals)
        {
            if (ModSaveFile.Dir() == null) return;
            var d = Current;
            if (!d.books.TryGetValue(book, out var list)) d.books[book] = list = new List<string>();
            list.AddRange(addedVals.Where(v => !list.Contains(v)));
            ModSaveFile.SaveJson(FileName, d);
        }

        public static IEnumerable<string> Books() => ModSaveFile.Dir() == null ? Enumerable.Empty<string>() : Current.books.Keys;

        public static int Count(string book) => Current.books.TryGetValue(book, out var l) ? l.Count : 0;

        /// <summary>Case-insensitive lookup so console input doesn't have to match exactly.</summary>
        public static string Resolve(string name)
        {
            if (ModSaveFile.Dir() == null) return null;
            return Current.books.Keys.FirstOrDefault(k => string.Equals(k, name, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Removes the book's entries from the lorebook; returns how many were removed.</summary>
        public static int RemoveFrom(Lorebook lorebook, string book)
        {
            if (!Current.books.TryGetValue(book, out var vals)) return 0;
            var set = new HashSet<string>(vals);
            int removed = lorebook.loreEntries.RemoveAll(e => set.Contains(e.val));
            Current.books.Remove(book);
            ModSaveFile.SaveJson(FileName, Current);
            return removed;
        }

        /// <summary>Names of every imported block found in an Extra Lore string.</summary>
        public static IEnumerable<string> BlocksIn(string extraLore)
        {
            if (string.IsNullOrEmpty(extraLore)) yield break;
            foreach (Match m in Regex.Matches(extraLore, @"=== Lorebook: (.+?) ==="))
                yield return m.Groups[1].Value;
        }
    }
}
