using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AIROG_GeminiTTS
{
    [BepInPlugin("com.airoguelite.geminitts", "Gemini TTS", "1.1.0")]
    public class GeminiTtsPlugin : BaseUnityPlugin
    {
        public static GeminiTtsPlugin Instance { get; private set; }
        public static ConfigEntry<bool> UseGeminiTts { get; private set; }
        public static ConfigEntry<string> GeminiApiKey { get; private set; }
        public static ConfigEntry<string> VoiceNarration { get; private set; }
        public static ConfigEntry<string> VoiceMale { get; private set; }
        public static ConfigEntry<string> VoiceFemale { get; private set; }
        public static ConfigEntry<string> VoiceMonster { get; private set; }
        public static ConfigEntry<string> VoiceRobot { get; private set; }
        public static ConfigEntry<string> VoiceEnemy { get; private set; }
        public static ConfigEntry<float> SpeakingRate { get; private set; }

        private void Awake()
        {
            Instance = this;
            UseGeminiTts = Config.Bind("General", "UseGeminiTts", true, "Whether to use Gemini TTS instead of TikTok TTS");
            GeminiApiKey = Config.Bind("General", "GeminiApiKey", "", "Your Google Gemini API Key");
            
            VoiceNarration = Config.Bind("Voices", "Narration", "Charon", "Gemini voice for Narration");
            VoiceMale = Config.Bind("Voices", "Male", "Kore", "Gemini voice for Male characters");
            VoiceFemale = Config.Bind("Voices", "Female", "Aoede", "Gemini voice for Female characters");
            VoiceMonster = Config.Bind("Voices", "Monster", "Fenrir", "Gemini voice for Monsters");
            VoiceRobot = Config.Bind("Voices", "Robot", "Puck", "Gemini voice for Robots");
            VoiceEnemy = Config.Bind("Voices", "Enemy", "Fenrir", "Gemini voice for Enemies");
            
            SpeakingRate = Config.Bind("Settings", "SpeakingRate", 1.0f, "Speaking rate (0.25 to 4.0)");

            Logger.LogInfo("Gemini TTS Plugin Loaded!");

            Harmony harmony = new Harmony("com.airoguelite.geminitts");
            harmony.PatchAll();
        }
    }
}
