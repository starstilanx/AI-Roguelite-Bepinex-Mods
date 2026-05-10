using HarmonyLib;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine;

namespace AIROG_DeepgramTTS
{
    [HarmonyPatch(typeof(TtsClient), "GenerateAndQueueTts")]
    public static class TtsClientPatches
    {
        [HarmonyPrefix]
        public static bool GenerateAndQueueTtsPrefix(GameplayManager manager, string turnTxt, bool clearQueue, ref Task __result)
        {
            if (!DeepgramTtsPlugin.UseDeepgramTts.Value)
                return true;
            __result = GenerateAndQueueTtsAsync(manager, turnTxt, clearQueue);
            return false;
        }

        private static async Task GenerateAndQueueTtsAsync(GameplayManager manager, string turnTxt, bool clearQueue)
        {
            List<TtsHelper.TtsSegment> segments = await TtsHelper.GenerateTtsSegments(manager, turnTxt);
            if (segments.Count == 0) return;

            var tasks = new List<Task<string>>();
            foreach (var seg in segments)
            {
#pragma warning disable CS0618
                SS.VoiceType voiceType = RoleToVoiceType(seg.role, manager.playerCharacter.GetGender());
#pragma warning restore CS0618
                string text = seg.text;
                tasks.Add(DeepgramTtsClient.Instance.GenerateTts(Utils.KeepWordishChars(text, true), voiceType));
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

#pragma warning disable CS0618
        private static SS.VoiceType RoleToVoiceType(string role, SS.Gender playerGender)
        {
            if (role == "NARRATOR") return SS.VoiceType.NARRATION;
            if (role == "PLAYER") return playerGender == SS.Gender.MALE ? SS.VoiceType.MALE : SS.VoiceType.FEMALE;
            return SS.VoiceType.NARRATION;
        }
#pragma warning restore CS0618
    }
}
