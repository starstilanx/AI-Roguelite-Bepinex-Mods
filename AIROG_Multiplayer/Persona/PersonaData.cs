using System;

namespace AIROG_Multiplayer.Persona
{
    [Serializable]
    public class PersonaData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string PersonaName { get; set; } = "New Persona";
        public string CharacterName { get; set; } = "";
        public string CharacterClass { get; set; } = "";
        public string Age { get; set; } = "";
        public string Build { get; set; } = "";
        public string Description { get; set; } = "";
        public string Background { get; set; } = "";
        public string Personality { get; set; } = "";
        public string PhysicalAppearance { get; set; } = "";
        public long DefaultHp { get; set; } = 100;
        public long DefaultMaxHp { get; set; } = 100;
        public int DefaultLevel { get; set; } = 1;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
