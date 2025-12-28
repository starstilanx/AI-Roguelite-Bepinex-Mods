using System;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace AIROG_StableHordeDetector
{
    [BepInPlugin("com.airoguelite.stablehordedetector", "Stable Horde Detector", "1.0.0")]
    public class StableHordeDetectorPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Logger.LogInfo("Stable Horde Detector Plugin is loaded!");
            Harmony.CreateAndPatchAll(typeof(StableHordeDetectorPlugin));
        }

        [HarmonyPatch(typeof(StableHordeClient), "HttpWithRetry")]
        [HarmonyPostfix]
        public static void HttpWithRetryPostfix(string url, System.Threading.Tasks.Task<string> __result)
        {
            // We only care about the status check URL which returns the generation details
            if (!string.IsNullOrEmpty(url) && url.Contains("/generate/status/") && __result != null)
            {
                // Since it's an async method, __result is the Task. We check it when it's done.
                __result.ContinueWith(t =>
                {
                    if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    {
                        string jsonResponse = t.Result;
                        try
                        {
                            JObject json = JObject.Parse(jsonResponse);
                            // The status response normally contains a "generations" array
                            // Example: { "generations": [ { "worker_name": "...", "model": "...", ... } ], ... }
                            var generations = json["generations"] as JArray;
                            if (generations != null && generations.Count > 0)
                            {
                                var gen = generations[0];
                                string model = gen["model"]?.ToString() ?? "Unknown";
                                string workerName = gen["worker_name"]?.ToString() ?? "Unknown";
                                string workerId = gen["worker_id"]?.ToString() ?? "Unknown";
                                string state = gen["state"]?.ToString() ?? "Unknown"; // e.g., finished?
                                
                                Debug.Log($"[StableHordeDetector] Status Check: {url}");
                                string logMsg = $"[StableHordeDetector] [{DateTime.Now}] Model: {model} | Worker: {workerName} ({workerId}) | State: {state}";
                                Debug.Log(logMsg);

                                try 
                                {
                                    string logPath = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "stable_horde_log.txt");
                                    System.IO.File.AppendAllText(logPath, logMsg + Environment.NewLine);
                                }
                                catch (Exception fileEx)
                                {
                                    Debug.LogWarning($"[StableHordeDetector] Failed to write to log file: {fileEx.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[StableHordeDetector] Failed to parse status response: {ex.Message}");
                        }
                    }
                });
            }
        }
    }
}
