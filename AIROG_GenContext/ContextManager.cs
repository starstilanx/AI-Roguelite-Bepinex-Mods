using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AIROG_GenContext
{
    public static class ContextManager
    {
        // Token Budget Settings
        public const int MAX_TOTAL_TOKENS = 2048; 
        public const int CHAR_LIMIT_PER_TOKEN = 4; 

        // Instantiated Providers
        private static List<IContextProvider> _providers = new List<IContextProvider>();
        private static Dictionary<string, bool> _enabledProviders = new Dictionary<string, bool>();
        private static Dictionary<string, bool> _globalSettings = new Dictionary<string, bool>();

        public static void Init()
        {
            Debug.Log("[GenContext] specialized ContextManager initialized.");
            _providers.Clear();
            _providers.Add(new DMNotes.DmNotesProvider());
            _providers.Add(new ContextProviders.NPCProvider());
            _providers.Add(new ContextProviders.HistoryProvider());
            _providers.Add(new ContextProviders.WorldContextProvider());
            _providers.Add(new ContextProviders.SettlementProvider());
            
            // Sort by priority (Higher first)
            _providers.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            LoadConfig();

            // Default defaults
            if (!_globalSettings.ContainsKey("NemesisSystem")) _globalSettings["NemesisSystem"] = true;
            if (!_globalSettings.ContainsKey("NemesisLooting")) _globalSettings["NemesisLooting"] = true;
            if (!_globalSettings.ContainsKey("DisableTruncation")) _globalSettings["DisableTruncation"] = false;
            if (!_globalSettings.ContainsKey("DMNotes")) _globalSettings["DMNotes"] = true;
        }

        public static List<IContextProvider> GetProviders() => _providers;

        public static bool IsProviderEnabled(string name)
        {
            if (_enabledProviders.ContainsKey(name)) return _enabledProviders[name];
            return true; // Default enabled
        }

        public static void SetProviderEnabled(string name, bool enabled)
        {
            _enabledProviders[name] = enabled;
            SaveConfig();
        }

        public static bool GetGlobalSetting(string key)
        {
            if (_globalSettings.TryGetValue(key, out bool val)) return val;
            return false;
        }

        public static void SetGlobalSetting(string key, bool value)
        {
            _globalSettings[key] = value;
            SaveConfig();
        }

        private class ConfigWrapper
        {
            public Dictionary<string, bool> Providers;
            public Dictionary<string, bool> GlobalSettings;
        }

        private static void LoadConfig()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "gen_context_config.json");
                if (System.IO.File.Exists(path))
                {
                    string json = System.IO.File.ReadAllText(path);
                    // Try new format first
                    try 
                    {
                        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<ConfigWrapper>(json);
                        if (config != null)
                        {
                            if (config.Providers != null) _enabledProviders = config.Providers;
                            if (config.GlobalSettings != null) _globalSettings = config.GlobalSettings;
                            return;
                        }
                    }
                    catch {}

                    // Fallback to old format
                    var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, bool>>(json);
                    if (dict != null) _enabledProviders = dict;
                }
            }
            catch (Exception ex) { Debug.LogError($"[GenContext] Failed to load config: {ex}"); }
        }

        private static void SaveConfig()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "gen_context_config.json");
                var config = new ConfigWrapper 
                { 
                    Providers = _enabledProviders, 
                    GlobalSettings = _globalSettings 
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(path, json);
            }
            catch (Exception ex) { Debug.LogError($"[GenContext] Failed to save config: {ex}"); }
        }

        // Main Injection Logic
        [HarmonyPatch(typeof(GameplayManager), "BuildPromptString", new Type[] { typeof(string), typeof(bool), typeof(bool), typeof(InteractionInfo), typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld), typeof(List<Faction>), typeof(List<string>), typeof(bool) }, new ArgumentType[] { ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
        [HarmonyPostfix]
        public static void Postfix_BuildPromptString(GameplayManager __instance, ref string __result, GameCharacter currentChar)
        {
            if (string.IsNullOrEmpty(__result)) return;

            StringBuilder sb = new StringBuilder();
            int currentTokens = 0;

            bool disableTruncation = GetGlobalSetting("DisableTruncation");

            foreach (var provider in _providers)
            {
                // Check if enabled
                if (!IsProviderEnabled(provider.Name)) continue;

                if (!disableTruncation && currentTokens >= MAX_TOTAL_TOKENS) break;

                // Use int.MaxValue / 4 when truncation disabled (avoids overflow when providers multiply by 4)
                // Clamp to Math.Max(1, ...) so providers never receive 0 or negative token budgets
                int remainingTokens = disableTruncation
                    ? int.MaxValue / 4
                    : Math.Max(1, MAX_TOTAL_TOKENS - currentTokens);
                string ctx = provider.GetContext(__result, remainingTokens);

                
                if (!string.IsNullOrEmpty(ctx))
                {
                    sb.Append(ctx);
                    currentTokens += ctx.Length / CHAR_LIMIT_PER_TOKEN;
                }
            }

            // Appending to the prompt
            if (sb.Length > 0)
            {
                __result += "\n\n[CONTEXT INJECTION]\n" + sb.ToString();
                Debug.Log($"[GenContext] Injected {currentTokens} estimated tokens of context.");
            }
        }
    }
}
