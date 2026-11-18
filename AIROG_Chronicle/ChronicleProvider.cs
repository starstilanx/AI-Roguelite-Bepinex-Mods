using System;
using AIROG_GenContext;

namespace AIROG_Chronicle
{
    public class ChronicleProvider : IContextProvider
    {
        public string Name        => "Chronicle";
        public int    Priority    => 88;  // above WorldContext(80)/SkillWeb(85), below Settlement(90)/DmNotes(95)/NPC(200)
        public string Description => "Injects a compact narrative timeline so the AI remembers story arcs from earlier in the session, keeping context lean.";

        // What goes in, and why it cannot grow with the length of the save, is laid out on
        // ChronicleComposer: a fixed number of recent chapter recaps, a fixed number of act
        // summaries for everything older, at most a couple of tag-recalled chapters, the last
        // beats of the open chapter and the hidden-beat instruction.
        public string GetContext(string prompt, int maxTokens)
        {
            try
            {
                var state = ChronicleManager.State;
                if (state == null || maxTokens <= 0 || !ModSwitch.Enabled) return "";

                ChronicleManager.EnsureDerived();

                var settings = ChroniclePlugin.Settings();
                int maxChars = Math.Min(maxTokens, Math.Max(1, settings.MaxTokens)) * 4;

                // In the unified pipeline the newest turns travel in a separate chat-history field,
                // so the text handed to providers holds only older, already-summarised story. Add
                // the turns Chronicle saw itself, or a name mentioned a moment ago recalls nothing.
                string scene = (prompt ?? "") + "\n" + ChronicleManager.RecentStoryText;

                return ChronicleComposer.Compose(state, scene, maxChars,
                    settings, ChronicleManager.PlayerName());
            }
            catch (Exception ex)
            {
                // Runs inside the game's prompt builder: a throw here would lose the whole turn.
                UnityEngine.Debug.LogError($"[Chronicle] Context build error: {ex.Message}");
                return "";
            }
        }
    }
}
