using HarmonyLib;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine;
using System;

namespace AIROG_DeepgramTTS
{
    [HarmonyPatch(typeof(TiktokTtsClient))]
    public static class TiktokTtsClientPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("GenerateMultipleTtsForTmp")]
        public static bool GenerateMultipleTtsForTmpPrefix(TiktokTtsClient __instance, string strForTts, string voiceName, ref Task<List<string>> __result)
        {
            if (!DeepgramTtsPlugin.UseDeepgramTts.Value)
            {
                return true;
            }
            __result = GenerateMultipleTtsForTmpAsync(strForTts, voiceName);
            return false;
        }

        private static async Task<List<string>> GenerateMultipleTtsForTmpAsync(string strForTts, string voiceName)
        {
            List<AnnotatedStrForTts> list = TtsHelper.ExtractAnnotatedStringsForTiktokTts2(strForTts);
            List<Task<string>> tasks = new List<Task<string>>();
            
            // Basic heuristic for voice type if not specified by annotations
            SS.VoiceType voiceType = voiceName.Contains("female") ? SS.VoiceType.FEMALE : SS.VoiceType.NARRATION;

            foreach (var annotatedStr in list)
            {
                tasks.Add(DeepgramTtsClient.Instance.GenerateTts(Utils.KeepWordishChars(annotatedStr.content, true), voiceType));
            }

            string[] uuids = await Task.WhenAll(tasks);
            return uuids.Where(u => !string.IsNullOrEmpty(u)).ToList();
        }

        [HarmonyPrefix]
        [HarmonyPatch("GenerateMultipleTtsAndUpdateQueue")]
        public static bool GenerateMultipleTtsAndUpdateQueuePrefix(TiktokTtsClient __instance, GameplayManager manager, string strForTts, SS.VoiceType npcVoiceType, int tryStrLength, bool dialogueOnly, bool clearQueue, ref Task __result)
        {
            if (!DeepgramTtsPlugin.UseDeepgramTts.Value)
            {
                return true;
            }
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
                SS.VoiceType voiceType = GetVoiceType(annotatedStr, npcVoiceType, manager.playerCharacter.GetGender());
                tasks.Add(DeepgramTtsClient.Instance.GenerateTts(Utils.KeepWordishChars(annotatedStr.content, true), voiceType));
            }

            string[] uuids = await Task.WhenAll(tasks);
            List<string> validUuids = uuids.Where(u => !string.IsNullOrEmpty(u)).ToList();

            if (validUuids.Count == 0) return;

            string finalUuid = await DeepgramTtsClient.Instance.ConcatenateAudioFiles(validUuids);

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

        private static SS.VoiceType GetVoiceType(AnnotatedStrForTts annotatedStr, SS.VoiceType npcVoiceType, SS.Gender playerGender)
        {
            return annotatedStr.speakerType switch
            {
                AnnotatedStrForTts.SpeakerType.NARRATOR => SS.VoiceType.NARRATION,
                AnnotatedStrForTts.SpeakerType.PLAYER => (playerGender == SS.Gender.MALE) ? SS.VoiceType.MALE : SS.VoiceType.FEMALE,
                AnnotatedStrForTts.SpeakerType.NPC_MAN => SS.VoiceType.MALE,
                AnnotatedStrForTts.SpeakerType.NPC_WOMAN => SS.VoiceType.FEMALE,
                AnnotatedStrForTts.SpeakerType.NPC_NEUTRAL => npcVoiceType,
                _ => SS.VoiceType.NARRATION,
            };
        }
    }
}
