using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.ContextProviders
{
    public class GrandStrategyProvider : IContextProvider
    {
        public int Priority    => 75;
        public string Name     => "Grand Strategy";
        public string Description => "Injects the player's dominion (holdings, army, wars, deeds) into the AI prompt.";

        // ── Stub classes (kept in sync with GrandStrategyData.cs in AIROG_GrandStrategy) ──
#pragma warning disable 0649
        private class DominionStateStub
        {
            public bool   Founded;
            public string DominionName;
            public string CapitalPlaceUuid;
            public string CapitalName;
            public int    Treasury;
            public int    ArmyStrength;
            public int    CommandPoints;
            public int    MaxCommandPoints;
            public Dictionary<string, HoldingStub> Holdings;
            public List<string> CasusBelli;         // HashSet serializes as array
            public List<string> VassalFactionUuids; // HashSet serializes as array
            public Dictionary<string, string> VassalNames;
            public string TaxPolicy;
            public List<string> Wonders;
            public string WonderInProgress;
            public PetitionStub PendingPetition;
            public List<DeedStub> Deeds;
            public string ActiveVictory;
        }

        private class PetitionStub
        {
            public string Text;
        }

        private class HoldingStub
        {
            public string Name;
            public List<string> Improvements;
            public int  Unrest;
            public bool IsCapital;
        }

        private class DeedStub
        {
            public int    Turn;
            public string Description;
        }
#pragma warning restore 0649

        private DominionStateStub _cache;
        private float _lastLoadTime;
        private const float CACHE_REFRESH_RATE = 8f;

        public string GetContext(string prompt, int maxTokens)
        {
            RefreshCacheIfNeeded();
            if (_cache == null || !_cache.Founded || string.IsNullOrEmpty(_cache.DominionName)) return "";

            var sb = new System.Text.StringBuilder();
            sb.Append($"[DOMINION — {_cache.DominionName}]\n");
            sb.Append($"The player is the sovereign ruler of {_cache.DominionName} (capital: {_cache.CapitalName}). ");
            sb.Append($"Treasury: {_cache.Treasury} gold | Army strength: {_cache.ArmyStrength}");

            if (_cache.Holdings != null && _cache.Holdings.Count > 0)
            {
                var parts = _cache.Holdings.Values.Take(8).Select(h =>
                    h.Name
                    + (h.IsCapital ? " (capital)" : "")
                    + (h.Improvements != null && h.Improvements.Count > 0
                        ? $" [{string.Join(", ", h.Improvements.Select(i => i.ToLower()))}]" : "")
                    + (h.Unrest >= 50 ? " (unrest brewing!)" : ""));
                sb.Append($"\nHoldings: {string.Join("; ", parts)}");
            }

            if (_cache.Wonders != null && _cache.Wonders.Count > 0)
                sb.Append($"\nGreat works of the capital: {string.Join(", ", _cache.Wonders.Select(WonderName))}");

            if (_cache.VassalNames != null && _cache.VassalNames.Count > 0)
                sb.Append($"\nVassal realms sworn to the dominion: {string.Join(", ", _cache.VassalNames.Values)}");

            if (_cache.Deeds != null && _cache.Deeds.Count > 0)
            {
                var recent = _cache.Deeds.Skip(Math.Max(0, _cache.Deeds.Count - 3)).Select(d => d.Description);
                sb.Append($"\nRecent deeds: {string.Join(" ", recent)}");
            }

            sb.Append("\n[DOMINION GUIDANCE]");
            sb.Append($"\n• The player rules {_cache.DominionName}. NPCs within its holdings know them as their liege and react accordingly (deference, petitions, resentment where unrest is high).");
            sb.Append("\n• Rival factions treat the player as a sovereign power, not a mere wanderer — envoys, threats, and courtesies befit a ruler.");
            if (_cache.Holdings != null && _cache.Holdings.Values.Any(h => h.Unrest >= 50))
                sb.Append("\n• Discontent simmers in the restless holdings — let murmurs of dissent color scenes there.");
            if (_cache.TaxPolicy == "HIGH")
                sb.Append("\n• The crown's taxes are punishing — commoners grumble about the levies, and tax collectors are unwelcome figures.");
            else if (_cache.TaxPolicy == "LOW")
                sb.Append("\n• The crown taxes lightly — the smallfolk speak of a generous sovereign.");
            if (!string.IsNullOrEmpty(_cache.WonderInProgress))
                sb.Append($"\n• The {WonderName(_cache.WonderInProgress)} is under construction in the capital — scaffolds, artisans, and hauled stone are everywhere there.");
            if (_cache.PendingPetition != null && !string.IsNullOrEmpty(_cache.PendingPetition.Text))
                sb.Append($"\n• A petition awaits the sovereign's judgment: \"{_cache.PendingPetition.Text}\" — courtiers and petitioners may press the matter.");
            if (!string.IsNullOrEmpty(_cache.ActiveVictory))
                sb.Append($"\n• {_cache.DominionName} has achieved a legendary triumph ({_cache.ActiveVictory}) — its renown colors every interaction.");

            return sb.ToString();
        }

        private static string WonderName(string key)
        {
            switch (key)
            {
                case "CITADEL": return "Grand Citadel";
                case "MINT":    return "Royal Mint";
                case "TEMPLE":  return "High Temple";
                default:        return key;
            }
        }

        private void RefreshCacheIfNeeded()
        {
            if (_cache != null && Time.realtimeSinceStartup - _lastLoadTime < CACHE_REFRESH_RATE) return;
            _lastLoadTime = Time.realtimeSinceStartup;
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) { _cache = null; return; }
                string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "grand_strategy_data.json");
                if (!File.Exists(path)) { _cache = null; return; }
                _cache = JsonConvert.DeserializeObject<DominionStateStub>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GenContext] GrandStrategyProvider failed to read dominion data: {e.Message}");
                _cache = null;
            }
        }
    }
}
