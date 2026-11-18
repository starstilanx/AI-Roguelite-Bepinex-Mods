using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIROG_Core;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AIROG_TavernImport
{
    /// <summary>
    /// Imports SillyTavern lorebooks and Character Card V1/V2/V3 files into AI Roguelite's
    /// native systems (no parallel prompt layer):
    ///   - Lorebook entries become native keyword lore entries; always-on entries go to Extra Lore.
    ///   - A card imported via the spawn menu's "Import Character" becomes an important NPC with
    ///     personality/background filled from the card and the card art as the portrait.
    ///   - A card imported via the new-game "Import Character" becomes the player character.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class TavernImportPlugin : BaseModPlugin
    {
        public const string PLUGIN_GUID = "com.airog.tavernimport";
        public const string PLUGIN_NAME = "Tavern Import";
        public const string PLUGIN_VERSION = "1.0.0";

        public static TavernImportPlugin Instance { get; private set; }

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<CardImportMode> CardMode;
        public static ConfigEntry<bool> UseCardArtAsPortrait;
        public static ConfigEntry<bool> LockImportedNpcs;
        public static ConfigEntry<bool> ImportEmbeddedLorebook;
        public static ConfigEntry<bool> ImportPlayerName;
        public static ConfigEntry<bool> IncludeExampleDialogue;
        public static ConfigEntry<int> MaxVisualChars;
        public static ConfigEntry<int> MaxBackgroundChars;
        public static ConfigEntry<int> MaxExampleChars;
        public static ConfigEntry<ConstantEntryMode> ConstantEntries;
        public static ConfigEntry<bool> SkipDisabledEntries;
        public static ConfigEntry<int> MaxConstantChars;

        protected override void Awake()
        {
            base.Awake();
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true, "Master switch.");

            CardMode = Config.Bind("Cards", "ImportMode", CardImportMode.Verbatim,
                "Verbatim: map the card's text straight onto the NPC profile (no AI call).\n"
                + "AiAdapted: hand the card to the game's own Generate Profile call so it is rewritten to fit the world. NPCs only.");
            UseCardArtAsPortrait = Config.Bind("Cards", "UseCardArtAsPortrait", true,
                "Use a PNG card's image as the character's portrait instead of generating one.");
            LockImportedNpcs = Config.Bind("Cards", "LockImportedNpcs", true,
                "Lock imported NPCs' name and description so later AI state updates don't rewrite the card.");
            ImportEmbeddedLorebook = Config.Bind("Cards", "ImportEmbeddedLorebook", true,
                "Also import a card's embedded character_book into the lorebook.");
            ImportPlayerName = Config.Bind("Cards", "ImportPlayerName", true,
                "When a card is imported as the player, rename the player to the card's name.");
            IncludeExampleDialogue = Config.Bind("Cards", "IncludeExampleDialogue", true,
                "Append the card's example dialogue to the personality field as a speech-style sample.");
            MaxVisualChars = Config.Bind("Cards", "MaxVisualChars", 600,
                "Cap on the visual description (also used as the image-generation prompt).");
            MaxBackgroundChars = Config.Bind("Cards", "MaxBackgroundChars", 2500,
                "Cap on the background field. The game sends it with every prompt the NPC is part of.");
            MaxExampleChars = Config.Bind("Cards", "MaxExampleChars", 800, "Cap on the example-dialogue sample.");

            ConstantEntries = Config.Bind("Lorebooks", "ConstantEntries", ConstantEntryMode.ExtraLore,
                "Where ST 'constant' (always-on) entries go. ExtraLore: the scenario's Extra Lore, sent with every story prompt. "
                + "Keyed: import like normal entries (dropped if they have no keys). Skip: ignore them.");
            SkipDisabledEntries = Config.Bind("Lorebooks", "SkipDisabledEntries", true, "Skip entries disabled in the source lorebook.");
            MaxConstantChars = Config.Bind("Lorebooks", "MaxConstantChars", 4000,
                "Cap on the always-on text one lorebook may add to Extra Lore (0 = no cap).");

            SafePatch(typeof(CharacterIO), nameof(CharacterIO.ImportCharacterWithDialog),
                prefix: new HarmonyMethod(typeof(TavernImportPlugin), nameof(ImportCharacterWithDialog_Prefix)));
            SafePatch(typeof(LorebookView), nameof(LorebookView.Redraw),
                postfix: new HarmonyMethod(typeof(ImportButtons), nameof(ImportButtons.LorebookView_Redraw_Postfix)));
            SafePatch(typeof(NgLorebook), nameof(NgLorebook.UpdateDisplay),
                postfix: new HarmonyMethod(typeof(ImportButtons), nameof(ImportButtons.NgLorebook_UpdateDisplay_Postfix)));
            SafePatch(typeof(GameplayManager), "ProcessConsoleCommand",
                prefix: new HarmonyMethod(typeof(ConsoleCommands), nameof(ConsoleCommands.Prefix)));

            Logger.LogInfo($"{PLUGIN_NAME} {PLUGIN_VERSION} loaded.");
        }

        internal static void LogInfo(string msg) => Instance?.Logger.LogInfo(msg);
        internal static void LogWarn(string msg) => Instance?.Logger.LogWarning(msg);

        /// <summary>
        /// CharacterIO.ImportCharacterWithDialog backs both "Import Character" buttons: the spawn
        /// menu (mm = GameplayManager, the result is placed as an NPC) and the new-game player
        /// panel (mm = MainMenu, the result is adopted as the player). Replacing its picker with
        /// one that also accepts cards gives both paths card support with no new UI. Native
        /// .zip/.7z exports still go through the game's own loader.
        /// </summary>
        static bool ImportCharacterWithDialog_Prefix(MmCtxGetter mm, ref Task<GameCharacter> __result)
        {
            if (!Enabled.Value) return true;
            __result = ImportCharacter(mm);
            return false;
        }

        static async Task<GameCharacter> ImportCharacter(MmCtxGetter mm)
        {
            string path = GameImporter.PickCardOrZip();
            if (path == null) return null;
            if (!GameImporter.IsCardFile(path)) return await ImportNativeExport(path, mm);

            try
            {
                var card = TavernReader.ReadCard(path);
                if (mm is MainMenu menu)
                {
                    // Written onto the staged player in place; returning null skips the native
                    // adopt step, which would replace the character (and its class/abilities).
                    GameImporter.Show(GameImporter.ImportPlayerNewGame(menu, card));
                    return null;
                }
                if (mm is GameplayManager gm)
                {
                    var notes = new List<string>();
                    var ch = await GameImporter.BuildNpc(gm, card, notes);
                    if (notes.Count > 0) GameImporter.Show($"Imported {ch.GetPrettyName()}.\n\n" + string.Join("\n\n", notes));
                    return ch;
                }
                return null;
            }
            catch (Exception ex)
            {
                LogWarn($"Card import failed for {path}: {ex}");
                GameImporter.Show($"Couldn't import that character card:\n{ex.Message}");
                return null;
            }
        }

        /// <summary>The native zip path, minus its own file picker.</summary>
        static async Task<GameCharacter> ImportNativeExport(string path, MmCtxGetter mm)
        {
            string tempDir = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            try
            {
                using (var cts = new CancellationTokenSource())
                    await Utils.ExtractWith7ZipLong(path, tempDir, cts);
                if (Directory.GetFiles(tempDir, "character_export.txt", SearchOption.AllDirectories).Length == 0)
                {
                    MessageModal.I.ShowModal("The selected file doesn't contain an exported character.");
                    return null;
                }
                return CharacterIO.ImportCharacterFromFolder(tempDir, mm);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
