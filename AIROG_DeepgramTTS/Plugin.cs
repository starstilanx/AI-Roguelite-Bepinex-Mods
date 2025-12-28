using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AIROG_DeepgramTTS
{
    [BepInPlugin("com.airoguelite.deepgramtts", "Deepgram TTS", "1.0.0")]
    public class DeepgramTtsPlugin : BaseUnityPlugin
    {
        public static DeepgramTtsPlugin Instance { get; private set; }
        public static ConfigEntry<bool> UseDeepgramTts { get; private set; }
        public static ConfigEntry<string> DeepgramApiKey { get; private set; }
        public static ConfigEntry<string> VoiceNarration { get; private set; }
        public static ConfigEntry<string> VoiceMale { get; private set; }
        public static ConfigEntry<string> VoiceFemale { get; private set; }
        public static ConfigEntry<string> VoiceMonster { get; private set; }
        public static ConfigEntry<string> VoiceRobot { get; private set; }
        public static ConfigEntry<string> VoiceEnemy { get; private set; }

        private void Awake()
        {
            Instance = this;
            UseDeepgramTts = Config.Bind("General", "UseDeepgramTts", false, "Whether to use Deepgram Aura TTS");
            DeepgramApiKey = Config.Bind("General", "DeepgramApiKey", "", "Your Deepgram API Key");
            
            // Featured Aura-2 English Voices
            VoiceNarration = Config.Bind("Voices", "Narration", "aura-2-thalia-en", "Deepgram voice for Narration");
            VoiceMale = Config.Bind("Voices", "Male", "aura-2-apollo-en", "Deepgram voice for Male characters");
            VoiceFemale = Config.Bind("Voices", "Female", "aura-2-helena-en", "Deepgram voice for Female characters");
            VoiceMonster = Config.Bind("Voices", "Monster", "aura-2-arcas-en", "Deepgram voice for Monsters");
            VoiceRobot = Config.Bind("Voices", "Robot", "aura-2-aries-en", "Deepgram voice for Robots");
            VoiceEnemy = Config.Bind("Voices", "Enemy", "aura-2-andromeda-en", "Deepgram voice for Enemies");

            Logger.LogInfo("Deepgram TTS Plugin Loaded!");

            Harmony harmony = new Harmony("com.airoguelite.deepgramtts");
            harmony.PatchAll();
        }
    }
}
