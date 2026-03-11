using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AIROG_WomboStyles
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class WomboStylesPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID    = "com.airog.wombostyles";
        public const string PLUGIN_NAME    = "Wombo Styles Fetcher";
        public const string PLUGIN_VERSION = "1.0.0";

        private const string FIREBASE_KEY = "AIzaSyDCvp5MTJLUdtBYEKYWXJrlLzu1zuKM6Xw";
        private const string STYLES_URL   = "https://paint.api.wombo.ai/api/v2/styles";

        public static WomboStylesPlugin Instance { get; private set; }

        private static readonly Dictionary<int, string> _fetchedStyles = new Dictionary<int, string>();
        private static volatile bool _fetchComplete  = false;
        private static volatile bool _fetchStarted   = false;

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[WomboStyles] Loading v{PLUGIN_VERSION}");

            var harmony = new Harmony(PLUGIN_GUID);
            var optionsMethod = AccessTools.Method(typeof(MainMenu), "Options");
            if (optionsMethod != null)
            {
                harmony.Patch(optionsMethod,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(WomboStylesPlugin), nameof(Postfix_Options))));
                Logger.LogInfo("[WomboStyles] Patched MainMenu.Options");
            }
            else
            {
                Logger.LogError("[WomboStyles] Could not find MainMenu.Options!");
            }
        }

        // ─── Fetch via curl (same as WomboClient) ────────────────────────────────

        // Candidates tried in order until one returns parseable style data
        private static readonly string[] STYLE_ENDPOINTS = new[]
        {
            "https://app.wombo.art/api/styles",           // v1 — redirects, needs -L
            "https://dream.ai/api/v2/styles",             // post-rebrand v2
            "https://dream.ai/api/styles",                // post-rebrand v1
            "https://paint.api.wombo.ai/api/v2/styles",  // original v2 (404 but worth retrying)
        };

        private static async Task FetchStylesAsync()
        {
            try
            {
                Instance.Logger.LogInfo("[WomboStyles] Fetching styles via curl…");
                string token = await GetAnonymousTokenCurl();

                foreach (string endpoint in STYLE_ENDPOINTS)
                {
                    Instance.Logger.LogInfo($"[WomboStyles] Trying: {endpoint}");
                    string json = await CurlGetFollowRedirects(endpoint, token);
                    string preview = json?.Length > 300 ? json.Substring(0, 300) + "…" : json;
                    Instance.Logger.LogInfo($"[WomboStyles] Response ({json?.Length ?? 0} chars): {preview}");

                    if (!string.IsNullOrEmpty(json) && ParseStylesResponse(json) > 0)
                    {
                        Instance.Logger.LogInfo($"[WomboStyles] Success with {endpoint}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WomboStyles] Fetch failed: {ex.Message}");
            }
            finally
            {
                _fetchComplete = true;
                Instance.Logger.LogInfo($"[WomboStyles] Fetch complete — {_fetchedStyles.Count} styles from API.");
            }
        }

        // Uses curl with -L to follow HTTP redirects (Utils.ExecuteCurlCommandAsync doesn't include -L)
        private static async Task<string> CurlGetFollowRedirects(string url, string token)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("--silent --show-error --ca-native -L ");
                sb.Append($"-X GET \"{url}\" ");
                sb.Append("-H \"User-Agent: Mozilla/5.0 (Linux; Android 6.0; Nexus 5 Build/MRA58N) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Mobile Safari/537.36\" ");
                sb.Append("-H \"X-App-Version: WEB-7.0.0\" ");
                sb.Append("-H \"Accept: application/json\" ");
                if (token != null)
                    sb.Append($"-H \"Authorization: Bearer {token}\" ");

                return await Utils.ExecuteCommandAsync(SS.I.curlExePath, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WomboStyles] CurlGet failed for {url}: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> GetAnonymousTokenCurl()
        {
            try
            {
                string url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FIREBASE_KEY}";
                var headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" }
                };
                string response = await Utils.ExecuteCurlCommandAsync(url, "POST", headers, "{}");
                return (string)JObject.Parse(response)["idToken"];
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WomboStyles] Could not get anonymous token: {ex.Message}");
                return null;
            }
        }

        // Returns number of styles parsed (0 = nothing useful found)
        private static int ParseStylesResponse(string json)
        {
            int before = _fetchedStyles.Count;
            try
            {
                JToken root = JToken.Parse(json);
                JArray arr = null;

                if (root is JArray)
                    arr = (JArray)root;
                else if (root is JObject obj)
                    arr = (JArray)(obj["data"] ?? obj["styles"] ?? obj["result"]);

                if (arr == null)
                {
                    Debug.LogWarning("[WomboStyles] Could not locate style array in API response.");
                    return 0;
                }

                foreach (JToken item in arr)
                {
                    int id = item["id"]?.ToObject<int>() ?? -1;
                    string name = item["name"]?.ToString();
                    bool visible = item["is_visible"]?.ToObject<bool>() ?? true;

                    if (id > 0 && !string.IsNullOrWhiteSpace(name) && visible)
                        _fetchedStyles[id] = name;
                }

                Debug.Log($"[WomboStyles] Parsed {_fetchedStyles.Count} visible styles.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WomboStyles] Failed to parse styles JSON: {ex.Message}");
            }
            return _fetchedStyles.Count - before;
        }

        // ─── Harmony Patch ────────────────────────────────────────────────────────

        public static void Postfix_Options(MainMenu __instance)
        {
            // Kick off the fetch the first time Options is opened (SS.I is guaranteed ready by then)
            if (!_fetchStarted)
            {
                _fetchStarted = true;
                _ = FetchStylesAsync();
                return;   // styles won't be ready on this first open; next open will inject them
            }

            if (!_fetchComplete || _fetchedStyles.Count == 0)
                return;

            try
            {
                var dictField   = AccessTools.Field(typeof(MainMenu), "dropdownNumToWomboStyleStrDict");
                var currentDict = (Dictionary<int, string>)dictField.GetValue(null);

                int addedCount = 0;
                foreach (var kv in _fetchedStyles)
                {
                    if (!currentDict.ContainsKey(kv.Key))
                    {
                        currentDict[kv.Key] = kv.Value + " *";
                        addedCount++;
                    }
                }

                if (addedCount == 0)
                    return;

                // Rebuild dropdown
                var dropdown = __instance.womboStyleDropdown;
                dropdown.ClearOptions();
                dropdown.AddOptions(currentDict.Values.ToList());

                // Restore saved selection
                var indexMap = new Dictionary<int, int>();
                int idx = 0;
                foreach (var kv in currentDict)
                    indexMap[kv.Key] = idx++;

                int savedStyle = PlayerPrefs.GetInt("PREF_KEY_WOMBO_STYLE4", 130);
                if (indexMap.ContainsKey(savedStyle))
                    dropdown.SetValueWithoutNotify(indexMap[savedStyle]);
                else if (indexMap.ContainsKey(130))
                    dropdown.SetValueWithoutNotify(indexMap[130]);

                Instance.Logger.LogInfo(
                    $"[WomboStyles] Injected {addedCount} new style(s). Dropdown total: {currentDict.Count}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WomboStyles] Error injecting styles: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
