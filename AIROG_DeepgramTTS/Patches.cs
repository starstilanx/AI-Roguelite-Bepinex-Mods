using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
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
            // The game starts this without awaiting it, so a fault here would vanish unobserved.
            try
            {
                // Clients play the audio the host broadcasts (as the original does); generating
                // here as well would speak every turn twice.
                if (MpSession.IsClient) return;

                List<TtsHelper.TtsSegment> segments = await TtsHelper.GenerateTtsSegments(manager, turnTxt);
                if (segments.Count == 0) return;

                var tasks = new List<Task<string>>();
                foreach (var seg in segments)
                {
#pragma warning disable CS0618
                    SS.VoiceType voiceType = RoleToVoiceType(seg.role, manager);
#pragma warning restore CS0618
                    // Newlines to spaces first, as the game does: KeepWordishChars drops them
                    // outright, gluing the words on either side of a paragraph break together.
                    foreach (string chunk in SplitForDeepgram(Utils.ReplaceNewlinesWSpaces(seg.text ?? "")))
                        tasks.Add(DeepgramTtsClient.Instance.GenerateTts(Utils.KeepWordishChars(chunk, true), voiceType));
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

                    // Inside the lock, like the original: playback deletes the file once loaded.
                    if (MpSession.IsHost)
                    {
                        string path = Path.Combine(SS.I.tmpDir, finalUuid + ".mp3");
                        MpSession.BroadcastToClients(MpMsgType.TTS_AUDIO, new MpTtsAudio
                        {
                            clearQueue = clearQueue,
                            fileName = Path.GetFileName(path),
                            data = File.ReadAllBytes(path)
                        }.ToBytes());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DeepgramTTS] GenerateAndQueueTts failed: {ex}");
            }
        }

        // Deepgram rejects a request over 2000 characters, and in single-voice mode the whole
        // turn arrives as one segment.
        private const int MAX_CHARS_PER_REQUEST = 1900;

        /// <summary>Cuts text into pieces Deepgram accepts, breaking at sentence ends where it can.</summary>
        internal static List<string> SplitForDeepgram(string text)
        {
            var chunks = new List<string>();
            text = (text ?? "").Trim();
            while (text.Length > MAX_CHARS_PER_REQUEST)
            {
                string head = Utils.TruncateToCharLimitUpToLastSentence(text, MAX_CHARS_PER_REQUEST);
                if (string.IsNullOrEmpty(head)) head = text.Substring(0, MAX_CHARS_PER_REQUEST);
                if (head.Trim().Length > 0) chunks.Add(head.Trim());
                text = text.Substring(head.Length).Trim();
            }
            if (text.Length > 0) chunks.Add(text);
            return chunks;
        }

#pragma warning disable CS0618
        private static SS.VoiceType RoleToVoiceType(string role, GameplayManager manager)
        {
            if (role == "NARRATOR") return SS.VoiceType.NARRATION;
            if (role == "PLAYER") return manager.playerCharacter.GetGender() == SS.Gender.MALE ? SS.VoiceType.MALE : SS.VoiceType.FEMALE;
            // Roles the game's TTS script uses for speakers who are not a known character.
            if (role == "NEW_MALE") return SS.VoiceType.MALE;
            if (role == "NEW_FEMALE") return SS.VoiceType.FEMALE;

            // Any other role is a character name. Uses the voice profile the game has already
            // classified for that character, if any; unclassified characters get the narrator.
            TtsVoiceProfile profile = TtsHelper.FindCharacterByName(role, manager)?.ttsVoiceProfile;
            List<string> tags = profile?.tags;
            if (tags != null)
            {
                if (tags.Any(t => t != null && t.IndexOf("monster", StringComparison.OrdinalIgnoreCase) >= 0)) return SS.VoiceType.MONSTER;
                if (tags.Any(t => t != null && t.IndexOf("robot", StringComparison.OrdinalIgnoreCase) >= 0)) return SS.VoiceType.ROBOT;
                if (tags.Any(t => t != null && t.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0)) return SS.VoiceType.ENEMY;
            }
            if (string.Equals(profile?.gender, "Male", StringComparison.OrdinalIgnoreCase)) return SS.VoiceType.MALE;
            if (string.Equals(profile?.gender, "Female", StringComparison.OrdinalIgnoreCase)) return SS.VoiceType.FEMALE;
            return SS.VoiceType.NARRATION;
        }
#pragma warning restore CS0618
    }
}
