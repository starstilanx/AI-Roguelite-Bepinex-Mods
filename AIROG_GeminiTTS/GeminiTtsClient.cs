using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AIROG_GeminiTTS
{
    public class GeminiTtsClient
    {
        private static GeminiTtsClient _instance;
        public static GeminiTtsClient Instance => _instance ??= new GeminiTtsClient();

        private const string API_ENDPOINT = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-preview-tts:generateContent";

        public async Task<string> GenerateTts(string text, SS.VoiceType voiceType)
        {
            try
            {
                string apiKey = GetApiKey();
                if (string.IsNullOrEmpty(apiKey))
                {
                    Debug.LogError("Gemini API Key missing! Set it in the BepInEx config file for Gemini TTS.");
                    return null;
                }

                string voiceName = GetVoiceName(voiceType);

                var payload = new JObject();
                payload["contents"] = new JArray(new JObject
                {
                    ["parts"] = new JArray(new JObject { ["text"] = text })
                });
                
                payload["generationConfig"] = new JObject
                {
                    ["responseModalities"] = new JArray("AUDIO"),
                    ["speechConfig"] = new JObject
                    {
                        ["voiceConfig"] = new JObject
                        {
                            ["prebuiltVoiceConfig"] = new JObject { ["voiceName"] = voiceName }
                        },
                        ["audioConfig"] = new JObject
                        {
                            ["speakingRate"] = GeminiTtsPlugin.SpeakingRate.Value
                        }
                    }
                };

                string url = $"{API_ENDPOINT}?key={apiKey}";
                using (HttpClient client = new HttpClient())
                {
                    var response = await client.PostAsync(url, new StringContent(payload.ToString(), Encoding.UTF8, "application/json"));
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        Debug.LogError($"Gemini TTS API Error: {response.StatusCode} - {error}\nPayload Sent: {payload.ToString()}");
                        return null;
                    }

                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    var jobj = JObject.Parse(jsonResponse);
                    
                    var candidates = jobj["candidates"] as JArray;
                    if (candidates == null || candidates.Count == 0)
                    {
                        Debug.LogError("Gemini TTS: No candidates in response.");
                        return null;
                    }

                    string base64Audio = null;
                    string mimeType = null;

                    var parts = candidates[0]?["content"]?["parts"] as JArray;
                    if (parts != null)
                    {
                        foreach (var part in parts)
                        {
                            if (part["inlineData"] != null)
                            {
                                base64Audio = part["inlineData"]["data"]?.ToString();
                                mimeType = part["inlineData"]["mimeType"]?.ToString();
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(base64Audio))
                    {
                        Debug.LogError("Gemini TTS: No audio data found in candidates[0].content.parts");
                        return null;
                    }

                    byte[] audioBytes = Convert.FromBase64String(base64Audio);
                    string uuid = Guid.NewGuid().ToString();
                    string mp3Path = Path.Combine(SS.I.tmpDir, uuid + ".mp3");

                    if (mimeType == "audio/mpeg" || mimeType == "audio/mp3")
                    {
                        File.WriteAllBytes(mp3Path, audioBytes);
                        return uuid;
                    }
                    else
                    {
                        Debug.Log($"Gemini TTS: Received non-MP3 audio ({mimeType}). Attempting conversion via FFmpeg.");
                        string tempFileFull = Path.Combine(SS.I.tmpDir, uuid);
                        string pcmPath = tempFileFull + ".pcm";
                        File.WriteAllBytes(pcmPath, audioBytes);

                        string ffmpegPath = Path.Combine(SS.I.toolsDir, "ffmpeg.exe");
                        string arguments = $"-y -f s16le -ar 24000 -ac 1 -i \"{pcmPath}\" \"{mp3Path}\"";
                        
                        await Utils.ExecuteCommandAsync(ffmpegPath, arguments);
                        
                        if (File.Exists(mp3Path))
                        {
                            File.Delete(pcmPath);
                            return uuid;
                        }
                        else
                        {
                            Debug.LogError("FFmpeg failed to generate MP3 from Gemini output.");
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Gemini TTS Client Exception: {ex.Message}");
                return null;
            }
        }

        private string GetVoiceName(SS.VoiceType type)
        {
            return type switch
            {
                SS.VoiceType.NARRATION => GeminiTtsPlugin.VoiceNarration.Value,
                SS.VoiceType.MALE => GeminiTtsPlugin.VoiceMale.Value,
                SS.VoiceType.FEMALE => GeminiTtsPlugin.VoiceFemale.Value,
                SS.VoiceType.MONSTER => GeminiTtsPlugin.VoiceMonster.Value,
                SS.VoiceType.ROBOT => GeminiTtsPlugin.VoiceRobot.Value,
                SS.VoiceType.ENEMY => GeminiTtsPlugin.VoiceEnemy.Value,
                _ => GeminiTtsPlugin.VoiceNarration.Value
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
                    // Optionally delete parts
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
                Debug.LogError($"Gemini TTS Concatenate Exception: {ex.Message}");
            }
            return null;
        }

        private string GetApiKey()
        {
            if (GeminiTtsPlugin.GeminiApiKey != null && !string.IsNullOrEmpty(GeminiTtsPlugin.GeminiApiKey.Value))
            {
                return GeminiTtsPlugin.GeminiApiKey.Value;
            }

            // Fallback for transition/backward compatibility if needed, 
            // but user requested to switch away from model-config folder.
            if (SS.I.geminiConfigVals != null)
            {
                if (SS.I.geminiConfigVals["api_key"] != null) return SS.I.geminiConfigVals["api_key"].ToString();
                if (SS.I.geminiConfigVals["apiKey"] != null) return SS.I.geminiConfigVals["apiKey"].ToString();
                if (SS.I.geminiConfigVals["key"] != null) return SS.I.geminiConfigVals["key"].ToString();
            }
            return null;
        }
    }
}
