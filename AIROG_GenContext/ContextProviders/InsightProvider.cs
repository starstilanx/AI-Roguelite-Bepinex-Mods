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
        public string Description => "Injects hidden motives about NPCs when the player has gained Insight on them, and requests new insights after enough conversations.";

        private const int InsightThreshold = 3; // Must match InsightData.InsightThreshold
        private const string OpenTag = "<NPC_INSIGHT>";
        private const string CloseTag = "</NPC_INSIGHT>";

        private class InsightDataStub
        {
            public Dictionary<string, string> NpcInsights;
            public Dictionary<string, string> PlaceInsights;
            public Dictionary<string, int> ConversationCounts;
        }

        private InsightDataStub _insightCache = new InsightDataStub();
        private float _lastLoadTime = -999f;
        private const float CACHE_REFRESH_RATE = 5f;

        public string GetContext(string prompt, int maxTokens)
        {
            var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
            if (manager == null) return "";

            RefreshCacheIfNeeded();

            string context = "";

            // ── NPC Insight ─────────────────────────────────────────────────────
            var npc = manager.npcActionsHandler?.currentNpc;
            if (npc != null)
            {
                string existingInsight = null;
                bool hasInsight = _insightCache.NpcInsights != null &&
                                  _insightCache.NpcInsights.TryGetValue(npc.uuid, out existingInsight);

                if (hasInsight && !string.IsNullOrEmpty(existingInsight))
                {
                    // Already have an insight — inject it silently into the AI context
                    context += $"\n[PLAYER INSIGHT — HIDDEN FROM NARRATIVE: {existingInsight}]\n";
                }
                else
                {
                    // Check if enough conversations have happened to earn an insight
                    int convCount = 0;
                    _insightCache.ConversationCounts?.TryGetValue(npc.uuid, out convCount);

                    if (convCount >= InsightThreshold)
                    {
                        // Ask the AI to generate an insight this turn — InsightPlugin will extract and save it
                        context += $"\n[INSIGHT DIRECTIVE — HIDDEN FROM PLAYER]\n" +
                                   $"The player has spent significant time with this character. " +
                                   $"Based on what has unfolded so far, output a hidden {OpenTag}one sentence revealing " +
                                   $"their deepest hidden motive, secret, or true nature{CloseTag} " +
                                   $"at the start of your response. This block is stripped before display.\n";
                    }
                }
            }

            // ── Location Insight ─────────────────────────────────────────────────
            if (manager.currentPlace != null)
            {
                if (_insightCache.PlaceInsights != null &&
                    _insightCache.PlaceInsights.TryGetValue(manager.currentPlace.uuid, out var placeInsight))
                {
                    context += $"\n[LOCATION INSIGHT — HIDDEN FROM NARRATIVE: {placeInsight}]\n";
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
                if (loaded != null) _insightCache = loaded;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load Insight data: {ex.Message}");
            }
        }
    }
}
