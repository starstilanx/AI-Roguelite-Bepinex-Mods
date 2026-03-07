using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using System;

namespace AIROG_Sapi5
{
    [BepInPlugin("com.airoguelite.sapi5", "SAPI5 TTS", "1.0.0")]
    public class Sapi5Plugin : BaseUnityPlugin
    {
        public static Sapi5Plugin Instance { get; private set; }
        public static ConfigEntry<bool> UseSapi5 { get; private set; }
        public static ConfigEntry<string> VoiceNarration { get; private set; }
        public static ConfigEntry<string> VoiceMale { get; private set; }
        public static ConfigEntry<string> VoiceFemale { get; private set; }
        public static ConfigEntry<string> VoiceMonster { get; private set; }
        public static ConfigEntry<string> VoiceRobot { get; private set; }
        public static ConfigEntry<string> VoiceEnemy { get; private set; }
        
        public static ConfigEntry<int> Rate { get; private set; }
        public static ConfigEntry<int> Volume { get; private set; }

        private void Awake()
        {
            try
            {
                Logger.LogInfo("[SAPI5] Awake() starting...");
                Instance = this;
                
                Logger.LogInfo("[SAPI5] Binding config entries...");
                UseSapi5 = Config.Bind("General", "UseSapi5", false, "Whether to use SAPI5 TTS");
                
                VoiceNarration = Config.Bind("Voices", "Narration", "Microsoft David Desktop", "SAPI5 voice for Narration");
                VoiceMale = Config.Bind("Voices", "Male", "Microsoft David Desktop", "SAPI5 voice for Male characters");
                VoiceFemale = Config.Bind("Voices", "Female", "Microsoft Zira Desktop", "SAPI5 voice for Female characters");
                VoiceMonster = Config.Bind("Voices", "Monster", "Microsoft David Desktop", "SAPI5 voice for Monsters");
                VoiceRobot = Config.Bind("Voices", "Robot", "Microsoft David Desktop", "SAPI5 voice for Robots");
                VoiceEnemy = Config.Bind("Voices", "Enemy", "Microsoft David Desktop", "SAPI5 voice for Enemies");

                Rate = Config.Bind("Audio", "Rate", 0, "Speech rate (-10 to 10)");
                Volume = Config.Bind("Audio", "Volume", 100, "Volume (0 to 100)");

                Logger.LogInfo("[SAPI5] Config bound. Applying Harmony patches...");
                
                Harmony harmony = new Harmony("com.airoguelite.sapi5");
                harmony.PatchAll();

                Logger.LogInfo("[SAPI5] Harmony patches applied. Testing SAPI5 availability...");

                // SpeechSynthesizer is COM-based and requires an STA thread.
                // Run voice listing on a dedicated STA thread to avoid NullReferenceException.
                var voiceListThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
                        {
                            var voices = synth.GetInstalledVoices();
                            if (voices.Count == 0)
                            {
                                Logger.LogWarning("[SAPI5] No SAPI5 voices found on this system.");
                            }
                            foreach (var voice in voices)
                            {
                                Logger.LogInfo($"[SAPI5] Available Voice: {voice.VoiceInfo.Name}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"[SAPI5] Could not list SAPI5 voices (non-fatal): {e.Message}");
                    }
                });
                voiceListThread.SetApartmentState(System.Threading.ApartmentState.STA);
                voiceListThread.IsBackground = true;
                voiceListThread.Start();

                Logger.LogInfo("[SAPI5] SAPI5 TTS Plugin Loaded Successfully!");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SAPI5] FATAL ERROR in Awake(): {ex}");
            }
        }
    }
}
