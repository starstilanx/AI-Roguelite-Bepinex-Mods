using System;

namespace AIROG_Multiplayer
{
    // Client-side image application: applying a received story image to the right
    // IllustratedStoryTurn, with a retry loop for the save-reload race.
    public partial class MultiplayerPlugin
    {
        /// <summary>
        /// Looks up the IllustratedStoryTurn by UUID, marks it finished, and calls
        /// UpdateMainImageWithXfade. Returns true if the entity was found and applied.
        /// Safe to call from main thread only.
        /// </summary>
        private static bool TryApplyClientImage(string uuid)
        {
            try
            {
                var manager = SS.I?.hackyManager;
                if (manager == null) return false;

                // Primary: entity map lookup (populated after save reload)
                GameEntity entity = null;
                SS.I.uuidToGameEntityMap?.TryGetValue(uuid, out entity);
                var illu = entity as IllustratedStoryTurn;

                // Secondary: scan lastIlluStoryTurns (covers the entity-map deserialization lag)
                if (illu == null)
                {
                    var pc = manager.playerCharacter?.pcGameEntity;
                    illu = pc?.lastIlluStoryTurns?.FindLast(il => il != null && il.uuid == uuid);
                }

                if (illu == null) return false;

                illu.imgGenInfo.imgGenState = GameEntity.ImgGenState.FINISHED;
                illu.imgGenInfo.imageDirtyBit = true;
                _ = manager.mainImg.UpdateMainImageWithXfade(illu);
                Instance?.Log.LogInfo($"[Client] Applied story image: {uuid}");
                UnityEngine.Debug.Log($"[MP-DIAG] TryApplyClientImage success: {uuid}");
                return true;
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Client] TryApplyClientImage error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Retries TryApplyClientImage every second for up to 10 seconds.
        /// Used when the StoryImage packet arrives before the save reload completes
        /// (entity map not yet populated).
        /// </summary>
        private static System.Collections.IEnumerator RetryApplyClientImage(string uuid)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                yield return new UnityEngine.WaitForSeconds(1f);

                if (!IsClientMode) yield break; // Disconnected

                if (TryApplyClientImage(uuid))
                    yield break;

                UnityEngine.Debug.Log($"[MP-DIAG] RetryApplyClientImage attempt {attempt + 1}/10 for {uuid}");
            }

            // Final fallback: display whatever the last finished turn is
            Instance?.Log.LogWarning($"[Client] UUID {uuid} not found after 10 retries — using last finished turn.");
            try
            {
                var manager = SS.I?.hackyManager;
                var lastFinished = manager?.playerCharacter?.pcGameEntity?.GetLastFinishedIlluStoryTurn();
                if (lastFinished != null)
                {
                    lastFinished.imgGenInfo.imageDirtyBit = true;
                    _ = manager.mainImg.UpdateMainImageWithXfade(lastFinished);
                }
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Client] RetryApplyClientImage fallback error: {ex.Message}");
            }
        }
    }
}
