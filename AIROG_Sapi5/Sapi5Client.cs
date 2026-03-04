using System;
using System.Collections.Generic;
using System.IO;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using UnityEngine;
using System.Linq;

namespace AIROG_Sapi5
{
    public class Sapi5Client
    {
        private static Sapi5Client _instance;
        public static Sapi5Client Instance => _instance ??= new Sapi5Client();

        public async Task<string> GenerateTts(string text, string voiceName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Sapi5Plugin.UseSapi5.Value) return null;

                    Debug.Log($"[SAPI5] GenerateTts called for voice: {voiceName}, text length: {text?.Length ?? 0}");

                    string uuid = Guid.NewGuid().ToString();
                    string wavPath = Path.Combine(SS.I.tmpDir, uuid + ".wav");

                    using (SpeechSynthesizer synth = new SpeechSynthesizer())
                    {
                        // Select Voice
                        // If exact match fails, try partial match
                        // Use SelectVoice (exact) or SelectVoiceByHints
                        bool voiceFound = false;
                        foreach (var v in synth.GetInstalledVoices())
                        {
                            if (v.VoiceInfo.Name.Equals(voiceName, StringComparison.OrdinalIgnoreCase))
                            {
                                synth.SelectVoice(v.VoiceInfo.Name);
                                voiceFound = true;
                                Debug.Log($"[SAPI5] Voice matched exactly: {v.VoiceInfo.Name}");
                                break;
                            }
                        }
                        
                        if (!voiceFound && !string.IsNullOrEmpty(voiceName))
                        {
                             // Try contains
                             foreach (var v in synth.GetInstalledVoices())
                             {
                                 if (v.VoiceInfo.Name.IndexOf(voiceName, StringComparison.OrdinalIgnoreCase) >= 0)
                                 {
                                     synth.SelectVoice(v.VoiceInfo.Name);
                                     voiceFound = true;
                                     Debug.Log($"[SAPI5] Voice matched partially: {v.VoiceInfo.Name}");
                                     break;
                                 }
                             }
                        }

                        if (!voiceFound)
                        {
                            Debug.LogWarning($"[SAPI5] Voice '{voiceName}' not found, using default voice");
                        }

                        // Configure Audio
                        synth.SetOutputToWaveFile(wavPath);
                        synth.Rate = Sapi5Plugin.Rate.Value;
                        synth.Volume = Sapi5Plugin.Volume.Value;

                        synth.Speak(text);
                    }

                    Debug.Log($"[SAPI5] Generated TTS file: {wavPath}");
                    return uuid;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"SAPI5 Client Exception: {ex.Message}");
                    return null;
                }
            });
        }

        public async Task<string> ConcatenateAudioFiles(List<string> uuids)
        {
            return await Task.Run(() =>
            {
                if (uuids == null || uuids.Count == 0) return null;
                if (uuids.Count == 1) return uuids[0];

                try
                {
                    string combinedUuid = Guid.NewGuid().ToString();
                    string combinedPath = Path.Combine(SS.I.tmpDir, combinedUuid + ".wav");

                    List<string> files = uuids.Select(u => Path.Combine(SS.I.tmpDir, u + ".wav")).Where(File.Exists).ToList();
                    
                    if (files.Count == 0) return null;

                    // Concatenate WAVs manually
                    MergeWavFiles(files, combinedPath);

                    // Clean up parts
                    foreach (var f in files)
                    {
                        try { File.Delete(f); } catch { }
                    }

                    return combinedUuid;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"SAPI5 Concatenate Exception: {ex.Message}");
                    return null;
                }
            });
        }

        private void MergeWavFiles(List<string> inputs, string output)
        {
            if (inputs.Count == 0) return;

            using (var outputStream = new FileStream(output, FileMode.Create))
            using (var writer = new BinaryWriter(outputStream))
            {
                // Read first file to get format
                byte[] firstHeader = File.ReadAllBytes(inputs[0]).Take(44).ToArray(); 
                // We assume all files have same format since they come from same settings/SAPI5 session logic
                // Ideally we verify check parameters, but SAPI5 usually output standard PCM 
                
                // Write header placeholder
                writer.Write(firstHeader);
                
                long totalDataLen = 0;

                foreach (var input in inputs)
                {
                    using (var fs = new FileStream(input, FileMode.Open))
                    using (var reader = new BinaryReader(fs))
                    {
                        if (fs.Length < 44) continue;
                        
                        // Skip header
                        fs.Seek(44, SeekOrigin.Begin);
                        
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, bytesRead);
                            totalDataLen += bytesRead;
                        }
                    }
                }

                // Update Header lengths
                // ChunkSize (at 4) = 36 + SubChunk2Size
                // SubChunk2Size (at 40) = totalDataLen
                
                writer.Seek(4, SeekOrigin.Begin);
                writer.Write((int)(36 + totalDataLen));
                
                writer.Seek(40, SeekOrigin.Begin);
                writer.Write((int)totalDataLen);
            }
        }
    }
}
