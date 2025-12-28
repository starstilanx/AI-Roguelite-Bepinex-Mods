using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AIROG_HistoryTab
{
    public static class HistoryData
    {
        private static readonly ConditionalWeakTable<UniverseInfo, HistoryEntry> _universeHistoryMap = new ConditionalWeakTable<UniverseInfo, HistoryEntry>();

        public class HistoryEntry
        {
            public string HistoryText { get; set; } = "";
            public string UniverseUuid { get; set; }
        }

        public static string LastGeneratedHistory { get; set; }

        public static string GetHistory(UniverseInfo universe)
        {
            if (universe == null) return "";
            if (_universeHistoryMap.TryGetValue(universe, out var entry))
            {
                return entry.HistoryText;
            }
            return "";
        }

        public static void SetHistory(UniverseInfo universe, string history)
        {
            if (universe == null) return;
            var entry = _universeHistoryMap.GetOrCreateValue(universe);
            entry.HistoryText = history;
            entry.UniverseUuid = universe.uuid;
        }

        public static void Save(string saveDir)
        {
            try
            {
                string filePath = Path.Combine(saveDir, "universe_history.json");
                var dataList = new List<HistoryEntry>();
                
                // Note: ConditionalWeakTable is hard to iterate. 
                // We might need to keep a separate list of weak references or handle this differently.
                // For now, let's assume we can get all universes from the player.
                var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
                if (manager?.playerCharacter?.pcGameEntity?.universes != null)
                {
                    foreach (var uni in manager.playerCharacter.pcGameEntity.universes)
                    {
                        if (_universeHistoryMap.TryGetValue(uni, out var entry))
                        {
                            dataList.Add(entry);
                        }
                    }
                }

                File.WriteAllText(filePath, Newtonsoft.Json.JsonConvert.SerializeObject(dataList));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("Failed to save history: " + ex.Message);
            }
        }

        public static void Load(string saveDir)
        {
            try
            {
                string filePath = Path.Combine(saveDir, "universe_history.json");
                if (!File.Exists(filePath)) return;

                var dataList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryEntry>>(File.ReadAllText(filePath));
                if (dataList == null) return;

                var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
                if (manager?.playerCharacter?.pcGameEntity?.universes != null)
                {
                    foreach (var entry in dataList)
                    {
                        var uni = manager.playerCharacter.pcGameEntity.universes.Find(u => u.uuid == entry.UniverseUuid);
                        if (uni != null)
                        {
                            SetHistory(uni, entry.HistoryText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("Failed to load history: " + ex.Message);
            }
        }
    }
}
