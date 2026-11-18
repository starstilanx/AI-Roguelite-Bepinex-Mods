using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIROG_TavernImport
{
    /// <summary>
    /// Console commands:
    ///   TAVERN                 - help
    ///   TAVERN LORE            - import a lorebook (or a card's embedded lorebook)
    ///   TAVERN CARD            - import a card as an NPC at your current location
    ///   TAVERN PLAYER          - import a card onto your player character
    ///   TAVERN LIST            - imported lorebooks in this save
    ///   TAVERN REMOVE &lt;name&gt;   - remove an imported lorebook's entries and always-on block
    /// </summary>
    public static class ConsoleCommands
    {
        const string Help =
            "Tavern Import commands:\n"
            + "TAVERN LORE - import a SillyTavern lorebook (or a card's embedded lorebook)\n"
            + "TAVERN CARD - import a character card as an NPC here\n"
            + "TAVERN PLAYER - import a character card onto your player character\n"
            + "TAVERN LIST - list lorebooks imported into this save\n"
            + "TAVERN REMOVE <name> - remove an imported lorebook\n\n"
            + "Cards also work through the spawn menu's \"Import Character\" (as an NPC) "
            + "and the new-game screen's \"Import Character\" (as the player).";

        public static bool Prefix(string txt, GameplayManager __instance)
        {
            string raw = (txt ?? "").Trim();
            string upper = raw.ToUpperInvariant();
            if (upper != "TAVERN" && !upper.StartsWith("TAVERN ")) return true;
            if (!TavernImportPlugin.Enabled.Value) return true;

            if (upper == "TAVERN") { GameImporter.Show(Help); return false; }
            if (MpSession.IsClient) { GameImporter.Show("Only the host can use Tavern Import commands."); return false; }

            try
            {
                string arg = raw.Substring("TAVERN".Length).Trim();
                string sub = arg.Split(' ')[0].ToUpperInvariant();
                string rest = arg.Substring(sub.Length).Trim();
                switch (sub)
                {
                    case "LORE": Lore(__instance); break;
                    case "CARD": _ = Card(__instance); break;
                    case "PLAYER": Player(__instance); break;
                    case "LIST": List(__instance); break;
                    case "REMOVE": Remove(__instance, rest); break;
                    default: GameImporter.Show(Help); break;
                }
            }
            catch (Exception ex)
            {
                TavernImportPlugin.LogWarn($"Console '{raw}' failed: {ex}");
                GameImporter.Show($"Tavern Import failed:\n{ex.Message}");
            }
            return false;
        }

        static void Lore(GameplayManager gm)
        {
            string path = GameImporter.PickBook();
            if (path == null) return;
            GameImporter.Show(GameImporter.ImportBookInGame(gm, TavernReader.ReadBook(path)));
        }

        static async Task Card(GameplayManager gm)
        {
            string path = GameImporter.PickCard();
            if (path == null) return;
            try { GameImporter.Show(await GameImporter.SpawnNpcHere(gm, path)); }
            catch (Exception ex)
            {
                TavernImportPlugin.LogWarn($"TAVERN CARD failed for {path}: {ex}");
                GameImporter.Show($"Couldn't import that character card:\n{ex.Message}");
            }
        }

        static void Player(GameplayManager gm)
        {
            string path = GameImporter.PickCard();
            if (path == null) return;
            GameImporter.Show(GameImporter.ImportPlayerInGame(gm, path));
        }

        static void List(GameplayManager gm)
        {
            var sb = new StringBuilder();
            var tracked = ImportLedger.Books().ToList();
            var blocks = ImportLedger.BlocksIn(gm.scenarioState?.playerSpecifiedLore).ToList();
            foreach (var b in tracked.Union(blocks, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("- ").Append(b).Append(": ").Append(ImportLedger.Count(b)).Append(" keyed entries");
                if (blocks.Contains(b, StringComparer.OrdinalIgnoreCase)) sb.Append(" + always-on block");
                sb.Append('\n');
            }
            GameImporter.Show(sb.Length == 0 ? "No lorebooks imported into this save." : "Imported lorebooks:\n" + sb);
        }

        static void Remove(GameplayManager gm, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { GameImporter.Show("Usage: TAVERN REMOVE <lorebook name>  (see TAVERN LIST)"); return; }
            UniverseInfo univ = gm.GetCurrentUniverse();
            string lore = gm.scenarioState.playerSpecifiedLore ?? "";

            string tracked = ImportLedger.Resolve(name);
            string block = ImportLedger.BlocksIn(lore).FirstOrDefault(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));
            if (tracked == null && block == null) { GameImporter.Show($"No imported lorebook named '{name}'. See TAVERN LIST."); return; }

            int removed = tracked != null && univ?.lorebook != null ? ImportLedger.RemoveFrom(univ.lorebook, tracked) : 0;
            if (block != null) gm.scenarioState.playerSpecifiedLore = LoreMerger.RemoveBlock(lore, block);
            gm.CommitEntityEdit(univ, gm.scenarioState);
            GameImporter.Show($"Removed '{tracked ?? block}': {removed} lorebook entr{(removed == 1 ? "y" : "ies")}"
                + (block != null ? " and its always-on block." : "."));
        }
    }
}
