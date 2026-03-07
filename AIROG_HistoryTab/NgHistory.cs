using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_HistoryTab
{
    public class NgHistory : MonoBehaviour
    {
        public TMP_InputField historyInput;
        public TMP_InputField univNameTextInput;
        public TMP_InputField univDescTextInput;
        public TMP_InputField worldNameTextInput;
        public TMP_InputField worldBkgdTextInput;

        public NgFactions ngFactions;
        public NgRegions ngRegions;
        public NgCurrency ngCurrency;

        public void PopulateHistory(string history)
        {
            Debug.Log($"[HistoryTab] PopulateHistory called. Input field null: {historyInput == null}, history length: {history?.Length ?? 0}");
            if (historyInput != null)
            {
                historyInput.text = history;
                Debug.Log($"[HistoryTab] historyInput.text is now: {historyInput.text.Length} characters.");
                
                // Ensure we update the static holder whenever text changes
                // This covers the case where the user edits it manually
                historyInput.onValueChanged.RemoveAllListeners();
                historyInput.onValueChanged.AddListener((val) => {
                    HistoryData.LastGeneratedHistory = val;
                    HistoryUI.SaveTempHistory(val);
                });
                
                // Also set it immediately
                HistoryData.LastGeneratedHistory = history;
            }
        }

        public void OnClearHistory()
        {
            if (historyInput != null)
            {
                historyInput.text = "";
            }
        }

        public void OnHistoryHelpClicked()
        {
            MainMenu.soundManagerForMenuSingleton.smallClickSoundFxObj.PlayNextSound();
            MainMenu.MessageModal().ShowModal("World History provides context to the AI about the past events of your universe. It will be injected into prompts to help the AI maintain consistency with your world's lore.", true, true, 500);
        }

        public async void OnRegenHistory()
        {
            await System.Threading.Tasks.Task.Yield(); // Squash CS1998
            Debug.Log("[HistoryTab] OnRegenHistory clicked.");

            if (worldBkgdTextInput == null || string.IsNullOrEmpty(worldBkgdTextInput.text))
            {
                Debug.Log("[HistoryTab] OnRegenHistory aborted: worldBkgdTextInput is null or empty.");
                MainMenu.MessageModal().ShowModal(LS.I.GetLocStr("world-background-empty"));
                return;
            }

            string promptConfirm = (historyInput != null && historyInput.text.Length > 0) ? "Regenerate History?" : "Generate History?";
            Debug.Log($"[HistoryTab] Showing confirmation: {promptConfirm}");
            
            MainMenu.ConfirmationModal().ShowTextPromptModal(promptConfirm + "\nOptional: provide further instructions to AI", true, true, async delegate
            {
                Debug.Log("[HistoryTab] Confirmation confirmed. Starting generation...");
                string history = null;
                string hint = MainMenu.ConfirmationModal()?.textInput1?.text;
                
                // Build Extra Context
                string extraContext = BuildExtraContext();

                await Utils.DoTaskWLoadScrn(async delegate
                {
                    try {
                        history = await HistoryGenerator.GenerateHistory(
                            SS.I.ngLangCode, 
                            SS.I.ngCustomLang, 
                            univNameTextInput.text, 
                            univDescTextInput.text, 
                            worldNameTextInput.text, 
                            worldBkgdTextInput.text, 
                            hint,
                            extraContext); // Pass extra context
                    } catch (Exception e) {
                        Debug.LogError($"[HistoryTab] Error in GenerateHistory: {e}");
                        history = "Error: " + e.Message;
                    }
                });

                Debug.Log($"[HistoryTab] Generation finished. Result length: {(history?.Length ?? 0)}");
                if (!string.IsNullOrEmpty(history))
                {
                    PopulateHistory(history);
                    HistoryUI.SaveTempHistory(history);
                }
            });
        }

        private string BuildExtraContext()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            // Factions
            if (ngFactions != null)
            {
                var factions = ngFactions.GetNonNullablePpFactions();
                if (factions != null && factions.Count > 0)
                {
                    sb.AppendLine("\n[FACTIONS]");
                    foreach (var f in factions)
                    {
                        sb.AppendLine($"- {f.n}: {f.d}");
                    }
                }
            }

            // Regions
            if (ngRegions != null)
            {
                var locInfo = ngRegions.GetPpInitLocInfo();
                if (locInfo != null && locInfo.regions != null && locInfo.regions.Count > 0)
                {
                    sb.AppendLine("\n[REGIONS/LOCATIONS]");
                    foreach (var r in locInfo.regions)
                    {
                        sb.AppendLine($"- {r.n}: {r.d}");
                    }
                    if (locInfo.startingLocationHierarchy != null && locInfo.startingLocationHierarchy.Count > 0)
                    {
                         sb.AppendLine("Starting Location Hierarchy: " + string.Join(" > ", locInfo.startingLocationHierarchy));
                    }
                }
            }

            // Currency
            if (ngCurrency != null)
            {
                var curr = ngCurrency.ToCurrencyInfo();
                if (curr != null)
                {
                    sb.AppendLine($"\n[CURRENCY]\nName: {curr.nme}\nDescription: {curr.desc}");
                }
            }
            
            return sb.ToString();
        }
    }

    public static class HistoryGenerator
    {
        public static async System.Threading.Tasks.Task<string> GenerateHistory(string langCode, string customLang, string universeName, string universeDesc, string worldName, string worldBkgd, string hintStr, string extraContext = "")
        {
            const string FALLBACK_PROMPT = "As a world-building AI, create a compelling and immersive history for the universe of '${universe_name}'.\n\n" +
                "Universe Description:\n${universe_desc}\n\n" +
                "Current World Context (${world_name}):\n${world_bkgd}\n" +
                "${extra_context}\n" +
                "Instructions:\n" +
                "1. Write 3-5 paragraphs of history including major past events, the rise and fall of civilizations, or significant turning points.\n" +
                "2. Ensure the tone matches the world background.${maybe_hint_str}${maybe_i18n_str}\n\n" +
                "Output the history text directly. Do not include any meta-commentary or JSON.";

            string promptTemplate = FALLBACK_PROMPT;
            if (SS.I != null && SS.I.chatGptPromptsDict.ContainsKey("gen_history"))
            {
                promptTemplate = SS.I.chatGptPromptsDict["gen_history"];
                // Verify if user's custom prompt has extra_context, if not, append specific context to world_bkgd for compatibility
                if (!promptTemplate.Contains("${extra_context}"))
                {
                    // If the user hasn't updated their prompt to include our new variable, just append it to world_bkgd logic
                   // Actually, we can just replace world_bkgd with "world_bkgd + extraContext" if needed, 
                   // but let's try to be smarter.
                   // If the template is missing ${extra_context}, we append it to ${world_bkgd}
                   promptTemplate = promptTemplate.Replace("${world_bkgd}", "${world_bkgd}\n${extra_context}");
                }
            }

            string text = promptTemplate
                .Replace("${universe_name}", universeName)
                .Replace("${universe_desc}", universeDesc)
                .Replace("${world_name}", worldName)
                .Replace("${world_bkgd}", worldBkgd)
                .Replace("${extra_context}", extraContext)
                .Replace("${maybe_hint_str}", AIAsker.GetFullHintStr(hintStr));

            string newValue = "";
            string preferredNonEnLang = Utils.GetPreferredNonEnLang(langCode, customLang);
            if (!Utils.IsNullOrEmpty(preferredNonEnLang))
            {
                newValue = ", in " + preferredNonEnLang;
            }
            text = text.Replace("${maybe_i18n_str}", newValue);

            Debug.Log("[HistoryTab] Generating history with prompt: " + (text.Length > 100 ? text.Substring(0, 100) + "..." : text));

            string result = "Error: Failed to generate";
            try
            {
                var method = typeof(AIAsker).GetMethod("GenerateTxtNoTryStrStyle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    var parameters = method.GetParameters();
                    object[] args = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : Type.Missing;
                    }
                    
                    args[0] = AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER;
                    if (args.Length > 1) args[1] = text;
                    if (args.Length > 2) args[2] = AIAsker.ChatGptPostprocessingType.NONE;
                    if (args.Length > 3) args[3] = false; // forceOfficialChatgpt
                    if (args.Length > 4) args[4] = false; // forceNsfwFriendlyIfAvail
                    if (args.Length > 5) args[5] = null;  // extraDisallowedTokens
                    if (args.Length > 6) args[6] = false; // background
                    if (args.Length > 7) args[7] = true;  // forceEventCheckModel
                    if (args.Length > 8) args[8] = AIAsker.ModelOverrideMode.GOOD_FOR_CORRECTNESS;
                    if (args.Length > 9) args[9] = false; // isForLorebuilding

                    var task = (System.Threading.Tasks.Task<string>)method.Invoke(null, args);
                    result = await task;
                }
                else
                {
                    Debug.LogError("[HistoryTab] GenerateTxtNoTryStrStyle method not found via reflection!");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HistoryTab] Reflection call failed: {ex}");
                result = "Error: " + ex.Message;
            }

            Debug.Log("[HistoryTab] Generation result: " + (string.IsNullOrEmpty(result) ? "NULL/EMPTY" : (result.Length > 50 ? result.Substring(0, 50) + "..." : result)));
            return result;
        }
    }
}
