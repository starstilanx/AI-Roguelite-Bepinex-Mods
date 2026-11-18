using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SFB;

namespace AIROG_TavernImport
{
    public enum CardImportMode
    {
        /// <summary>Card text is mapped onto the profile fields as-is. No AI call.</summary>
        Verbatim,
        /// <summary>The card is handed to the game's own "Generate Profile" call, which rewrites it
        /// in the game's style and length. Falls back to Verbatim if the call fails.</summary>
        AiAdapted,
    }

    /// <summary>
    /// Everything that writes imported data into live game state. The native import path, the
    /// lorebook buttons and the console commands all route through here.
    /// </summary>
    public static class GameImporter
    {
        // ---- File pickers -----------------------------------------------------------------

        public static string PickCardOrZip() => Pick("Select a Character (card PNG/JSON or AI Roguelite export)",
            new ExtensionFilter("Characters", "png", "json", "zip", "7z"));

        public static string PickCard() => Pick("Select a Character Card",
            new ExtensionFilter("Character Cards", "png", "json"));

        public static string PickBook() => Pick("Select a Lorebook (or a card with an embedded lorebook)",
            new ExtensionFilter("Lorebooks and Cards", "json", "png"));

        static string Pick(string title, ExtensionFilter filter)
        {
            string[] r = StandaloneFileBrowser.OpenFilePanel(title, "",
                new[] { filter, new ExtensionFilter("All Files", "*") }, false);
            return r != null && r.Length > 0 && !string.IsNullOrEmpty(r[0]) ? r[0] : null;
        }

        public static bool IsCardFile(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext == ".png" || ext == ".json";
        }

        // ---- Lorebooks --------------------------------------------------------------------

        /// <summary>Imports into the live universe lorebook. Returns a summary for display.</summary>
        public static string ImportBookInGame(GameplayManager gm, TavernBook book, string charName = null)
        {
            UniverseInfo univ = gm.GetCurrentUniverse();
            if (univ == null) return "No active universe to import into.";
            if (univ.lorebook == null) univ.lorebook = new Lorebook();

            string extra = gm.scenarioState.playerSpecifiedLore ?? "";
            var r = LoreMerger.Merge(univ.lorebook.loreEntries, ref extra, book, charName, PlayerName(gm));
            gm.scenarioState.playerSpecifiedLore = extra;
            ImportLedger.Record(book.Name, r.AddedVals);
            gm.CommitEntityEdit(univ, gm.scenarioState);
            return r.Summary() + CapNote();
        }

        /// <summary>Imports into the new-game screen's staged lorebook and Extra Lore field. Works
        /// on the UI directly so it behaves the same whether or not a scenario is loaded yet.</summary>
        public static string ImportBookNewGame(MainMenu menu, TavernBook book, string charName = null)
        {
            Lorebook staged = menu.ngLorebook.ToLorebook();
            string extra = menu.ngWorldGeneral.extraLoreTextInput.text ?? "";
            var r = LoreMerger.Merge(staged.loreEntries, ref extra, book, charName, menu.PlayerCh?.GetPrettyName());
            menu.ngLorebook.PopulateLorebook(staged);
            menu.ngWorldGeneral.extraLoreTextInput.SetTextWithoutNotify(extra);
            menu.OnPromptPresetChanged(); // marks the preset edited and syncs UI into the scenario
            return r.Summary() + CapNote();
        }

        static string CapNote()
        {
            int cap = Utils.GetMaxTriggeredLorebookEntries();
            return $"\n\nThe game injects at most {cap} matched lorebook entr{(cap == 1 ? "y" : "ies")} per prompt, "
                 + "picking whichever keys appeared most recently in the story (Settings: \"Max triggered lorebook entries\").";
        }

        // ---- Cards: NPCs ------------------------------------------------------------------

        /// <summary>Builds (but does not place) an important NPC from a card. The native import
        /// path places it via EntitySpawner.PlaceImportedCharacter.</summary>
        public static async Task<GameCharacter> BuildNpc(GameplayManager gm, TavernCard card, List<string> notes)
        {
            string playerName = PlayerName(gm);
            MappedProfile p = CardMapper.Map(card, playerName);
            string name = string.IsNullOrWhiteSpace(card.Name) ? "Stranger" : card.Name.Trim();

            var ch = new GameCharacter(gm.currentPlace, name, p.Visual, gm, GameCharacter.CharacterType.NPC);
            ApplyPortrait(ch, card, notes);

            bool adapted = false;
            if (TavernImportPlugin.CardMode.Value == CardImportMode.AiAdapted)
            {
                string inst = "Build this character's profile from the character card below. Keep their identity, "
                    + "history and personality faithful to the card, but write it to fit this world and the game's usual style.\n\n"
                    + CardMapper.ToPromptText(card, playerName);
                await Utils.DoTaskWithLoadingScreen(async delegate
                {
                    try { await ch.GenerateImportantData(inst); adapted = ch.importantData != null; }
                    catch (Exception ex) { TavernImportPlugin.LogWarn($"AI-adapted profile failed for {name}: {ex.Message}"); }
                });
                if (!adapted) notes.Add("AI adaptation failed; used the card text verbatim.");
            }
            if (!adapted)
            {
                ch.importantData = new ImportantCharacterData(gm)
                {
                    visualDescription = p.Visual,
                    personality = p.Personality,
                    background = p.Background,
                };
            }

            var ge = ch.AsNonCpnGe();
            ge.strKeys = TriggerKeys(name);
            if (TavernImportPlugin.LockImportedNpcs.Value) ge.lockNameAndDesc = true;

            if (card.Book != null && card.Book.Entries.Count > 0 && TavernImportPlugin.ImportEmbeddedLorebook.Value)
                notes.Add(ImportBookInGame(gm, card.Book, name));
            return ch;
        }

        /// <summary>Console path: build and place the NPC in the player's current location.</summary>
        public static async Task<string> SpawnNpcHere(GameplayManager gm, string path)
        {
            var card = TavernReader.ReadCard(path);
            var notes = new List<string>();
            var ch = await BuildNpc(gm, card, notes);
            EntitySpawner.PlaceImportedCharacter(gm, gm.currentPlace, ch, null);
            return Report($"{ch.GetPrettyName()} has arrived.", notes);
        }

        // ---- Cards: player ----------------------------------------------------------------

        /// <summary>Writes a card onto an existing player character in place, which keeps the
        /// character's class, abilities, perks and inventory.</summary>
        static void ApplyToPlayer(MmCtxGetter mm, GameCharacter pc, TavernCard card, List<string> notes)
        {
            string name = string.IsNullOrWhiteSpace(card.Name) ? pc.GetPrettyName() : card.Name.Trim();
            // {{user}} would be the player themself here, so it reads as the character's own name.
            MappedProfile p = CardMapper.Map(card, name);
            if (TavernImportPlugin.ImportPlayerName.Value) pc.SetName(name);
            if (pc.importantData == null) pc.importantData = new ImportantCharacterData(mm);
            pc.importantData.visualDescription = p.Visual;
            pc.importantData.personality = p.Personality;
            pc.importantData.background = p.Background;
            ApplyPortrait(pc, card, notes);
        }

        public static string ImportPlayerInGame(GameplayManager gm, string path)
        {
            var card = TavernReader.ReadCard(path);
            GameCharacter pc = gm.playerCharacter?.GetPcGameCharacter();
            if (pc == null) return "No player character found.";
            var notes = new List<string>();
            ApplyToPlayer(gm, pc, card, notes);
            gm.CommitEntityEdit(pc, pc.importantData);
            if (card.Book != null && card.Book.Entries.Count > 0 && TavernImportPlugin.ImportEmbeddedLorebook.Value)
                notes.Add(ImportBookInGame(gm, card.Book, card.Name));
            return Report($"You are now {pc.GetPrettyName()}.", notes);
        }

        public static string ImportPlayerNewGame(MainMenu menu, TavernCard card)
        {
            GameCharacter pc = menu.PlayerCh;
            if (pc == null) return "Load or create a scenario first, then import the character.";
            var notes = new List<string>();
            menu.SyncUiToEntities();        // keep any unsaved edits elsewhere on the screen
            ApplyToPlayer(menu, pc, card, notes);
            menu.PopulateUiFromEntities();
            if (card.Book != null && card.Book.Entries.Count > 0 && TavernImportPlugin.ImportEmbeddedLorebook.Value)
                notes.Add(ImportBookNewGame(menu, card.Book, card.Name));
            else
                menu.OnPromptPresetChanged();
            return Report($"Player set to {pc.GetPrettyName()}.", notes);
        }

        // ---- Shared -----------------------------------------------------------------------

        static void ApplyPortrait(GameCharacter ch, TavernCard card, List<string> notes)
        {
            if (card.ImageBytes == null || !TavernImportPlugin.UseCardArtAsPortrait.Value) return;
            try
            {
                AssetGenService.I.ApplyReplacementImage(ch, card.ImageBytes, sprite: false);
                // The ctor already queued an AI portrait; FINISHED makes GenerateImage bail out.
                ch.imgGenInfo.imgGenState = GameEntity.ImgGenState.FINISHED;
                ch.imgGenInfo.imgGenProgressAmount = 1f;
            }
            catch (Exception ex)
            {
                TavernImportPlugin.LogWarn($"Could not apply card art for {card.Name}: {ex}");
                notes.Add("Card art could not be applied; the game will generate a portrait instead.");
            }
        }

        /// <summary>The full name, plus the first name when the card name has several words, so
        /// "Seraphina Vale" is recalled when the story just says "Seraphina".</summary>
        static List<string> TriggerKeys(string name)
        {
            var keys = new List<string> { name };
            string first = name.Split(' ')[0].Trim(',', '.');
            if (first.Length >= 3 && first != name) keys.Add(first);
            return keys;
        }

        static string PlayerName(MmCtxGetter mm) => mm?.GetPlayerCh()?.GetPrettyName();

        static string Report(string headline, List<string> notes)
        {
            var sb = new StringBuilder(headline);
            foreach (var n in notes.Where(n => !string.IsNullOrWhiteSpace(n))) sb.Append("\n\n").Append(n);
            return sb.ToString();
        }

        public static void Show(string msg) => MessageModal.I.ShowModal(msg, isBig: msg.Length > 300, justified: true);
    }
}
