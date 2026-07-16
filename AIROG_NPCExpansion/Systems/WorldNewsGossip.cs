using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Bridges AIROG_WorldExpansion's background world simulation into the rumor network:
    /// recent world events (wars, successions, conquests, dominion news) get seeded as
    /// "News:" facts on nearby NPCs, then spread NPC-to-NPC via RumorNetwork like any
    /// other fact — so the world sim reaches the player through NPC mouths, not just a UI tab.
    ///
    /// Soft dependency: reads world_expansion_data.json from the save dir (the same
    /// file-based contract GenContext uses). No-op if WorldExpansion isn't installed.
    /// </summary>
    public static class WorldNewsGossip
    {
        private const float  SEED_CHANCE       = 0.5f;  // per rumor tick
        private const int    EVENT_WINDOW      = 25;    // only events this many turns old
        private const double CACHE_SECONDS     = 15.0;
        private static readonly System.Random _rng = new System.Random();

        // Event types worth spreading by word of mouth (skip SEASON/ECONOMY noise)
        private static readonly HashSet<string> NewsworthyTypes = new HashSet<string>
        {
            "WAR", "MAJOR", "TERRITORY", "DIPLOMACY", "COURT", "DOMINION", "PLAYER",
        };

        // ── Stubs for world_expansion_data.json (subset we care about) ──
#pragma warning disable 0649
        private class WorldStateStub
        {
            public int CurrentTurn;
            public List<WorldEventStub> Events;
        }

        private class WorldEventStub
        {
            public int    Turn;
            public string Description;
            public string Type;
        }
#pragma warning restore 0649

        private static WorldStateStub _cache;
        private static DateTime _lastLoad = DateTime.MinValue;

        /// <summary>Called from ScenarioUpdater's rumor tick (every ~3 turns).</summary>
        public static void SeedWorldNews(List<GameCharacter> npcsInPlace)
        {
            try
            {
                if (npcsInPlace == null || npcsInPlace.Count == 0) return;
                if (_rng.NextDouble() > SEED_CHANCE) return;

                RefreshCache();
                if (_cache?.Events == null || _cache.Events.Count == 0) return;

                var fresh = _cache.Events
                    .Where(e => e != null
                                && !string.IsNullOrEmpty(e.Description)
                                && NewsworthyTypes.Contains(e.Type)
                                && _cache.CurrentTurn - e.Turn <= EVENT_WINDOW)
                    .ToList();
                if (fresh.Count == 0) return;

                var evt = fresh[_rng.Next(fresh.Count)];
                var living = npcsInPlace
                    .Where(n => n != null && n.corpseState == GameCharacter.CorpseState.NONE)
                    .ToList();
                if (living.Count == 0) return;

                var npc = living[_rng.Next(living.Count)];
                // "News:" prefix distinguishes world-sim facts from personal gossip;
                // RumorNetwork.AddFact dedups and truncates to its own fact cap
                RumorNetwork.AddFact(npc.uuid, "News: " + evt.Description);
                Debug.Log($"[WorldNewsGossip] Seeded world news on {npc.GetPrettyName()}: {evt.Description}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldNewsGossip] Failed to seed world news: {e.Message}");
            }
        }

        private static void RefreshCache()
        {
            if ((DateTime.UtcNow - _lastLoad).TotalSeconds < CACHE_SECONDS) return;
            _lastLoad = DateTime.UtcNow;

            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) { _cache = null; return; }
            string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "world_expansion_data.json");
            if (!File.Exists(path)) { _cache = null; return; }

            try
            {
                _cache = JsonConvert.DeserializeObject<WorldStateStub>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldNewsGossip] Could not read world data: {e.Message}");
                _cache = null;
            }
        }
    }
}
