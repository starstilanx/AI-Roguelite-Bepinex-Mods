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
            if (!Directory.Exists(folder))
            {
                yield break;
            }

            string[] files = Directory.GetFiles(folder);
            bool addedAny = false;
            foreach (string file in files)
            {
                if (file.EndsWith(".meta")) continue;

                string url = "file://" + file;
                AudioType type = GetAudioType(file);

                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, type))
                {
                    yield return www.SendWebRequest();

                    if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
                    {
                        Debug.LogError($"[MusicExpansion] Error loading {file}: {www.error}");
                    }
                    else
                    {
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                        if (clip != null)
                        {
                            clip.name = Path.GetFileNameWithoutExtension(file);
                            AddTrackToManager(manager, clip, isAmbient);
                            addedAny = true;
                        }
                    }
                }
            }

            if (addedAny)
            {
                ReshufflePlaylist(manager, isAmbient);
            }
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

        private static void AddTrackToManager(GameMusicManager manager, AudioClip clip, bool isAmbient)
        {
            // Update the public array (good for inspection or subsequent reloads)
            if (isAmbient)
            {
                manager.ambientTracks = AddToArray(manager.ambientTracks, clip);
            }
            else
            {
                manager.combatTracks = AddToArray(manager.combatTracks, clip);
            }

            // Inject into the private active AudioPojo
            string fieldName = isAmbient ? "ambientAudioPojo" : "combatAudioPojo";
            
            // Access the private field 'ambientAudioPojo' / 'combatAudioPojo' in GameMusicManager
            var pojoField = AccessTools.Field(typeof(GameMusicManager), fieldName);
            object pojoInstance = pojoField.GetValue(manager);

            if (pojoInstance != null)
            {
                // Access the public 'tracks' field in the private AudioPojo class
                // Since the class is private, we use AccessTools/Reflection to get the field
                // Note: The field 'tracks' is public inside the private class, but since we don't have the type, we rely on AccessTools by name or type finding.
                var tracksField = AccessTools.Field(pojoInstance.GetType(), "tracks");
                
                AudioClip[] currentTracks = (AudioClip[])tracksField.GetValue(pojoInstance);
                AudioClip[] newTracks = AddToArray(currentTracks, clip);
                
                tracksField.SetValue(pojoInstance, newTracks);
            }
        }

        private static T[] AddToArray<T>(T[] array, T item)
        {
            if (array == null) return new T[] { item };
            T[] newArray = new T[array.Length + 1];
            Array.Copy(array, newArray, array.Length);
            newArray[array.Length] = item;
            return newArray;
        }
    }
}
