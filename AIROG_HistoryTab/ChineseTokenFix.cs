using HarmonyLib;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using UnityEngine;
using UnityEngine.Localization.Settings;

namespace AIROG_HistoryTab
{
    public static class ChineseTokenFix
    {
        public static void Patch(Harmony harmony)
        {
            try
            {
                var targetMethod = AccessTools.Method(typeof(MyHttpClient), nameof(MyHttpClient.DoHttpRequest));
                if (targetMethod != null)
                {
                    harmony.Patch(targetMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ChineseTokenFix), nameof(Prefix_DoHttpRequest))));
                    Debug.Log("[ChineseTokenFix] Successfully patched MyHttpClient.DoHttpRequest");
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[ChineseTokenFix] Failed to apply patch: " + e);
            }
        }

        public static void Prefix_DoHttpRequest(ref string contentStr)
        {
            try
            {
                if (string.IsNullOrEmpty(contentStr) || !contentStr.Contains("max_tokens")) return;

                bool isChinese = IsChineseLanguage();
                if (!isChinese) return;

                JObject json = JObject.Parse(contentStr);
                if (json["max_tokens"] != null)
                {
                    int currentMax = json["max_tokens"].Value<int>();
                    
                    // Sapphire Story limit is normally 220. For Chinese, we need more.
                    if (currentMax == 220)
                    {
                        json["max_tokens"] = 1000;
                        contentStr = json.ToString(Formatting.None);
                        Debug.Log($"[ChineseTokenFix] Increased max_tokens from 220 to 1000 for Chinese player.");
                    }
                }
            }
            catch
            {
                // Silent catch to avoid breaking the game loop
            }
        }

        private static bool IsChineseLanguage()
        {
            try
            {
                var selectedLocale = LocalizationSettings.SelectedLocale;
                if (selectedLocale != null && selectedLocale.Identifier.Code.StartsWith("zh"))
                {
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
