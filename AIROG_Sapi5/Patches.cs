using HarmonyLib;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine;
using System.IO;
using System.Collections;
using UnityEngine.Networking;
using System;
using System.Reflection;

namespace AIROG_Sapi5
{
    // The game merged TiktokTtsClient into TtsClient and replaced the old
    // SpeakerType/VoiceType enum pipeline with role-string segments resolved
    // via an LLM call (TtsHelper.GenerateTtsSegments) + TtsVoiceProfile tags.
    [HarmonyPatch(typeof(TtsClient))]
    public static class TtsClientPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("GenerateAndQueueTts")]
        public static bool GenerateAndQueueTtsPrefix(TtsClient __instance, GameplayManager manager, string turnTxt, bool clearQueue, ref Task __result)
        {
            if (!Sapi5Plugin.UseSapi5.Value) return true;
            Debug.Log("[SAPI5] GenerateAndQueueTtsPrefix - SAPI5 taking over TTS");
            __result = GenerateAndQueueTtsAsync(manager, turnTxt, clearQueue);
            return false;
        }

        private static async Task GenerateAndQueueTtsAsync(GameplayManager manager, string turnTxt, bool clearQueue)
        {
            try
            {
                Debug.Log($"[SAPI5] GenerateAndQueueTtsAsync called. turnTxt length: {turnTxt?.Length ?? -1}");

                List<TtsHelper.TtsSegment> segments = await TtsHelper.GenerateTtsSegments(manager, turnTxt);
                Debug.Log($"[SAPI5] Extracted {segments.Count} tts segments.");

                if (segments.Count == 0)
                {
                    Debug.LogWarning("[SAPI5] No tts segments to synthesize — skipping.");
                    return;
                }

                List<Task<string>> tasks = new List<Task<string>>();
                foreach (var seg in segments)
                {
                    string voiceName = GetSapi5VoiceForRole(seg.role, manager);
                    Debug.Log($"[SAPI5] Speaking as {seg.role} with voice: {voiceName}");
                    tasks.Add(Sapi5Client.Instance.GenerateTts(Utils.KeepWordishChars(seg.text, true), voiceName));
                }

                string[] uuids = await Task.WhenAll(tasks);
                List<string> validUuids = uuids.Where(u => !string.IsNullOrEmpty(u)).ToList();
                Debug.Log($"[SAPI5] {validUuids.Count}/{tasks.Count} TTS tasks produced valid audio.");

                if (validUuids.Count == 0) return;

                string finalUuid = await Sapi5Client.Instance.ConcatenateAudioFiles(validUuids);

                if (string.IsNullOrEmpty(finalUuid)) return;

                lock (manager.gameSpeechManager.currentSoundUuidQueue)
                {
                    if (clearQueue)
                    {
                        manager.gameSpeechManager.currentSoundUuidQueue.Clear();
                    }

                    manager.gameSpeechManager.currentSoundUuidQueue.Enqueue(finalUuid);

                    if (clearQueue)
                    {
                        manager.gameSpeechManager.soundQueueDirtyBit = true;
                    }
                }

                Debug.Log($"[SAPI5] Successfully queued audio UUID: {finalUuid}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SAPI5] GenerateAndQueueTtsAsync FAILED: {ex}");
            }
        }

        private static string GetSapi5VoiceForRole(string role, GameplayManager manager)
        {
            if (role == "NARRATOR") return Sapi5Plugin.VoiceNarration.Value;

            if (role == "PLAYER")
            {
                return (manager.playerCharacter.GetGender() == SS.Gender.MALE)
                    ? Sapi5Plugin.VoiceMale.Value
                    : Sapi5Plugin.VoiceFemale.Value;
            }

            TtsVoiceProfile profile = TtsHelper.FindCharacterByName(role, manager)?.ttsVoiceProfile;
            List<string> tags = profile?.tags;
            if (tags != null)
            {
                if (tags.Any(t => t.IndexOf("monster", StringComparison.OrdinalIgnoreCase) >= 0)) return Sapi5Plugin.VoiceMonster.Value;
                if (tags.Any(t => t.IndexOf("robot", StringComparison.OrdinalIgnoreCase) >= 0)) return Sapi5Plugin.VoiceRobot.Value;
                if (tags.Any(t => t.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0)) return Sapi5Plugin.VoiceEnemy.Value;
            }

            if (string.Equals(profile?.gender, "Male", StringComparison.OrdinalIgnoreCase)) return Sapi5Plugin.VoiceMale.Value;
            if (string.Equals(profile?.gender, "Female", StringComparison.OrdinalIgnoreCase)) return Sapi5Plugin.VoiceFemale.Value;

            return Sapi5Plugin.VoiceNarration.Value;
        }
    }

    [HarmonyPatch(typeof(GameSpeechManager))]
    public static class GameSpeechManagerPatches
    {
        private static AccessTools.FieldRef<GameSpeechManager, bool> isUnpackingAudioRef = AccessTools.FieldRefAccess<GameSpeechManager, bool>("isUnpackingAudio");
        private static AccessTools.FieldRef<GameSpeechManager, bool> shouldPauseRef = AccessTools.FieldRefAccess<GameSpeechManager, bool>("shouldPause");
        private static AccessTools.FieldRef<GameSpeechManager, bool> isPausedRef = AccessTools.FieldRefAccess<GameSpeechManager, bool>("isPaused");
        private const float PAUSE_FADE_IN_OUT_TIME = 0.3f;

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        public static bool UpdatePrefix(GameSpeechManager __instance)
        {
            // Only take over if explicitly using SAPI5 OR if we detect that the original code might fail handling wav
            // But since we want to support WAVs completely, we'll just replace the logic whenever this mod is active.
            // Even if Sapi5Plugin.UseSapi5 is false, checking for .wav existence doesn't hurt.
            
            bool shouldPause = shouldPauseRef(__instance);
            bool isPaused = isPausedRef(__instance);
            
            if (shouldPause)
            {
                if (__instance.speechAudio.volume > 0f)
                {
                    __instance.speechAudio.volume = Math.Max(0f, __instance.speechAudio.volume - Time.deltaTime / PAUSE_FADE_IN_OUT_TIME);
                }
                else if (!isPaused)
                {
                    __instance.speechAudio.Pause();
                    isPausedRef(__instance) = true;
                }
                return false;
            }
            if (isPaused)
            {
                __instance.speechAudio.UnPause();
                isPausedRef(__instance) = false;
            }
            if (__instance.speechAudio.volume < SS.I.ttsVolume)
            {
                __instance.speechAudio.volume = Math.Min(SS.I.ttsVolume, __instance.speechAudio.volume + Time.deltaTime / PAUSE_FADE_IN_OUT_TIME);
            }
            if (__instance.soundQueueDirtyBit)
            {
                Debug.Log("GameSpeechManager Update soundQueueDirtyBit was true: " + Time.realtimeSinceStartup);
                __instance.speechAudio.Stop();
                lock (__instance.currentSoundUuidQueue)
                {
                    __instance.soundQueueDirtyBit = false;
                }
                Debug.Log("GameSpeechManager Update soundQueueDirtyBit was true2: " + Time.realtimeSinceStartup);
            }

            if (!__instance.speechAudio.isPlaying && __instance.currentSoundUuidQueue.Count != 0 && !isUnpackingAudioRef(__instance))
            {
                Debug.Log("GameSpeechManager Update lock2 0: " + Time.realtimeSinceStartup);
                string text;
                lock (__instance.currentSoundUuidQueue)
                {
                    text = __instance.currentSoundUuidQueue.Dequeue();
                    Debug.Log("GameSpeechManager Update play next in queue: " + text + " .. New queue size: " + __instance.currentSoundUuidQueue.Count + " .. " + Time.realtimeSinceStartup);
                }
                Debug.Log("GameSpeechManager Update lock2 1: " + Time.realtimeSinceStartup);
                
                isUnpackingAudioRef(__instance) = true;

                // Determine file path
                string wavPath = Path.Combine(SS.I.tmpDir, text + ".wav");
                string mp3Path = Path.Combine(SS.I.tmpDir, text + ".mp3");
                string finalPath = File.Exists(wavPath) ? wavPath : mp3Path;

                __instance.StartCoroutine(UpdateAndPlayAudioClipFromFilePathAsync(__instance, finalPath));
                
                Debug.Log("GameSpeechManager Update end: " + Time.realtimeSinceStartup);
            }
            
            return false;
        }

        private static IEnumerator UpdateAndPlayAudioClipFromFilePathAsync(GameSpeechManager manager, string filePath)
        {
            Debug.Log("GameSpeechManager got here0: file://" + filePath + " .. " + Time.realtimeSinceStartup);
            AudioType audioType = filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? AudioType.WAV : AudioType.MPEG;
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, audioType))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log("GameSpeechManager Game Speech Manager error: " + www.error);
                    isUnpackingAudioRef(manager) = false;
                    yield break;
                }
                Debug.Log("GameSpeechManager got here1: " + Time.realtimeSinceStartup);
                Utils.DestroyAc(manager.speechAudio.clip);
                manager.speechAudio.clip = DownloadHandlerAudioClip.GetContent(www);
                manager.speechAudio.volume = SS.I.ttsVolume;
                manager.speechAudio.Play();
                Debug.Log("GameSpeechManager Audio is playing: " + Time.realtimeSinceStartup);
                
                try {
                    File.Delete(filePath);
                } catch {}
                
                Debug.Log("GameSpeechManager Deleted temp sound file hopefully: " + Time.realtimeSinceStartup);
                isUnpackingAudioRef(manager) = false;
            }
        }
    }
}
