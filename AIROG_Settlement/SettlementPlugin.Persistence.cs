using System;
using System.IO;
using Newtonsoft.Json;

namespace AIROG_Settlement
{
    // NOTE: Prompt injection intentionally lives in AIROG_GenContext's SettlementProvider,
    // which reads settlement_data.json (see gen_context_guidelines.md). Do not inject here.
    public partial class SettlementPlugin
    {
        // Save folder CurrentSettlement was loaded from, or first saved into (null = a fresh
        // state not tied to a save yet). The plugin object outlives scene changes, and saves can
        // come from an async continuation (image generation), so without this the settlement
        // could be written into whichever folder the game points at now - including a save that
        // is still loading, before our load hook has replaced the previous save's settlement.
        private string _stateDir;

        private static string NormalizeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            try { dir = Path.GetFullPath(Path.Combine(SS.I.saveTopLvlDir ?? "", dir)); } catch { }
            return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Every game scene starts from a blank settlement; a loaded save's own file is read
        /// right after by the ReadSaveFile hook. Without this a new game (which reads no save
        /// file) kept running - and then saved - the settlement of whatever was played before.
        /// </summary>
        public void ResetForNewScene()
        {
            CurrentSettlement = new SettlementState();
            _stateDir = null;
            PendingEvent = null;
            IsSettlementOpen = false; // the panel object itself went away with the old scene
        }

        public string GetSavePath(string subdir)
        {
            if (string.IsNullOrEmpty(subdir)) return null;
            return Path.Combine(SS.I.saveTopLvlDir, subdir, "settlement_data.json");
        }

        public void SaveSettlementData(bool quiet = false)
        {
            string subdir = SS.I?.saveSubDirAsArg;
            if (string.IsNullOrEmpty(subdir)) return;
            // In the main menu saveSubDirAsArg is the new-game staging folder, whose files the
            // game copies into the next new save.
            if (SS.I.hackyManager == null) return;

            try {
                string dir = NormalizeDir(subdir);
                if (_stateDir == null) _stateDir = dir;
                else if (!string.Equals(_stateDir, dir, StringComparison.OrdinalIgnoreCase))
                {
                    Log.LogWarning("Not saving: the settlement in memory belongs to another save.");
                    return;
                }

                string path = GetSavePath(subdir);
                string json = JsonConvert.SerializeObject(CurrentSettlement, Formatting.Indented);
                File.WriteAllText(path, json);
                if (!quiet) Log.LogInfo($"Settlement data saved to {subdir}");
            } catch (Exception e) {
                Log.LogError($"Failed to save settlement data: {e.Message}");
            }
        }

        public void LoadSettlementData(string subdir)
        {
            if (string.IsNullOrEmpty(subdir)) return;

            _stateDir = NormalizeDir(subdir);
            // A queued event belongs to the settlement it was rolled for
            PendingEvent = null;
            HideEventPopup();

            string path = GetSavePath(subdir);
            if (File.Exists(path)) {
                try {
                    string json = File.ReadAllText(path);
                    // An empty file (crash mid-write) deserializes to null
                    CurrentSettlement = JsonConvert.DeserializeObject<SettlementState>(json) ?? new SettlementState();
                    Log.LogInfo($"Settlement data loaded from {subdir}");
                } catch (Exception e) {
                    Log.LogError($"Failed to load settlement data: {e.Message}");
                    CurrentSettlement = new SettlementState();
                }
            } else {
                CurrentSettlement = new SettlementState();
            }
        }
    }
}
