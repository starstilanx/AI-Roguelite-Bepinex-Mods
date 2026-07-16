using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Core
{
    /// <summary>
    /// Centralizes the "combine SS.I.saveTopLvlDir + saveSubDirAsArg" idiom that every
    /// mod's save/load code repeats, plus small JSON read/write helpers on top of it.
    /// </summary>
    public static class ModSaveFile
    {
        /// <summary>The active save's directory, or null if there is no active save.</summary>
        public static string Dir()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return null;
            return System.IO.Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
        }

        /// <summary>Full path to a file inside the active save's directory, or null.</summary>
        public static string Path(string fileName)
        {
            string dir = Dir();
            return dir == null ? null : System.IO.Path.Combine(dir, fileName);
        }

        public static bool Exists(string fileName)
        {
            string path = Path(fileName);
            return path != null && File.Exists(path);
        }

        public static T LoadJson<T>(string fileName) where T : class
        {
            string path = Path(fileName);
            if (path == null || !File.Exists(path)) return null;

            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModSaveFile] Failed to load {fileName}: {ex.Message}");
                return null;
            }
        }

        public static bool SaveJson<T>(string fileName, T obj)
        {
            string path = Path(fileName);
            if (path == null) return false;

            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(obj));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModSaveFile] Failed to save {fileName}: {ex.Message}");
                return false;
            }
        }
    }
}
