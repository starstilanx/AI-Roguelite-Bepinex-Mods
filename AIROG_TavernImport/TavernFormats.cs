using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AIROG_TavernImport
{
    /// <summary>One lore entry, normalised across SillyTavern World Info, Card V2/V3
    /// character_book, NovelAI and Agnai memory books.</summary>
    public class TavernLoreEntry
    {
        public string Name = "";
        public List<string> Keys = new List<string>();
        public List<string> SecondaryKeys = new List<string>();
        public string Content = "";
        public bool Constant;
        public bool Enabled = true;
        public int Order = 100;
    }

    public class TavernBook
    {
        public string Name = "";
        public List<TavernLoreEntry> Entries = new List<TavernLoreEntry>();
    }

    public class TavernCard
    {
        public string Spec = "";
        public string Name = "";
        public string Description = "";
        public string Personality = "";
        public string Scenario = "";
        public string FirstMes = "";
        public string MesExample = "";
        public string CreatorNotes = "";
        public TavernBook Book;
        /// <summary>Raw bytes of the PNG the card came from, for use as the portrait.</summary>
        public byte[] ImageBytes;
    }

    /// <summary>
    /// Reads cards and lorebooks from disk. Everything here is pure parsing: no game state is
    /// touched, so it is safe to call from anywhere and easy to reason about when a file fails.
    /// </summary>
    public static class TavernReader
    {
        static readonly byte[] PngSig = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>A card file (.png with an embedded card, or .json). Throws with a readable
        /// message when the file is not a card.</summary>
        public static TavernCard ReadCard(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            JObject root;
            byte[] image = null;
            if (IsPng(bytes))
            {
                string json = ExtractCardJsonFromPng(bytes);
                if (json == null)
                    throw new InvalidDataException("This PNG has no character card data embedded in it (no 'chara' or 'ccv3' text chunk).");
                root = JObject.Parse(json);
                image = bytes;
            }
            else
            {
                root = JObject.Parse(DecodeText(bytes));
                if (!LooksLikeCard(root))
                    throw new InvalidDataException(root["entries"] != null
                        ? "That file is a lorebook, not a character card. Use Import Lorebook (journal or new-game Lorebook tab) or TAVERN LORE."
                        : "That JSON file isn't a character card.");
            }
            var card = ParseCard(root, Path.GetFileNameWithoutExtension(path));
            card.ImageBytes = image;
            return card;
        }

        /// <summary>A lorebook file. Accepts standalone lorebooks (ST, V2 book, NovelAI, Agnai)
        /// and also card files, in which case the card's embedded character_book is returned.</summary>
        public static TavernBook ReadBook(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string fallbackName = Path.GetFileNameWithoutExtension(path);
            if (IsPng(bytes))
            {
                var card = ReadCard(path);
                if (card.Book == null || card.Book.Entries.Count == 0)
                    throw new InvalidDataException($"The card '{card.Name}' has no embedded lorebook.");
                return card.Book;
            }
            var root = JObject.Parse(DecodeText(bytes));
            if (LooksLikeCard(root))
            {
                var card = ParseCard(root, fallbackName);
                if (card.Book == null || card.Book.Entries.Count == 0)
                    throw new InvalidDataException($"The card '{card.Name}' has no embedded lorebook.");
                return card.Book;
            }
            var book = ParseBook(root, fallbackName);
            if (book == null)
                throw new InvalidDataException("This file doesn't look like a lorebook (no 'entries' found).");
            return book;
        }

        // ---- PNG --------------------------------------------------------------------------

        public static bool IsPng(byte[] b)
        {
            if (b == null || b.Length < PngSig.Length) return false;
            for (int i = 0; i < PngSig.Length; i++) if (b[i] != PngSig[i]) return false;
            return true;
        }

        /// <summary>Walks the PNG chunk list for tEXt/zTXt/iTXt chunks keyed 'ccv3' (V3) or
        /// 'chara' (V1/V2). V3 wins when both are present, per the V3 spec.</summary>
        public static string ExtractCardJsonFromPng(byte[] b)
        {
            string chara = null, ccv3 = null;
            int pos = 8;
            while (pos + 8 <= b.Length)
            {
                int len = (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
                string type = Encoding.ASCII.GetString(b, pos + 4, 4);
                int dataStart = pos + 8;
                // Compared this way round so a corrupt length near int.MaxValue cannot overflow past the check.
                if (len < 0 || len > b.Length - dataStart) break;

                if (type == "tEXt" || type == "zTXt" || type == "iTXt")
                {
                    var (key, text) = ReadTextChunk(type, b, dataStart, len);
                    if (key != null)
                    {
                        string k = key.ToLowerInvariant();
                        if (k == "ccv3") ccv3 = text;
                        else if (k == "chara") chara = text;
                    }
                }
                if (type == "IEND") break;
                pos = dataStart + len + 4; // + CRC
            }
            return DecodeCardPayload(ccv3) ?? DecodeCardPayload(chara);
        }

        static (string key, string text) ReadTextChunk(string type, byte[] b, int start, int len)
        {
            int end = start + len;
            int nul = Array.IndexOf(b, (byte)0, start, len);
            if (nul < 0) return (null, null);
            string key = Latin1(b, start, nul - start);
            int p = nul + 1;
            try
            {
                switch (type)
                {
                    case "tEXt":
                        return (key, Latin1(b, p, end - p));
                    case "zTXt":
                        return (key, Latin1(Inflate(b, p + 1, end - p - 1)));
                    case "iTXt":
                    {
                        bool compressed = b[p] == 1;
                        p += 2; // compression flag + method
                        int langEnd = Array.IndexOf(b, (byte)0, p, end - p);
                        if (langEnd < 0) return (null, null);
                        int transEnd = Array.IndexOf(b, (byte)0, langEnd + 1, end - langEnd - 1);
                        if (transEnd < 0) return (null, null);
                        p = transEnd + 1;
                        byte[] raw = compressed ? Inflate(b, p, end - p) : Sub(b, p, end - p);
                        return (key, Encoding.UTF8.GetString(raw));
                    }
                }
            }
            catch (Exception ex)
            {
                TavernImportPlugin.LogWarn($"Could not read PNG {type} chunk '{key}': {ex.Message}");
            }
            return (null, null);
        }

        /// <summary>PNG text chunks are Latin-1. Decoded by hand: Unity's Mono may not ship the
        /// I18N code pages Encoding.GetEncoding("ISO-8859-1") needs.</summary>
        static string Latin1(byte[] b, int start, int len)
        {
            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = (char)b[start + i];
            return new string(chars);
        }

        static string Latin1(byte[] b) => Latin1(b, 0, b.Length);

        static byte[] Sub(byte[] b, int start, int len)
        {
            var r = new byte[len];
            Buffer.BlockCopy(b, start, r, 0, len);
            return r;
        }

        /// <summary>zlib stream: skip the 2-byte header, DeflateStream handles the rest.</summary>
        static byte[] Inflate(byte[] b, int start, int len)
        {
            using (var input = new MemoryStream(b, start + 2, len - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        /// <summary>Card payloads are base64'd UTF-8 JSON; a few tools write raw JSON instead.</summary>
        static string DecodeCardPayload(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("{")) return s;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch (FormatException) { return null; }
        }

        static string DecodeText(byte[] bytes)
        {
            // UTF-8 with or without BOM covers every exporter in practice.
            string s = Encoding.UTF8.GetString(bytes);
            return s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
        }

        // ---- Cards ------------------------------------------------------------------------

        static bool LooksLikeCard(JObject root)
        {
            string spec = Str(root, "spec");
            if (spec.StartsWith("chara_card", StringComparison.OrdinalIgnoreCase)) return true;
            if (root["data"] is JObject d && d["name"] != null && (d["description"] != null || d["first_mes"] != null)) return true;
            return root["name"] != null && (root["first_mes"] != null || root["char_greeting"] != null)
                || root["char_name"] != null;
        }

        public static TavernCard ParseCard(JObject root, string fallbackName)
        {
            // V2/V3 nest everything under "data"; V1 (and old TavernAI/Pygmalion) is flat.
            JObject d = root["data"] as JObject ?? root;
            var card = new TavernCard
            {
                Spec = Str(root, "spec").Length > 0 ? Str(root, "spec") : "chara_card_v1",
                Name = CardMapper.CleanName(FirstNonEmpty(Str(d, "name"), Str(d, "char_name"), fallbackName)),
                Description = FirstNonEmpty(Str(d, "description"), Str(d, "char_persona")),
                Personality = Str(d, "personality"),
                Scenario = FirstNonEmpty(Str(d, "scenario"), Str(d, "world_scenario")),
                FirstMes = FirstNonEmpty(Str(d, "first_mes"), Str(d, "char_greeting")),
                MesExample = FirstNonEmpty(Str(d, "mes_example"), Str(d, "example_dialogue")),
                CreatorNotes = Str(d, "creator_notes"),
            };
            if (d["character_book"] is JObject cb)
            {
                card.Book = ParseBook(cb, card.Name);
                if (card.Book != null && string.IsNullOrWhiteSpace(Str(cb, "name")))
                    card.Book.Name = card.Name;
            }
            return card;
        }

        // ---- Lorebooks --------------------------------------------------------------------

        public static TavernBook ParseBook(JObject root, string fallbackName)
        {
            JToken entries = root["entries"];
            if (entries == null || entries.Type == JTokenType.Null) return null;

            var book = new TavernBook
            {
                Name = FirstNonEmpty(Str(root, "name"), Str(root["originalData"] as JObject, "name"), fallbackName)
            };

            // ST World Info: object keyed by uid. V2 book / NovelAI / Agnai: array.
            IEnumerable<JToken> items = entries is JObject obj
                ? obj.Properties().Select(p => p.Value)
                : entries as JArray ?? Enumerable.Empty<JToken>();

            foreach (var t in items)
            {
                if (!(t is JObject e)) continue;
                var entry = new TavernLoreEntry
                {
                    Name = FirstNonEmpty(Str(e, "comment"), Str(e, "name"), Str(e, "displayName")),
                    Keys = StrList(e, "key", "keys", "keywords"),
                    SecondaryKeys = StrList(e, "keysecondary", "secondary_keys"),
                    Content = FirstNonEmpty(Str(e, "content"), Str(e, "text"), Str(e, "entry"), Str(e, "value")),
                    Constant = Bool(e, "constant", false),
                    Order = Int(e, Int(e, 100, "insertion_order"), "order"),
                };
                // ST writes "disable": true; V2/NovelAI/Agnai write "enabled": false.
                entry.Enabled = Bool(e, "enabled", true) && !Bool(e, "disable", false);
                book.Entries.Add(entry);
            }
            return book;
        }

        // ---- JSON helpers -----------------------------------------------------------------

        static string Str(JObject o, string key)
        {
            if (o == null) return "";
            var t = o[key];
            return t == null || t.Type == JTokenType.Null ? "" : (t.Type == JTokenType.String ? (string)t : t.ToString());
        }

        static List<string> StrList(JObject o, params string[] keys)
        {
            foreach (var k in keys)
            {
                var t = o[k];
                if (t == null || t.Type == JTokenType.Null) continue;
                // Only plain values count as keys: a nested object or array would otherwise become
                // a JSON-text "key", or (outside an array) abort the whole import on the cast.
                IEnumerable<string> raw = t is JArray arr
                    ? arr.Where(x => x is JValue).Select(x => x.Type == JTokenType.String ? (string)x : x.ToString())
                    : t is JValue ? t.ToString().Split(',') // some exporters write "a, b, c"
                    : Enumerable.Empty<string>();
                var list = raw.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).ToList();
                if (list.Count > 0) return list;
            }
            return new List<string>();
        }

        static bool Bool(JObject o, string key, bool def)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type == JTokenType.Integer) return (long)t != 0;
            return bool.TryParse(t.ToString(), out bool b) ? b : def;
        }

        static int Int(JObject o, int def, string key)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            return int.TryParse(t.ToString(), out int v) ? v : def;
        }

        static string FirstNonEmpty(params string[] s) => s.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    }
}
