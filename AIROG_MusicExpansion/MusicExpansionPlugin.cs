using BepInEx;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace AIROG_MusicExpansion
{
    [BepInPlugin("com.airog.musicexpansion", "AI Roguelite Music Expansion", "1.1.0")]
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
        // GameMusicManager is a scene object, so its Awake fires again on every save/scene
        // load. AudioClips created from UnityWebRequest are NOT scene objects, so they survive
        // those reloads. We therefore decode each track from disk exactly once and keep the
        // clip references in these static caches; every subsequent load just re-splices the
        // cached references into the fresh manager (a cheap array copy) with zero re-decoding.
        private static List<AudioClip> s_ambientCache;
        private static List<AudioClip> s_combatCache;

        public static void Postfix(GameMusicManager __instance)
        {
            if (s_ambientCache != null)
            {
                // Cache is warm: inject the already-decoded clips synchronously and keep playing.
                // No buffering needed — this completes within the same frame as Awake.
                InjectCached(__instance);
                return;
            }

            // First Main Scene entry this session: hold playback while we decode from disk once.
            SetShouldPlay(__instance, false);
            __instance.StartCoroutine(FirstLoad(__instance));
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

        private static IEnumerator FirstLoad(GameMusicManager manager)
        {
            s_ambientCache = new List<AudioClip>();
            s_combatCache = new List<AudioClip>();

            string musicPath = Path.Combine(Application.streamingAssetsPath, "Music");
            if (Directory.Exists(musicPath))
            {
                yield return DecodeFolder(Path.Combine(musicPath, "Ambient"), s_ambientCache);
                yield return DecodeFolder(Path.Combine(musicPath, "Encounter"), s_combatCache);
            }

            InjectCached(manager);

            // Buffering done — let the game play from the freshly-extended, shuffled lists.
            SetShouldPlay(manager, true);
            Debug.Log($"[MusicExpansion] Decoded {s_ambientCache.Count} ambient + {s_combatCache.Count} encounter tracks once; cached for all future loads.");
        }

        private static IEnumerator DecodeFolder(string folder, List<AudioClip> into)
        {
            if (!Directory.Exists(folder)) yield break;

            foreach (string file in Directory.GetFiles(folder))
            {
                if (file.EndsWith(".meta")) continue;
                AudioType type = GetAudioType(file);
                if (type == AudioType.UNKNOWN) continue;

                using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + file, type))
                {
                    // Keep the audio compressed in memory instead of expanding every track to
                    // raw PCM. With 47 tracks that is the difference between tens of MB and
                    // potentially hundreds; the small per-play decode is negligible for music.
                    ((DownloadHandlerAudioClip)www.downloadHandler).compressed = true;

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
                        into.Add(clip);
                    }
                }
            }
        }

        private static void InjectCached(GameMusicManager manager)
        {
            if (s_ambientCache.Count > 0)
            {
                AddTracksToManager(manager, s_ambientCache, true);
                ReshufflePlaylist(manager, true);
            }
            if (s_combatCache.Count > 0)
            {
                AddTracksToManager(manager, s_combatCache, false);
                ReshufflePlaylist(manager, false);
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
