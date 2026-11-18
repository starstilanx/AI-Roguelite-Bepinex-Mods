using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIROG_Wildcards.Engine
{
    /// <summary>
    /// Wildcard .txt files under one root folder (subfolders allowed), in the layout the A1111
    /// sd-dynamic-prompts extension uses, so an existing wildcards folder can be dropped in.
    /// Files are read on first use, cached, and re-read when their modified time or size changes.
    /// No Unity or game types.
    /// </summary>
    public sealed class WildcardFileStore : IWildcardSource
    {
        private const long MaxFileBytes = 16L * 1024 * 1024;

        private sealed class CachedFile
        {
            public DateTime StampUtc;
            public long Length;
            public List<WildcardEntry> Entries;
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, CachedFile> files = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Regex> globs = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> reportedProblems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string root;
        private List<string> listing;
        private DateTime listingReadUtc;

        /// <summary>Receives load messages and read problems. May be null.</summary>
        public Action<string> Log;

        /// <summary>
        /// How long the list of files used for * and ** patterns is reused before the folder is
        /// scanned again. Individual files are always checked for changes.
        /// </summary>
        public double ListingTtlSeconds = 5.0;

        public WildcardFileStore(string rootFolder)
        {
            SetRoot(rootFolder);
        }

        public string Root
        {
            get { lock (gate) return root; }
        }

        public void SetRoot(string rootFolder)
        {
            string full = Path.GetFullPath(rootFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            lock (gate)
            {
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return;
                root = full;
                files.Clear();
                listing = null;
                reportedProblems.Clear();
            }
        }

        /// <summary>Drops everything cached; the next use reads from disk again.</summary>
        public void Clear()
        {
            lock (gate)
            {
                files.Clear();
                listing = null;
                reportedProblems.Clear();
            }
        }

        /// <summary>Number of .txt files currently under the root folder.</summary>
        public int CountFiles()
        {
            try
            {
                lock (gate)
                {
                    listing = null;
                    return Listing().Count;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public IList<WildcardEntry> Get(string name)
        {
            try
            {
                string rel = NormalizeName(name);
                if (rel == null) return null;
                lock (gate)
                {
                    return rel.IndexOf('*') >= 0 ? LoadGlob(rel) : LoadFile(rel);
                }
            }
            catch (Exception ex)
            {
                ReportOnce("Could not read wildcard '" + name + "': " + ex.Message);
                return null;
            }
        }

        // A problem that persists (a folder we may not read, an oversized file) would otherwise
        // be logged again on every AI request.
        private void ReportOnce(string message)
        {
            lock (gate)
            {
                if (!reportedProblems.Add(message)) return;
            }
            Action<string> log = Log;
            if (log != null) log(message);
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string rel = name.Trim().Replace('\\', '/');
            if (rel.Length == 0 || rel[0] == '/' || rel[rel.Length - 1] == '/') return null;
            if (rel.IndexOf("..", StringComparison.Ordinal) >= 0 || rel.IndexOf(':') >= 0) return null;
            return rel;
        }

        private List<WildcardEntry> LoadFile(string rel)
        {
            string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar) + ".txt"));
            // The engine already refuses names that could climb out of the folder; this is the backstop.
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;

            FileInfo file = new FileInfo(full);
            if (!file.Exists)
            {
                files.Remove(full);
                return null;
            }
            DateTime stamp = file.LastWriteTimeUtc;
            long length = file.Length;
            CachedFile hit;
            if (files.TryGetValue(full, out hit) && hit.StampUtc == stamp && hit.Length == length) return hit.Entries;

            if (length > MaxFileBytes)
            {
                ReportOnce("Wildcard file '" + rel + ".txt' is larger than 16 MB and was skipped.");
                return null;
            }
            List<WildcardEntry> entries;
            try
            {
                entries = Parse(Decode(File.ReadAllBytes(full)));
            }
            catch (IOException)
            {
                // Usually an editor in the middle of saving: keep what we had and try again next time.
                return hit != null ? hit.Entries : null;
            }
            files[full] = new CachedFile { StampUtc = stamp, Length = length, Entries = entries };
            Action<string> log = Log;
            if (log != null) log("Loaded wildcard '" + rel + "' (" + entries.Count.ToString(CultureInfo.InvariantCulture) + " options).");
            return entries;
        }

        private List<WildcardEntry> LoadGlob(string pattern)
        {
            Regex regex;
            if (!globs.TryGetValue(pattern, out regex))
            {
                regex = GlobToRegex(pattern);
                globs[pattern] = regex;
            }
            List<WildcardEntry> all = null;
            foreach (string rel in Listing())
            {
                if (!regex.IsMatch(rel)) continue;
                List<WildcardEntry> entries = LoadFile(rel);
                if (entries == null || entries.Count == 0) continue;
                if (all == null) all = new List<WildcardEntry>();
                all.AddRange(entries);
            }
            return all;
        }

        /// <summary>Every wildcard under the root as "sub/folder/name", sorted.</summary>
        private List<string> Listing()
        {
            DateTime now = DateTime.UtcNow;
            if (listing != null && (now - listingReadUtc).TotalSeconds < ListingTtlSeconds) return listing;

            List<string> names = new List<string>();
            if (Directory.Exists(root))
            {
                foreach (string path in Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories))
                {
                    // On .NET Framework "*.txt" also matches longer extensions such as ".txt_old".
                    if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    rel = rel.Substring(0, rel.Length - 4).Replace('\\', '/');
                    if (rel.Length > 0) names.Add(rel);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
            }
            listing = names;
            listingReadUtc = now;
            return names;
        }

        /// <summary>"*" matches within one folder level, "**" across levels.</summary>
        public static Regex GlobToRegex(string pattern)
        {
            StringBuilder sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c != '*')
                {
                    sb.Append(Regex.Escape(c.ToString()));
                    continue;
                }
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// UTF-8 with or without a byte-order mark, UTF-16 with one, and as a last resort the
        /// single-byte Windows/Latin-1 text old wildcard packs were sometimes saved in.
        /// </summary>
        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Not valid UTF-8.
            }
            try
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
            catch (Exception)
            {
                // Code page 1252 is not available on every runtime (Unity's Mono strips it).
            }
            char[] chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        /// <summary>
        /// One option per line. Blank lines are skipped, lines starting with # are comments, and
        /// a leading "number::" sets the option's weight. Handles CRLF, lone CR and a stray BOM.
        /// </summary>
        public static List<WildcardEntry> Parse(string content)
        {
            List<WildcardEntry> entries = new List<WildcardEntry>();
            if (string.IsNullOrEmpty(content)) return entries;
            int i = content[0] == '\uFEFF' ? 1 : 0;
            int length = content.Length;
            while (i < length)
            {
                int end = i;
                while (end < length && content[end] != '\n' && content[end] != '\r') end++;
                string line = content.Substring(i, end - i).Trim();
                i = end + 1;
                if (line.Length == 0 || line[0] == '#') continue;
                entries.Add(ParseLine(line));
            }
            return entries;
        }

        private static WildcardEntry ParseLine(string line)
        {
            int k = 0;
            while (k < line.Length && line[k] >= '0' && line[k] <= '9') k++;
            if (k < line.Length && line[k] == '.')
            {
                k++;
                while (k < line.Length && line[k] >= '0' && line[k] <= '9') k++;
            }
            if (k > 0)
            {
                int w = k;
                while (w < line.Length && (line[w] == ' ' || line[w] == '\t')) w++;
                double weight;
                if (w + 1 < line.Length && line[w] == ':' && line[w + 1] == ':'
                    && double.TryParse(line.Substring(0, k), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out weight))
                {
                    return new WildcardEntry(line.Substring(w + 2).Trim(), weight);
                }
            }
            return new WildcardEntry(line, 1.0);
        }
    }
}
