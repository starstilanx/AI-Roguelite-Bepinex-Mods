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
        public string Description => "Injects hidden motives about NPCs and places when the player has gained Insight on them, and requests new insights after enough interactions.";

        // Must match the corresponding constants in AIROG_Insight.InsightData
        private const int InsightThreshold = 3;
        private const int DeepenThreshold = 4;
        private const int PlaceInsightThreshold = 5;

        private class InsightDataStub
        {
#pragma warning disable 0649 // assigned by Newtonsoft.Json during deserialization
            public Dictionary<string, string> NpcInsights;
            public Dictionary<string, string> PlaceInsights;
            public Dictionary<string, int> ConversationCounts;
            public Dictionary<string, int> InsightGainedAtCount;
            public Dictionary<string, int> PlaceInteractionCounts;
            public string ActiveNpcUuid;
#pragma warning restore 0649
        }

        private InsightDataStub _insightCache = new InsightDataStub();
        private string _cachePath;
        private DateTime _cacheStamp;

        public string GetContext(string prompt, int maxTokens)
        {
            try
            {
                return BuildContext();
            }
            catch (Exception ex)
            {
                // Runs inside the game's prompt builder: a throw here would lose the whole turn.
                Debug.LogWarning($"[GenContext] Insight context error: {ex.Message}");
                return "";
            }
        }

        private string BuildContext()
        {
            var manager = SS.I != null ? SS.I.hackyManager : null;
            if (manager == null) return "";

            RefreshCache();

            string context = "";

            // ── NPC Insight ─────────────────────────────────────────────────────
            // The NPC this turn is aimed at, as recorded by AIROG_Insight at the start of the turn.
            // (npcActionsHandler.currentNpc is never assigned by the game any more.)
            string npcUuid = _insightCache.ActiveNpcUuid;
            if (!string.IsNullOrEmpty(npcUuid))
            {
                string existingInsight = null;
                bool hasInsight = _insightCache.NpcInsights != null &&
                                  _insightCache.NpcInsights.TryGetValue(npcUuid, out existingInsight) &&
                                  !string.IsNullOrEmpty(existingInsight);

                int convCount = 0;
                _insightCache.ConversationCounts?.TryGetValue(npcUuid, out convCount);

                if (hasInsight)
                {
                    // Already have an insight — inject it silently into the AI context
                    context += $"\n[PLAYER INSIGHT — HIDDEN FROM NARRATIVE: {existingInsight}]\n";

                    // Enough further conversations? Ask the AI to reveal a deeper layer.
                    int gainedAt = 0;
                    _insightCache.InsightGainedAtCount?.TryGetValue(npcUuid, out gainedAt);
                    if (convCount - gainedAt >= DeepenThreshold)
                    {
                        context += "\n[INSIGHT DIRECTIVE — HIDDEN FROM PLAYER]\n" +
                                   "The player's bond with this character has deepened since their last insight. " +
                                   "Building on what is already known (above), output a hidden <NPC_INSIGHT>one NEW sentence " +
                                   "revealing a deeper, previously unhinted layer of their motives, secrets, or true nature</NPC_INSIGHT> " +
                                   "at the start of your response. Do not repeat known insights. This block is stripped before display.\n";
                    }
                }
                else if (convCount >= InsightThreshold)
                {
                    // Ask the AI to generate a first insight this turn — InsightPlugin will extract and save it
                    context += "\n[INSIGHT DIRECTIVE — HIDDEN FROM PLAYER]\n" +
                               "The player has spent significant time with this character. " +
                               "Based on what has unfolded so far, output a hidden <NPC_INSIGHT>one sentence revealing " +
                               "their deepest hidden motive, secret, or true nature</NPC_INSIGHT> " +
                               "at the start of your response. This block is stripped before display.\n";
                }
            }

            // ── Location Insight ─────────────────────────────────────────────────
            if (manager.currentPlace != null)
            {
                string placeUuid = manager.currentPlace.uuid;
                string placeInsight = null;
                bool hasPlaceInsight = _insightCache.PlaceInsights != null &&
                                       _insightCache.PlaceInsights.TryGetValue(placeUuid, out placeInsight) &&
                                       !string.IsNullOrEmpty(placeInsight);

                if (hasPlaceInsight)
                {
                    context += $"\n[LOCATION INSIGHT — HIDDEN FROM NARRATIVE: {placeInsight}]\n";
                }
                else
                {
                    int placeCount = 0;
                    _insightCache.PlaceInteractionCounts?.TryGetValue(placeUuid, out placeCount);
                    if (placeCount >= PlaceInsightThreshold)
                    {
                        context += "\n[INSIGHT DIRECTIVE — HIDDEN FROM PLAYER]\n" +
                                   "The player has explored this location extensively. " +
                                   "Output a hidden <PLACE_INSIGHT>one sentence revealing a hidden truth, secret history, " +
                                   "or concealed danger of this location</PLACE_INSIGHT> " +
                                   "at the start of your response. This block is stripped before display.\n";
                    }
                }
            }

            return context;
        }

        /// <summary>
        /// Re-reads insight_data.json whenever it changed on disk. AIROG_Insight rewrites it at the
        /// start of every turn (who the turn is aimed at, updated counts) and the prompt is built
        /// moments later, so a time-based cache would hand the AI the previous turn's target.
        /// </summary>
        private void RefreshCache()
        {
            string path = null;
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "insight_data.json");

            if (path == null || !File.Exists(path))
            {
                // No file for this save (yet): never keep serving another save's insights
                _insightCache = new InsightDataStub();
                _cachePath = null;
                return;
            }

            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (path == _cachePath && stamp == _cacheStamp) return;
            _cachePath = path;
            _cacheStamp = stamp;

            try
            {
                var loaded = JsonConvert.DeserializeObject<InsightDataStub>(File.ReadAllText(path));
                _insightCache = loaded ?? new InsightDataStub();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load Insight data: {ex.Message}");
            }
        }
    }
}
