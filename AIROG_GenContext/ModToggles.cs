using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext
{
    /// <summary>
    /// Per-save on/off switches for whole mods. This is what the "Mods" checklist edits.
    ///
    /// History: the checklist used to flip one global flag per context provider, and all that flag
    /// did was stop that provider's text reaching the prompt. The mod behind it kept running -
    /// Reverie still intercepted "I go to sleep" and printed its dream start/end lines, Settlement
    /// still showed its button in every save. Two things changed:
    ///
    ///   1. A switch is now the mod's master switch. Every gameplay mod asks
    ///      <see cref="IsEnabled"/> at its own entry points (turn ticks, input/response
    ///      interceptors, HUD buttons), not just the prompt builder.
    ///   2. The value is stored in the SAVE (airog_mod_toggles.json next to my_save.txt), so one
    ///      save can run Settlement while another never sees it.
    ///
    /// Lookup order: this save's value, else the global default, else on. The global default is the
    /// old gen_context_config.json provider map, so whatever a player had unchecked before this
    /// change stays unchecked in every save until they decide otherwise for a particular one.
    ///
    /// A disabled mod must be INERT, not uninstalled: it still loads and saves its own data file,
    /// so switching it back on later resumes where it left off.
    /// </summary>
    public static class ModToggles
    {
        /// <summary>
        /// Switch names. These are the labels shown in the checklist and the keys in both json files,
        /// so they must never be renamed - an old name in a player's config would silently stop applying.
        /// </summary>
        public static class Keys
        {
            public const string ALife          = "A-Life";
            public const string Chronicle      = "Chronicle";
            public const string DmNotes        = "DM Notes";
            public const string GrandStrategy  = "Grand Strategy";
            public const string History        = "World History";
            public const string Insight        = "Insight";
            public const string Mythic         = "Mythic Director";
            public const string NPCExpansion   = "NPC Expansion";
            public const string Reverie        = "Reverie";
            public const string Settlement     = "Settlement Info";
            public const string SkillWeb       = "Skill Web Constellation";
            public const string SkillWebTraits = "Skill Web Traits";
            public const string WorldExpansion = "World State";
        }

        private const string SaveFileName = "airog_mod_toggles.json";

        private static readonly object _lock = new object();
        private static Dictionary<string, bool> _perSave = new Dictionary<string, bool>();
        private static Dictionary<string, bool> _defaults = new Dictionary<string, bool>();
        private static readonly List<string> _extraKeys = new List<string>();
        private static string _loadedDir;      // save dir _perSave was read from (null = no save)
        private static string _lastSub, _lastTop;
        private static bool _dirty;            // per-save values not yet on disk (save dir did not exist yet)
        private static bool _reloadPending;    // a different save's switches were read; Reloaded not raised for it yet

        /// <summary>Raised on the calling thread after a switch's effective value changes: (key, enabled).</summary>
        public static event Action<string, bool> Changed;

        /// <summary>
        /// Raised when a different save's switches were loaded, so HUD buttons can re-apply
        /// visibility. UI ONLY.
        ///
        /// It is raised on the main thread once the game has finished loading that save or starting
        /// that new game (see <see cref="RaiseReloadedIfPending"/>). It is NOT raised from inside the
        /// <see cref="IsEnabled"/> call that first notices the new save folder, as it once was: that
        /// call can come from any mod, on any thread, in the middle of the load, while every mod is
        /// still holding the previous save's data. <see cref="IsEnabled"/> itself already answers for
        /// the new save from that first call on, so a button injected during the load must apply its
        /// own visibility when it is created rather than wait for this event.
        ///
        /// Keep handlers to UI all the same. A handler that syncs, simulates or SAVES risks working
        /// on the previous save's in-memory state with the new save's folder as the target, i.e.
        /// overwriting the new save's data with the old save's. Do that work in the mod's own load
        /// hook instead.
        /// </summary>
        public static event Action Reloaded;

        /// <summary>Global defaults = the legacy provider map. Owned (loaded/saved) by ContextManager.</summary>
        internal static void BindDefaults(Dictionary<string, bool> defaults)
        {
            lock (_lock) _defaults = defaults ?? new Dictionary<string, bool>();
        }

        /// <summary>
        /// Lists a switch in the checklist for a mod that has no context provider of its own.
        /// Mods with a provider are listed automatically under the provider's name.
        /// </summary>
        public static void Register(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock) if (!_extraKeys.Contains(key)) _extraKeys.Add(key);
        }

        internal static List<string> ExtraKeys()
        {
            lock (_lock) return new List<string>(_extraKeys);
        }

        /// <summary>True unless this save (or, failing that, the global default) switched the mod off.</summary>
        public static bool IsEnabled(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            bool result;
            lock (_lock)
            {
                EnsureLoaded();
                if (!_perSave.TryGetValue(key, out result) && !_defaults.TryGetValue(key, out result))
                    result = true;
            }
            return result;
        }

        /// <summary>
        /// Raises <see cref="Reloaded"/> if a different save's switches have been read since it was
        /// last raised. GenContext calls this from the main thread when the game has finished a
        /// load / new game / multiplayer join (Patches.Patch_AfterLoadOrNewGame), and again from its
        /// save hook in case that first hook ever stops binding.
        /// </summary>
        internal static void RaiseReloadedIfPending()
        {
            bool raise;
            lock (_lock)
            {
                EnsureLoaded();
                raise = _reloadPending;
                _reloadPending = false;
            }
            if (raise) RaiseReloaded();
        }

        /// <summary>
        /// Switches a mod on or off for the current save. With no save loaded (main menu) the
        /// global default is changed instead.
        /// </summary>
        public static void SetEnabled(string key, bool enabled)
        {
            if (string.IsNullOrEmpty(key)) return;
            bool before = IsEnabled(key);
            lock (_lock)
            {
                EnsureLoaded();
                if (_loadedDir != null)
                {
                    _perSave[key] = enabled;
                    _dirty = true;
                    WritePerSave();
                }
                else
                {
                    _defaults[key] = enabled;
                    ContextManager.SaveConfigInternal();
                }
            }
            if (before != enabled) RaiseChanged(key, enabled);
        }

        /// <summary>Copies this save's explicit choices over the global defaults, for saves with no choices of their own.</summary>
        public static void SaveCurrentAsDefaults()
        {
            lock (_lock)
            {
                EnsureLoaded();
                foreach (var kv in _perSave) _defaults[kv.Key] = kv.Value;
                ContextManager.SaveConfigInternal();
            }
        }

        /// <summary>
        /// Writes switches that could not be written when they were set (a brand-new game has no
        /// save folder until its first save). Called from the game's save hook.
        /// </summary>
        public static void Flush()
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (_dirty) WritePerSave();
            }
        }

        // ---- internals -------------------------------------------------------------------------

        private static string CurrentSaveDir()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return null;
                return Path.Combine(SS.I.saveTopLvlDir ?? "", SS.I.saveSubDirAsArg);
            }
            catch { return null; }
        }

        /// <summary>
        /// Re-reads the per-save file when the active save changed, and notes that <see cref="Reloaded"/>
        /// is owed for it. Returns true when it did. Caller holds the lock.
        /// </summary>
        private static bool EnsureLoaded()
        {
            string sub = null, top = null;
            try { if (SS.I != null) { sub = SS.I.saveSubDirAsArg; top = SS.I.saveTopLvlDir; } } catch { }
            if (sub == _lastSub && top == _lastTop) return false;
            _lastSub = sub; _lastTop = top;

            string dir = CurrentSaveDir();
            if (dir == _loadedDir) return false;

            _loadedDir = dir;
            _perSave = new Dictionary<string, bool>();
            _dirty = false;
            _reloadPending = true;
            if (dir == null) return true;

            try
            {
                string path = Path.Combine(dir, SaveFileName);
                if (File.Exists(path))
                {
                    var read = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(path));
                    if (read != null) _perSave = read;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Could not read {SaveFileName}: {ex.Message}");
            }
            return true;
        }

        /// <summary>Caller holds the lock.</summary>
        private static void WritePerSave()
        {
            if (_loadedDir == null) return;
            try
            {
                if (!Directory.Exists(_loadedDir)) return; // new game, not saved yet - Flush() retries on the first save
                File.WriteAllText(Path.Combine(_loadedDir, SaveFileName), JsonConvert.SerializeObject(_perSave, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Could not write {SaveFileName}: {ex.Message}");
            }
        }

        private static void RaiseChanged(string key, bool enabled)
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (Action<string, bool> h in handlers.GetInvocationList())
            {
                try { h(key, enabled); }
                catch (Exception ex) { Debug.LogWarning($"[GenContext] A mod's toggle handler for '{key}' threw: {ex.Message}"); }
            }
        }

        private static void RaiseReloaded()
        {
            var handlers = Reloaded;
            if (handlers == null) return;
            foreach (Action h in handlers.GetInvocationList())
            {
                try { h(); }
                catch (Exception ex) { Debug.LogWarning($"[GenContext] A mod's toggle-reload handler threw: {ex.Message}"); }
            }
        }
    }
}
