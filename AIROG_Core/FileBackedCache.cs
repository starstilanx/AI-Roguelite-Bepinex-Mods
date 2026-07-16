using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Core
{
    /// <summary>
    /// Base class for the "load a JSON file from the save dir, cache it, refresh after
    /// N seconds" pattern repeated across AIROG_GenContext's context providers. Always
    /// uses Time.realtimeSinceStartup so cache expiry is consistent even when
    /// Time.timeScale is 0 (paused scenes) — some providers previously used Time.time,
    /// which stalls while paused and would drift out of sync with the others.
    /// </summary>
    public abstract class FileBackedCache<T> where T : class
    {
        private T _cache;
        private float _lastLoadTime = float.NegativeInfinity;
        private readonly float _refreshRateSeconds;

        protected FileBackedCache(float refreshRateSeconds)
        {
            _refreshRateSeconds = refreshRateSeconds;
        }

        /// <summary>File name (not path) inside the active save directory, e.g. "npc_data.json".</summary>
        protected abstract string FileName { get; }

        public T Get()
        {
            ReloadIfStale();
            return _cache;
        }

        public void Invalidate() => _lastLoadTime = float.NegativeInfinity;

        private void ReloadIfStale()
        {
            if (_cache != null && Time.realtimeSinceStartup - _lastLoadTime < _refreshRateSeconds) return;
            _lastLoadTime = Time.realtimeSinceStartup;
            Load();
        }

        private void Load()
        {
            string path = ModSaveFile.Path(FileName);
            if (path == null || !File.Exists(path)) return;

            try
            {
                _cache = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileBackedCache] Failed to load {FileName}: {ex.Message}");
            }
        }
    }
}
