using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AIROG_DeepgramTTS
{
    public class DeepgramTtsClient
    {
        private static DeepgramTtsClient _instance;
        public static DeepgramTtsClient Instance => _instance ??= new DeepgramTtsClient();

        private const string API_ENDPOINT = "https://api.deepgram.com/v1/speak";

        public async Task<string> GenerateTts(string text, SS.VoiceType voiceType)
        {
            try
            {
                string apiKey = DeepgramTtsPlugin.DeepgramApiKey.Value;
                if (string.IsNullOrEmpty(apiKey))
                {
                    Debug.LogError("Deepgram API Key missing! Set it in the options menu.");
                    return null;
                }

                string voiceName = GetVoiceName(voiceType);
                string url = $"{API_ENDPOINT}?model={voiceName}";

                var payload = new JObject
                {
                    ["text"] = text
                };

                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Token {apiKey}");
                    
                    var response = await client.PostAsync(url, new StringContent(payload.ToString(), Encoding.UTF8, "application/json"));
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        Debug.LogError($"Deepgram TTS API Error: {response.StatusCode} - {error}");
                        return null;
                    }

                    byte[] audioBytes = await response.Content.ReadAsByteArrayAsync();
                    string uuid = Guid.NewGuid().ToString();
                    string mp3Path = Path.Combine(SS.I.tmpDir, uuid + ".mp3");

                    // Deepgram defaults to MP3 unless specified otherwise
                    File.WriteAllBytes(mp3Path, audioBytes);
                    return uuid;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Deepgram TTS Client Exception: {ex.Message}");
                return null;
            }
        }

        private string GetVoiceName(SS.VoiceType type)
        {
            return type switch
            {
                SS.VoiceType.NARRATION => DeepgramTtsPlugin.VoiceNarration.Value,
                SS.VoiceType.MALE => DeepgramTtsPlugin.VoiceMale.Value,
                SS.VoiceType.FEMALE => DeepgramTtsPlugin.VoiceFemale.Value,
                SS.VoiceType.MONSTER => DeepgramTtsPlugin.VoiceMonster.Value,
                SS.VoiceType.ROBOT => DeepgramTtsPlugin.VoiceRobot.Value,
                SS.VoiceType.ENEMY => DeepgramTtsPlugin.VoiceEnemy.Value,
                _ => DeepgramTtsPlugin.VoiceNarration.Value
            };
        }

        public async Task<string> ConcatenateAudioFiles(List<string> uuids)
        {
            if (uuids == null || uuids.Count == 0) return null;
            if (uuids.Count == 1) return uuids[0];

            try
            {
                string combinedUuid = Guid.NewGuid().ToString();
                string combinedPath = Path.Combine(SS.I.tmpDir, combinedUuid + ".mp3");
                
                StringBuilder filter = new StringBuilder();
                StringBuilder inputs = new StringBuilder();
                for (int i = 0; i < uuids.Count; i++)
                {
                    inputs.Append($"-i \"{Path.Combine(SS.I.tmpDir, uuids[i] + ".mp3")}\" ");
                    filter.Append($"[{i}:a]");
                }
                filter.Append($"concat=n={uuids.Count}:v=0:a=1[a]");

                string ffmpegPath = Path.Combine(SS.I.toolsDir, "ffmpeg.exe");
                string arguments = $"-y {inputs} -filter_complex \"{filter}\" -map \"[a]\" -c:a libmp3lame -b:a 320k \"{combinedPath}\"";

                await Utils.ExecuteCommandAsync(ffmpegPath, arguments);

                if (File.Exists(combinedPath))
                {
                    foreach (var uuid in uuids)
                    {
                        string partPath = Path.Combine(SS.I.tmpDir, uuid + ".mp3");
                        if (File.Exists(partPath)) File.Delete(partPath);
                    }
                    return combinedUuid;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Deepgram TTS Concatenate Exception: {ex.Message}");
            }
            return null;
        }
    }
}
