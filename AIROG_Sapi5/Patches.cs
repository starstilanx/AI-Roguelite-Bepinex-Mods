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
    [HarmonyPatch(typeof(TiktokTtsClient))]
    public static class TiktokTtsClientPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("GenerateMultipleTtsForTmp")]
        public static bool GenerateMultipleTtsForTmpPrefix(TiktokTtsClient __instance, string strForTts, string voiceName, ref Task<List<string>> __result)
        {
            if (!Sapi5Plugin.UseSapi5.Value) return true;
            Debug.Log("[SAPI5] GenerateMultipleTtsForTmpPrefix - SAPI5 taking over TTS");
            __result = GenerateMultipleTtsForTmpAsync(strForTts);
            return false;
        }

        private static async Task<List<string>> GenerateMultipleTtsForTmpAsync(string strForTts)
        {
            Debug.Log("[SAPI5] GenerateMultipleTtsForTmpAsync called");
            List<AnnotatedStrForTts> list = TtsHelper.ExtractAnnotatedStringsForTiktokTts2(strForTts);
            List<Task<string>> tasks = new List<Task<string>>();
            
            foreach (var annotatedStr in list)
            {
                // Map speaker type to SAPI5 voice from config instead of using TikTok voiceName
                string sapi5Voice = GetSapi5VoiceForSpeakerType(annotatedStr.speakerType, SS.Gender.UNKNOWN);
                Debug.Log($"[SAPI5] Speaking as {annotatedStr.speakerType} with voice: {sapi5Voice}");
                tasks.Add(Sapi5Client.Instance.GenerateTts(Utils.KeepWordishChars(annotatedStr.content, true), sapi5Voice));
            }

            string[] uuids = await Task.WhenAll(tasks);
            return uuids.Where(u => !string.IsNullOrEmpty(u)).ToList();
        }

        private static string GetSapi5VoiceForSpeakerType(AnnotatedStrForTts.SpeakerType speakerType, SS.Gender playerGender)
        {
            return speakerType switch
            {
                AnnotatedStrForTts.SpeakerType.NARRATOR => Sapi5Plugin.VoiceNarration.Value,
                AnnotatedStrForTts.SpeakerType.PLAYER => (playerGender == SS.Gender.MALE) ? Sapi5Plugin.VoiceMale.Value : Sapi5Plugin.VoiceFemale.Value,
                AnnotatedStrForTts.SpeakerType.NPC_MAN => Sapi5Plugin.VoiceMale.Value,
                AnnotatedStrForTts.SpeakerType.NPC_WOMAN => Sapi5Plugin.VoiceFemale.Value,
                AnnotatedStrForTts.SpeakerType.NPC_NEUTRAL => Sapi5Plugin.VoiceNarration.Value,
                _ => Sapi5Plugin.VoiceNarration.Value,
            };
        }

        [HarmonyPrefix]
        [HarmonyPatch("GenerateMultipleTtsAndUpdateQueue")]
        public static bool GenerateMultipleTtsAndUpdateQueuePrefix(TiktokTtsClient __instance, GameplayManager manager, string strForTts, SS.VoiceType npcVoiceType, int tryStrLength, bool dialogueOnly, bool clearQueue, ref Task __result)
        {
            if (!Sapi5Plugin.UseSapi5.Value) return true;
            Debug.Log("[SAPI5] GenerateMultipleTtsAndUpdateQueuePrefix - SAPI5 taking over TTS");
            __result = GenerateMultipleTtsAndUpdateQueueAsync(manager, strForTts, npcVoiceType, dialogueOnly, clearQueue);
            return false;
        }

        private static async Task GenerateMultipleTtsAndUpdateQueueAsync(GameplayManager manager, string strForTts, SS.VoiceType npcVoiceType, bool dialogueOnly, bool clearQueue)
        {
             List<AnnotatedStrForTts> list = TtsHelper.ExtractAnnotatedStringsForTiktokTts2(strForTts);
             if (dialogueOnly)
             {
                 list = list.Where((AnnotatedStrForTts an) => an.speakerType != AnnotatedStrForTts.SpeakerType.NARRATOR && an.speakerType != AnnotatedStrForTts.SpeakerType.UNKNOWN).ToList();
             }

             List<Task<string>> tasks = new List<Task<string>>();
             foreach (var annotatedStr in list)
             {
                 string voiceName = GetVoiceName(annotatedStr, npcVoiceType, manager.playerCharacter.GetGender());
                 tasks.Add(Sapi5Client.Instance.GenerateTts(Utils.KeepWordishChars(annotatedStr.content, true), voiceName));
             }

             string[] uuids = await Task.WhenAll(tasks);
             List<string> validUuids = uuids.Where(u => !string.IsNullOrEmpty(u)).ToList();

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
        }

        private static string GetVoiceName(AnnotatedStrForTts annotatedStr, SS.VoiceType npcVoiceType, SS.Gender playerGender)
        {
            var type = annotatedStr.speakerType switch
            {
                AnnotatedStrForTts.SpeakerType.NARRATOR => SS.VoiceType.NARRATION,
                AnnotatedStrForTts.SpeakerType.PLAYER => (playerGender == SS.Gender.MALE) ? SS.VoiceType.MALE : SS.VoiceType.FEMALE,
                AnnotatedStrForTts.SpeakerType.NPC_MAN => SS.VoiceType.MALE,
                AnnotatedStrForTts.SpeakerType.NPC_WOMAN => SS.VoiceType.FEMALE,
                AnnotatedStrForTts.SpeakerType.NPC_NEUTRAL => npcVoiceType,
                _ => SS.VoiceType.NARRATION,
            };

            return type switch
            {
                SS.VoiceType.NARRATION => Sapi5Plugin.VoiceNarration.Value,
                SS.VoiceType.MALE => Sapi5Plugin.VoiceMale.Value,
                SS.VoiceType.FEMALE => Sapi5Plugin.VoiceFemale.Value,
                SS.VoiceType.MONSTER => Sapi5Plugin.VoiceMonster.Value,
                SS.VoiceType.ROBOT => Sapi5Plugin.VoiceRobot.Value,
                SS.VoiceType.ENEMY => Sapi5Plugin.VoiceEnemy.Value,
                _ => Sapi5Plugin.VoiceNarration.Value
            };
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
