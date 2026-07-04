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
            public List<AdvisorStub> Advisors;
            public LexStub Lex;
        }

        // Mirrors ThemeLexicon in AIROG_GrandStrategy — setting-appropriate terminology,
        // auto-detected from the world's description (null on legacy saves)
        private class LexStub
        {
            public string Key;
            public string RulerTitle;
            public string DomainNoun;
            public string CurrencyWord;
            public Dictionary<string, string> RoleTitles;
            public Dictionary<string, string> WonderNames;
        }

        private class PetitionStub
        {
            public string Text;
        }

        private class AdvisorStub
        {
            public string Role;
            public string Name;
            public string Personality;
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

        // Setting-appropriate terms with safe fallbacks (legacy saves have no lexicon yet)
        private string Ruler    => _cache?.Lex?.RulerTitle   ?? "leader";
        private string Domain   => _cache?.Lex?.DomainNoun   ?? "domain";
        private string Currency => _cache?.Lex?.CurrencyWord ?? "coin";

        private string RoleTitle(string role)
        {
            string t = null;
            if (_cache?.Lex?.RoleTitles != null) _cache.Lex.RoleTitles.TryGetValue(role ?? "", out t);
            return string.IsNullOrEmpty(t) ? role : t;
        }

        public string GetContext(string prompt, int maxTokens)
        {
            RefreshCacheIfNeeded();
            if (_cache == null || !_cache.Founded || string.IsNullOrEmpty(_cache.DominionName)) return "";

            var sb = new System.Text.StringBuilder();
            sb.Append($"[DOMINION — {_cache.DominionName}]\n");
            sb.Append($"The player is the {Ruler} of {_cache.DominionName}, a territorial power in this world (capital: {_cache.CapitalName}). ");
            sb.Append($"Treasury: {_cache.Treasury} {Currency} | Army strength: {_cache.ArmyStrength}");

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
                sb.Append($"\nSubordinate powers sworn to the dominion: {string.Join(", ", _cache.VassalNames.Values)}");

            if (_cache.Advisors != null && _cache.Advisors.Count > 0)
                sb.Append($"\nAdvisors: {string.Join(", ", _cache.Advisors.Select(a => $"{a.Name} ({RoleTitle(a.Role)})"))}");

            if (_cache.Deeds != null && _cache.Deeds.Count > 0)
            {
                var recent = _cache.Deeds.Skip(Math.Max(0, _cache.Deeds.Count - 3)).Select(d => d.Description);
                sb.Append($"\nRecent deeds: {string.Join(" ", recent)}");
            }

            sb.Append("\n[DOMINION GUIDANCE]");
            sb.Append($"\n• Express all dominion matters in this world's own idiom, technology level, and tone — titles, wealth, forces, and ceremonies must fit the setting. Never default to medieval royal language (kings, thrones, crowns) unless this world is actually medieval.");
            sb.Append($"\n• The player leads {_cache.DominionName}. NPCs within its holdings know them as its {Ruler} and react accordingly (respect, requests for help, resentment where unrest is high).");
            sb.Append($"\n• Rival factions treat the player as the {Ruler} of a real power, not a mere wanderer — envoys, threats, and courtesies befit their standing.");
            if (_cache.Holdings != null && _cache.Holdings.Values.Any(h => h.Unrest >= 50))
                sb.Append("\n• Discontent simmers in the restless holdings — let murmurs of dissent color scenes there.");
            if (_cache.TaxPolicy == "HIGH")
                sb.Append($"\n• {_cache.DominionName}'s taxes are punishing — ordinary people grumble under the levies, and its collectors are unwelcome figures.");
            else if (_cache.TaxPolicy == "LOW")
                sb.Append($"\n• {_cache.DominionName} taxes lightly — its people speak of a generous {Ruler}.");
            if (!string.IsNullOrEmpty(_cache.WonderInProgress))
                sb.Append($"\n• The {WonderName(_cache.WonderInProgress)} is under construction in the capital — its worksite and work crews dominate the area.");
            if (_cache.PendingPetition != null && !string.IsNullOrEmpty(_cache.PendingPetition.Text))
                sb.Append($"\n• A matter awaits the player's judgment: \"{_cache.PendingPetition.Text}\" — petitioners may press the issue.");
            if (_cache.Advisors != null && _cache.Advisors.Count > 0)
                sb.Append($"\n• The player's advisors may appear and speak in character: {string.Join(" ", _cache.Advisors.Select(a => $"{a.Name} the {RoleTitle(a.Role)} — {a.Personality}"))}");
            if (!string.IsNullOrEmpty(_cache.ActiveVictory))
                sb.Append($"\n• {_cache.DominionName} has achieved a legendary triumph ({_cache.ActiveVictory}) — its renown colors every interaction.");

            return sb.ToString();
        }

        private string WonderName(string key)
        {
            string n = null;
            if (_cache?.Lex?.WonderNames != null) _cache.Lex.WonderNames.TryGetValue(key ?? "", out n);
            if (!string.IsNullOrEmpty(n)) return n;
            switch (key)
            {
                case "CITADEL": return "Great Bastion";
                case "MINT":    return "Grand Exchange";
                case "TEMPLE":  return "Great Sanctum";
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
