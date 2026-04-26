using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;

namespace AIROG_GCHelper
{
    [BepInPlugin("com.airog.gchelper", "AIROG GC Helper", "1.0.0")]
    public class GCHelperPlugin : BaseUnityPlugin
    {
        // Collect at most once every 30 seconds, and only every N turns.
        private const int TurnsPerCollect = 5;
        private const float MinSecondsBetweenCollects = 30f;

        private int _turnsSinceLastCollect = 0;
        private float _lastCollectTime = -999f;

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
            Logger.LogInfo("[GCHelper] Loaded. Will collect every " + TurnsPerCollect + " turns (min " + MinSecondsBetweenCollects + "s apart).");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            GameplayManager.TurnHappenedEvent -= OnTurnHappened;
        }

        private void OnTurnHappened(int numTurns, long secs)
        {
            _turnsSinceLastCollect++;
            if (_turnsSinceLastCollect < TurnsPerCollect) return;
            if (Time.realtimeSinceStartup - _lastCollectTime < MinSecondsBetweenCollects) return;

            Collect("turn");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Scene transitions are natural cleanup points; no time gate needed here.
            Collect("scene load: " + scene.name);
        }

        private void Collect(string reason)
        {
            _turnsSinceLastCollect = 0;
            _lastCollectTime = Time.realtimeSinceStartup;

            long before = GC.GetTotalMemory(false) / (1024 * 1024);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            long after = GC.GetTotalMemory(false) / (1024 * 1024);

            Logger.LogInfo($"[GCHelper] Collected ({reason}): {before} MB -> {after} MB (freed {before - after} MB)");
        }
    }
}
