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
        // How far a single provider may overrun what is left of the budget before its block is dropped.
        private const int OVERRUN_GRACE_TOKENS = 768;

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
            _providers.Add(new ContextProviders.GrandStrategyProvider());
            _providers.Add(new ContextProviders.SettlementProvider());
            // (No Skill Web provider here: the old built-in "Skill Web Traits" one read a
            // SkillWeb.json schema that no longer exists and has been removed. AIROG_SkillWeb
            // registers its own "Skill Web Constellation" provider.)
            _providers.Add(new ContextProviders.InsightProvider());
            
            // Sort by priority (Higher first)
            _providers.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            LoadConfig();

            // The provider map is now the GLOBAL DEFAULT layer of the per-save mod switches.
            ModToggles.BindDefaults(_enabledProviders);

            // Default defaults
            if (!_globalSettings.ContainsKey("NemesisSystem")) _globalSettings["NemesisSystem"] = true;
            if (!_globalSettings.ContainsKey("NemesisLooting")) _globalSettings["NemesisLooting"] = true;
            if (!_globalSettings.ContainsKey("DisableTruncation")) _globalSettings["DisableTruncation"] = false;
            if (!_globalSettings.ContainsKey("DMNotes")) _globalSettings["DMNotes"] = true;
            if (!_globalSettings.ContainsKey("RRCompat")) _globalSettings["RRCompat"] = false;
        }

        public static List<IContextProvider> GetProviders() => _providers;

        /// <summary>Called by external mods to register their own context providers.</summary>
        public static void RegisterProvider(IContextProvider provider)
        {
            for (int i = 0; i < _providers.Count; i++)
                if (_providers[i].Name == provider.Name) return; // prevent double-registration
            _providers.Add(provider);
            _providers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            Debug.Log($"[GenContext] External provider registered: {provider.Name} (priority {provider.Priority})");
        }

        /// <summary>
        /// Whether a provider - and the mod behind it - is switched on for the current save.
        /// Same answer as <see cref="ModToggles.IsEnabled"/>; kept for existing callers.
        /// </summary>
        public static bool IsProviderEnabled(string name) => ModToggles.IsEnabled(name);

        /// <summary>Switches a provider's mod on/off for the current save (see <see cref="ModToggles"/>).</summary>
        public static void SetProviderEnabled(string name, bool enabled) => ModToggles.SetEnabled(name, enabled);

        internal static void SaveConfigInternal() => SaveConfig();

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
                        // An old-format file (a bare name->bool map) also deserializes into a
                        // non-null wrapper, just with both members null. Returning on that used to
                        // skip the legacy fallback below and discard the player's old toggles.
                        if (config != null && (config.Providers != null || config.GlobalSettings != null))
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
        public static void Postfix_BuildPromptString(GameplayManager __instance, ref string __result, GameCharacter currentChar)
        {
            if (string.IsNullOrEmpty(__result)) return;

            // This runs inside the game's prompt builder. Whatever goes wrong in here may cost the
            // injected context, never the prompt.
            try
            {
                int currentTokens;
                string injection = BuildInjection(__result, out currentTokens);
                if (injection.Length > 0)
                {
                    __result += "\n\n[CONTEXT INJECTION]\n" + injection;
                    Debug.Log($"[GenContext] Injected {currentTokens} estimated tokens of context.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] Context injection failed; the prompt goes out without it: {ex}");
            }
        }

        private static string BuildInjection(string prompt, out int currentTokens)
        {
            StringBuilder sb = new StringBuilder();
            currentTokens = 0;

            bool disableTruncation = GetGlobalSetting("DisableTruncation");

            foreach (var provider in _providers)
            {
                if (!disableTruncation && currentTokens >= MAX_TOTAL_TOKENS) break;

                // One provider per try: a mod whose provider throws loses its own block for this
                // prompt, and every other mod's context still goes in.
                string name = "(unnamed)";
                try
                {
                    name = provider.Name;

                    // Check if enabled
                    if (!IsProviderEnabled(name)) continue;

                    // Use int.MaxValue / 4 when truncation disabled (avoids overflow when providers multiply by 4)
                    // Clamp to Math.Max(1, ...) so providers never receive 0 or negative token budgets
                    int remainingTokens = disableTruncation
                        ? int.MaxValue / 4
                        : Math.Max(1, MAX_TOTAL_TOKENS - currentTokens);
                    string ctx = provider.GetContext(prompt, remainingTokens);
                    if (string.IsNullOrEmpty(ctx)) continue;

                    if (!disableTruncation && !FitsBudget(ctx, remainingTokens, name)) continue;

                    // Providers do not all end their block with a line break; without one the next
                    // provider's header ran on from the last word of this one.
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    sb.Append(ctx);
                    currentTokens += ctx.Length / CHAR_LIMIT_PER_TOKEN;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GenContext] Context provider '{name}' failed and was left out of this prompt: {ex}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Holds a provider to the budget it was given. Providers are told how much is left, but
        /// nothing checked what they returned, so one that ignored it could take the cap and any
        /// amount beyond it.
        ///
        /// The block is never cut here: the manager cannot tell prose from a hidden-tag
        /// instruction, and half an instruction is worse than none. A provider may therefore run
        /// over by up to <see cref="OVERRUN_GRACE_TOKENS"/> (Reverie's dream directive does so on
        /// purpose rather than be truncated); a block bigger than that is left out of this prompt.
        /// The injection as a whole can thus never exceed MAX_TOTAL_TOKENS + OVERRUN_GRACE_TOKENS.
        /// </summary>
        private static bool FitsBudget(string ctx, int remainingTokens, string providerName)
        {
            int allowedChars = (remainingTokens + OVERRUN_GRACE_TOKENS) * CHAR_LIMIT_PER_TOKEN;
            if (ctx.Length <= allowedChars) return true;

            Debug.LogWarning($"[GenContext] Context provider '{providerName}' returned {ctx.Length} characters with " +
                             $"{remainingTokens * CHAR_LIMIT_PER_TOKEN} left in the budget; left out of this prompt.");
            return false;
        }
    }
}
