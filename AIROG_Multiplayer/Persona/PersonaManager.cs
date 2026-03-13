using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace AIROG_Multiplayer.Persona
{
    public static class PersonaManager
    {
        private static readonly string SaveDir =
            Path.Combine(Paths.ConfigPath, "AIROG_Personas");
        private static readonly string ListFile =
            Path.Combine(SaveDir, "personas.json");

        private static List<PersonaData> _personas = new List<PersonaData>();

        public static IReadOnlyList<PersonaData> All => _personas;

        public static void Load()
        {
            try
            {
                Directory.CreateDirectory(SaveDir);
                if (!File.Exists(ListFile)) return;
                string json = File.ReadAllText(ListFile);
                _personas = JsonConvert.DeserializeObject<List<PersonaData>>(json)
                            ?? new List<PersonaData>();
            }
            catch (Exception ex)
            {
                BepInEx.Logging.Logger.CreateLogSource("PersonaManager").LogError($"Load failed: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(SaveDir);
                File.WriteAllText(ListFile, JsonConvert.SerializeObject(_personas, Formatting.Indented));
            }
            catch (Exception ex)
            {
                BepInEx.Logging.Logger.CreateLogSource("PersonaManager").LogError($"Save failed: {ex.Message}");
            }
        }

        public static void AddOrUpdate(PersonaData persona)
        {
            persona.UpdatedAt = DateTime.UtcNow;
            int idx = _personas.FindIndex(p => p.Id == persona.Id);
            if (idx >= 0) _personas[idx] = persona;
            else _personas.Add(persona);
            Save();
        }

        public static void Delete(string id)
        {
            _personas.RemoveAll(p => p.Id == id);
            Save();
        }

        /// <summary>Writes a single persona to BepInEx/config/AIROG_Personas/{name}.persona.json</summary>
        /// <returns>The full path written, or null on failure.</returns>
        public static string Export(PersonaData persona)
        {
            try
            {
                Directory.CreateDirectory(SaveDir);
                string safeName = string.Concat(persona.PersonaName.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safeName)) safeName = persona.Id;
                string path = Path.Combine(SaveDir, safeName + ".persona.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(persona, Formatting.Indented));
                return path;
            }
            catch (Exception ex)
            {
                BepInEx.Logging.Logger.CreateLogSource("PersonaManager").LogError($"Export failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Parses a JSON string as a PersonaData. Returns null on failure.</summary>
        public static PersonaData ImportFromJson(string json)
        {
            try
            {
                var persona = JsonConvert.DeserializeObject<PersonaData>(json);
                if (persona == null) return null;
                // Always assign a fresh Id to avoid collision
                persona.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                persona.UpdatedAt = DateTime.UtcNow;
                return persona;
            }
            catch
            {
                return null;
            }
        }
    }
}
