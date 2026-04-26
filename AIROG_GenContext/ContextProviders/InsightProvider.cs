using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.ContextProviders
{
    public class InsightProvider : IContextProvider
    {
        public int Priority => 150;
        public string Name => "Insight";
        public string Description => "Injects hidden motives and insights about NPCs or Locations if the player has gained Insight on them.";

        private class InsightDataStub
        {
            public Dictionary<string, string> NpcInsights;
            public Dictionary<string, string> PlaceInsights;
        }

        private InsightDataStub _insightCache = new InsightDataStub();
        private float _lastLoadTime = 0;
        private const float CACHE_REFRESH_RATE = 5f;

        public string GetContext(string prompt, int maxTokens)
        {
            var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
            if (manager == null) return "";

            RefreshCacheIfNeeded();

            string context = "";

            // Check NPC Insight
            if (manager.npcActionsHandler != null && manager.npcActionsHandler.currentNpc != null)
            {
                var npc = manager.npcActionsHandler.currentNpc;
                if (_insightCache.NpcInsights != null && _insightCache.NpcInsights.TryGetValue(npc.uuid, out var insight))
                {
                    context += $"\n[PLAYER INSIGHT (HIDDEN MOTIVE): {insight}]\n";
                }
            }

            // Check Location Insight
            if (manager.currentPlace != null)
            {
                if (_insightCache.PlaceInsights != null && _insightCache.PlaceInsights.TryGetValue(manager.currentPlace.uuid, out var placeInsight))
                {
                    context += $"\n[LOCATION INSIGHT: {placeInsight}]\n";
                }
            }

            return context;
        }

        private void RefreshCacheIfNeeded()
        {
            if (Time.time - _lastLoadTime > CACHE_REFRESH_RATE)
            {
                LoadData();
                _lastLoadTime = Time.time;
            }
        }

        private void LoadData()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;

            string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "insight_data.json");
            if (!File.Exists(path)) return;

            try
            {
                var loaded = JsonConvert.DeserializeObject<InsightDataStub>(File.ReadAllText(path));
                if (loaded != null)
                {
                    _insightCache = loaded;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load Insight data: {ex.Message}");
            }
        }
    }
}
