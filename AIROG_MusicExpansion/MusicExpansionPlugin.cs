using BepInEx;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace AIROG_MusicExpansion
{
    [BepInPlugin("com.airog.musicexpansion", "AI Roguelite Music Expansion", "1.0.0")]
    public class MusicExpansionPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Logger.LogInfo("Music Expansion Plugin Loaded");
            Harmony harmony = new Harmony("com.airog.musicexpansion");
            harmony.PatchAll();
        }
    }

    [HarmonyPatch(typeof(GameMusicManager), "Awake")]
    public static class GameMusicManager_Awake_Patch
    {
        public static void Postfix(GameMusicManager __instance)
        {
            // buffer: Prevent game from starting music immediately so we can inject our tracks first
            SetShouldPlay(__instance, false);
            
            __instance.StartCoroutine(LoadCustomMusic(__instance));
        }

        private static void SetShouldPlay(GameMusicManager manager, bool shouldPlay)
        {
            try
            {
                var pojoField = AccessTools.Field(typeof(GameMusicManager), "ambientAudioPojo");
                object pojoInstance = pojoField.GetValue(manager);
                if (pojoInstance != null)
                {
                    var shouldPlayField = AccessTools.Field(pojoInstance.GetType(), "shouldBePlaying");
                    shouldPlayField.SetValue(pojoInstance, shouldPlay);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MusicExpansion] Error setting play state: {ex.Message}");
            }
        }

        private static IEnumerator LoadCustomMusic(GameMusicManager manager)
        {
            string musicPath = Path.Combine(Application.streamingAssetsPath, "Music");
            if (!Directory.Exists(musicPath))
            {
                // If no folder, just restore playback immediately
                SetShouldPlay(manager, true);
                yield break;
            }

            // Load them
            yield return LoadTracks(Path.Combine(musicPath, "Ambient"), manager, true);
            yield return LoadTracks(Path.Combine(musicPath, "Encounter"), manager, false);

            // Now that we are done buffering, let the game play music (it will pick from the new verified shuffled list)
            SetShouldPlay(manager, true);
            Debug.Log("[MusicExpansion] Custom tracks buffered. Enabling playback.");
        }

        private static IEnumerator LoadTracks(string folder, GameMusicManager manager, bool isAmbient)
        {
            if (!Directory.Exists(folder)) yield break;

            var clips = new List<AudioClip>();

            foreach (string file in Directory.GetFiles(folder))
            {
                if (file.EndsWith(".meta")) continue;
                AudioType type = GetAudioType(file);
                if (type == AudioType.UNKNOWN) continue;

                using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + file, type))
                {
                    yield return www.SendWebRequest();

                    if (www.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[MusicExpansion] Error loading {file}: {www.error}");
                        continue;
                    }

                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    if (clip != null)
                    {
                        clip.name = Path.GetFileNameWithoutExtension(file);
                        clips.Add(clip);
                    }
                }
            }

            if (clips.Count == 0) yield break;

            // Single batch insert — one array allocation, two reflection calls total
            AddTracksToManager(manager, clips, isAmbient);
            ReshufflePlaylist(manager, isAmbient);
            Debug.Log($"[MusicExpansion] Loaded {clips.Count} {(isAmbient ? "ambient" : "encounter")} tracks.");
        }

        private static void ReshufflePlaylist(GameMusicManager manager, bool isAmbient)
        {
            try 
            {
                string fieldName = isAmbient ? "ambientAudioPojo" : "combatAudioPojo";
                var pojoField = AccessTools.Field(typeof(GameMusicManager), fieldName);
                object pojoInstance = pojoField.GetValue(manager);

                if (pojoInstance != null)
                {
                    Type pojoType = pojoInstance.GetType();
                    var tracksField = AccessTools.Field(pojoType, "tracks");
                    AudioClip[] currentTracks = (AudioClip[])tracksField.GetValue(pojoInstance);
                    
                    // Use the game's built-in shuffle
                    AudioClip[] shuffledTracks = Utils.AdvancedShuffle(currentTracks);
                    
                    tracksField.SetValue(pojoInstance, shuffledTracks);
                    Debug.Log($"[MusicExpansion] Reshuffled {(isAmbient ? "Ambient" : "Encounter")} playlist with {shuffledTracks.Length} tracks.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MusicExpansion] Failed to reshuffle playlist: {e.Message}");
            }
        }

        private static AudioType GetAudioType(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            switch (ext)
            {
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".mp3": return AudioType.MPEG;
                case ".aiff": return AudioType.AIFF;
                default: return AudioType.UNKNOWN;
            }
        }

        private static void AddTracksToManager(GameMusicManager manager, List<AudioClip> clips, bool isAmbient)
        {
            // Update public array once
            AudioClip[] existing = isAmbient ? manager.ambientTracks : manager.combatTracks;
            int offset = existing?.Length ?? 0;
            AudioClip[] merged = new AudioClip[offset + clips.Count];
            if (existing != null) Array.Copy(existing, merged, offset);
            for (int i = 0; i < clips.Count; i++) merged[offset + i] = clips[i];

            if (isAmbient) manager.ambientTracks = merged;
            else           manager.combatTracks  = merged;

            // Inject into the private AudioPojo once (field lookups cached here, not per-clip)
            string fieldName = isAmbient ? "ambientAudioPojo" : "combatAudioPojo";
            var pojoField    = AccessTools.Field(typeof(GameMusicManager), fieldName);
            object pojo      = pojoField.GetValue(manager);
            if (pojo == null) return;

            var tracksField      = AccessTools.Field(pojo.GetType(), "tracks");
            AudioClip[] pojoExisting = (AudioClip[])tracksField.GetValue(pojo);
            int pojoOffset       = pojoExisting?.Length ?? 0;
            AudioClip[] pojoMerged = new AudioClip[pojoOffset + clips.Count];
            if (pojoExisting != null) Array.Copy(pojoExisting, pojoMerged, pojoOffset);
            for (int i = 0; i < clips.Count; i++) pojoMerged[pojoOffset + i] = clips[i];
            tracksField.SetValue(pojo, pojoMerged);
        }
    }
}
