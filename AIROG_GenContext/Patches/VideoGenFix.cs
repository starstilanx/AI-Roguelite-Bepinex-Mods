using HarmonyLib;
using UnityEngine;
using System.IO;
using System.Threading.Tasks;

namespace AIROG_GenContext.Patches
{
    public static class VideoGenFix
    {
        public static void Patch(Harmony harmony, bool ignoredParam = true)
        {
            // We ignore the initial param for the patch itself, and check dynamically in the prefix
            // so the UI toggle works immediately.
            var targetMethod = AccessTools.Method(typeof(VideoMaker), "CreateVideoFromSingleImg");
            if (targetMethod != null)
            {
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(VideoGenFix), nameof(Prefix_CreateVideoFromSingleImg))));
                Debug.Log("[GenContext] Patched VideoMaker.CreateVideoFromSingleImg (Dynamic Config).");
            }
            else
            {
                Debug.LogError("[GenContext] Failed to find VideoMaker.CreateVideoFromSingleImg method.");
            }
        }

        public static bool Prefix_CreateVideoFromSingleImg(string imgPath)
        {
            // Check config dynamically
            if (GenContextPlugin.EnableVideoGenFix != null && !GenContextPlugin.EnableVideoGenFix.Value)
            {
                return true; // Skip fix if disabled
            }
            if (!File.Exists(imgPath))
            {
                Debug.LogWarning($"[VideoGenFix] Image file not found: {imgPath}. Waiting for it to appear...");
                
                // Retry loop
                for (int i = 0; i < 20; i++) // Try for 2 seconds (20 * 100ms)
                {
                    System.Threading.Thread.Sleep(100);
                    if (File.Exists(imgPath))
                    {
                        Debug.Log($"[VideoGenFix] File appeared after {i * 100}ms!");
                        return true;
                    }
                }
                
                Debug.LogError($"[VideoGenFix] File still missing after waiting: {imgPath}. Allowing original method to run (and likely crash).");
            }
            return true;
        }
    }
}
