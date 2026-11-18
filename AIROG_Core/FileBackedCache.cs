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
        private string _cachePath; // the file _cache was read from
        private float _lastLoadTime = float.NegativeInfinity;
        private readonly float _refreshRateSeconds;
        private readonly object _lock = new object();

        protected FileBackedCache(float refreshRateSeconds)
        {
            _refreshRateSeconds = refreshRateSeconds;
        }

        /// <summary>File name (not path) inside the active save directory, e.g. "npc_data.json".</summary>
        protected abstract string FileName { get; }

        public T Get()
        {
            lock (_lock)
            {
                ReloadIfStale();
                return _cache;
            }
        }

        public void Invalidate()
        {
            lock (_lock) _lastLoadTime = float.NegativeInfinity;
        }

        private void ReloadIfStale()
        {
            string path = ModSaveFile.Path(FileName);
            // The path is part of the freshness test: the timer alone kept serving the previous
            // save's data for the rest of the refresh window after the player switched saves.
            if (_cache != null && path == _cachePath && Time.realtimeSinceStartup - _lastLoadTime < _refreshRateSeconds) return;
            _lastLoadTime = Time.realtimeSinceStartup;
            Load(path);
        }

        private void Load(string path)
        {
            bool sameFile = path == _cachePath;
            _cachePath = path;

            if (path == null || !File.Exists(path))
            {
                // No backing file for the currently active save (e.g. after a save switch, or
                // the producing mod isn't installed) — reflect that rather than keep serving a
                // previous save's cached value.
                _cache = null;
                return;
            }

            try
            {
                _cache = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                // A read that fails on the same file (caught mid-write) keeps the last good value;
                // on a different save's file there is no good value to keep.
                if (!sameFile) _cache = null;
                Debug.LogWarning($"[FileBackedCache] Failed to load {FileName}: {ex.Message}");
            }
        }
    }
}
