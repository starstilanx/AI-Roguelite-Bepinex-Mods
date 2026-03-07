using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.ContextProviders
{
    public class SkillWebProvider : IContextProvider
    {
        public int Priority => 85; // High priority, character defining traits
        public string Name => "Skill Web Traits";
        public string Description => "Injects narrative traits unlocked by the player in the Skill Web.";

#pragma warning disable 0649
        private class SkillWebDataStub
        {
            public List<SkillNodeStub> nodes;
            public HashSet<string> activeAffixes;
        }

        private class SkillNodeStub
        {
            public string name;
            public bool isUnlocked;
            public int tier;
            public List<string> narrativeTraits;
        }

        private SkillWebDataStub _cache;
        private float _lastLoadTime;
        private const float CACHE_REFRESH_RATE = 5f;

        public string GetContext(string prompt, int maxTokens)
        {
            RefreshCacheIfNeeded();
            if (_cache == null || _cache.nodes == null) return "";

            string traits = "";
            bool hasTraits = false;
            foreach (var node in _cache.nodes)
            {
                if (node.isUnlocked && node.narrativeTraits != null)
                {
                    foreach (var trait in node.narrativeTraits)
                    {
                        if (_cache.activeAffixes != null && _cache.activeAffixes.Contains(trait))
                        {
                            traits += $"  - {trait} (from {node.name}, Tier {node.tier})\n";
                            hasTraits = true;
                        }
                    }
                }
            }

            if (!hasTraits) return "";

            return $"\n\n[PLAYER UNLOCKED TRAITS]\n{traits}";
        }

        private void RefreshCacheIfNeeded()
        {
            if (Time.time - _lastLoadTime > CACHE_REFRESH_RATE || _cache == null)
            {
                LoadData();
                _lastLoadTime = Time.time;
            }
        }

        private void LoadData()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
            string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "SkillWeb.json");
            if (!File.Exists(path)) return;

            try
            {
                _cache = JsonConvert.DeserializeObject<SkillWebDataStub>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load SkillWeb data: {ex.Message}");
            }
        }
    }
}
