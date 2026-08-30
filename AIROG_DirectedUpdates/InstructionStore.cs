using System;
using System.Collections.Generic;
using AIROG_Core;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// Holds GM-issued update instructions between the unified (first-pass) response and
    /// the per-entity state-update call, keyed by entity uuid. Optionally persisted to the
    /// active save directory so instructions survive a save/quit while a "confirm details
    /// updated" icon is still pending.
    /// </summary>
    internal static class InstructionStore
    {
        private const string FILE_NAME = "directed_updates_pending.json";

        public class Pending
        {
            public string name;
            public string instruction;
            public DateTime createdUtc;
        }

        private static Dictionary<string, Pending> map = new Dictionary<string, Pending>();
        private static string loadedDir;

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

        public static void Put(string uuid, string entityName, string instruction)
        {
            if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(instruction)) return;
            EnsureLoaded();
            PurgeExpired();
            map[uuid] = new Pending { name = entityName, instruction = instruction, createdUtc = DateTime.UtcNow };
            Persist();
        }

        /// <summary>Removes and returns the pending instruction for an entity, if any.</summary>
        public static bool TryTake(string uuid, out string instruction)
        {
            instruction = null;
            if (!TryPeek(uuid, out instruction, out string _)) return false;
            map.Remove(uuid);
            Persist();
            return true;
        }

        /// <summary>
        /// Reads the pending instruction for an entity without consuming it. Used by the
        /// tooltip hook, which runs while the instruction still has to survive for the
        /// (possibly player-confirmed) state-update call.
        /// </summary>
        public static bool TryPeek(string uuid, out string instruction, out string entityName)
        {
            instruction = null;
            entityName = null;
            if (string.IsNullOrEmpty(uuid)) return false;
            EnsureLoaded();
            PurgeExpired();
            if (!map.TryGetValue(uuid, out Pending p) || p == null || string.IsNullOrEmpty(p.instruction)) return false;
            instruction = p.instruction;
            entityName = p.name;
            return true;
        }
    }
}
