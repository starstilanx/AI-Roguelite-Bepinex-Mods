using System;
using System.Collections.Generic;
using AIROG_Core;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// Hand-off between the unified (first-pass) response parser and the icon the game raises
    /// for that entry, keyed by kind + entity uuid. An entry only lives from ProcessApiAction
    /// until GameLogViewObj.AddAiDecisionIcon a few moments later in the same turn; from then
    /// on the text rides on the icon itself (see DirectedUpdatesPlugin.Stash). Optionally
    /// persisted to the active save directory.
    /// </summary>
    internal static class InstructionStore
    {
        public const string KIND_INSTRUCTION = "instruction";
        public const string KIND_DESTINATION = "destination";

        private const string FILE_NAME = "directed_updates_pending.json";

        public class Pending
        {
            public string name;
            public string instruction;
            public DateTime createdUtc;
        }

        private static Dictionary<string, Pending> map = new Dictionary<string, Pending>();
        private static string loadedDir;

        // Instructions keep the bare uuid so a file written by an older version still reads.
        private static string Key(string kind, string uuid) => kind == KIND_INSTRUCTION ? uuid : kind + ":" + uuid;

        private static void EnsureLoaded()
        {
            string dir = ModSaveFile.Dir();
            if (dir == loadedDir) return;
            map = (dir != null && DirectedUpdatesPlugin.PersistPending.Value
                ? ModSaveFile.LoadJson<Dictionary<string, Pending>>(FILE_NAME)
                : null) ?? new Dictionary<string, Pending>();
            loadedDir = dir;
        }

        private static void PurgeExpired()
        {
            int mins = DirectedUpdatesPlugin.ExpiryMinutes.Value;
            if (mins <= 0) return;
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-mins);
            List<string> stale = null;
            foreach (KeyValuePair<string, Pending> kv in map)
            {
                if (kv.Value == null || kv.Value.createdUtc < cutoff)
                    (stale ?? (stale = new List<string>())).Add(kv.Key);
            }
            if (stale == null) return;
            foreach (string key in stale) map.Remove(key);
        }

        private static void Persist()
        {
            if (!DirectedUpdatesPlugin.PersistPending.Value) return;
            if (ModSaveFile.Dir() == null) return;
            ModSaveFile.SaveJson(FILE_NAME, map);
        }

        public static void Put(string kind, string uuid, string entityName, string text)
        {
            if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(text)) return;
            EnsureLoaded();
            PurgeExpired();
            map[Key(kind, uuid)] = new Pending { name = entityName, instruction = text, createdUtc = DateTime.UtcNow };
            Persist();
        }

        /// <summary>
        /// Drops whatever is pending for an entity. Called when a newer GM reply lists the
        /// entity without the field, so an entry that never got an icon (the game cancels the
        /// update when the entity also died, for example) cannot attach itself to this one.
        /// </summary>
        public static void Remove(string kind, string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            EnsureLoaded();
            if (map.Remove(Key(kind, uuid))) Persist();
        }

        /// <summary>Removes and returns the pending text for an entity, if any.</summary>
        public static bool TryTake(string kind, string uuid, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(uuid)) return false;
            EnsureLoaded();
            PurgeExpired();
            string key = Key(kind, uuid);
            if (!map.TryGetValue(key, out Pending p)) return false;
            map.Remove(key);
            Persist();
            if (p == null || string.IsNullOrEmpty(p.instruction)) return false;
            text = p.instruction;
            return true;
        }
    }
}
