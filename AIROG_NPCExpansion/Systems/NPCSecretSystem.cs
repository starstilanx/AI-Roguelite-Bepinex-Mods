using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Manages NPC secrets — hidden knowledge about crimes, allegiances, relationships,
    /// abilities, or past events. Secrets are generated lazily on first inquiry and
    /// revealed through high trust or explicit player action.
    /// </summary>
    public static class NPCSecretSystem
    {
        private static readonly System.Random _rng = new System.Random();

        // ─── Generation (lazy, first-ask) ─────────────────────────────────────────

        public static async Task EnsureSecretsGenerated(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (data.Secrets == null) data.Secrets = new List<NPCData.NPCSecret>();
            if (data.Secrets.Count > 0) return;
            if (string.IsNullOrEmpty(data.Personality)) return;

            string context = manager.GetContextForQuickActions();
            if (context.Length > 800) context = context.Substring(context.Length - 800);

            string prompt = $"NPC: {npc.GetPrettyName()}\n" +
                            $"Personality: {data.Personality}\n" +
                            $"Scenario: {data.Scenario}\n" +
                            $"World context: {context}\n\n" +
                            $"Generate 1-2 secrets this NPC is hiding from the world. Each must be dark, surprising, or deeply personal.\n" +
                            $"Format each secret on its own line exactly as:\n" +
                            $"SECRET:[Category]:[Secret text under 40 words]\n" +
                            $"Categories: Crime, Allegiance, Relationship, Ability, Past\n" +
                            $"Output ONLY secret lines, nothing else.";

            string result = await GameCompat.GenerateTxt(
                AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                prompt,
                AIAsker.ChatGptPostprocessingType.NONE);

            if (string.IsNullOrEmpty(result)) return;

            foreach (var line in result.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("SECRET:")) continue;
                var parts = trimmed.Substring(7).Split(new[] { ':' }, 2);
                if (parts.Length < 2) continue;
                string text = parts[1].Trim();
                if (string.IsNullOrEmpty(text)) continue;

                data.Secrets.Add(new NPCData.NPCSecret
                {
                    Category = parts[0].Trim(),
                    Text = text,
                    IsRevealed = false
                });
            }

            if (data.Secrets.Count > 0)
            {
                NPCData.Save(npc.uuid, data);
                Debug.Log($"[SecretSystem] Generated {data.Secrets.Count} secret(s) for {npc.GetPrettyName()}");
            }
        }

        // ─── Player-initiated reveal ("Ask Secret" arc action) ────────────────────

        public static async Task TryRevealSecret(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            // Generate secrets if this NPC has never been probed before
            await EnsureSecretsGenerated(npc, data, manager);

            if (data.Secrets == null || data.Secrets.Count == 0)
            {
                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#aaaaff>{npc.GetPrettyName()} has nothing to hide.</color>");
                return;
            }

            var hidden = data.Secrets.Where(s => !s.IsRevealed).ToList();
            if (hidden.Count == 0)
            {
                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#aaaaff>{npc.GetPrettyName()} has no more secrets to share with you.</color>");
                return;
            }

            var secret = hidden[_rng.Next(hidden.Count)];
            RevealSecret(npc, data, secret, manager, "confides in you");
        }

        // ─── Auto-reveal at very high trust (80+) ─────────────────────────────────

        public static void CheckAutoReveal(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (data.Secrets == null || data.Secrets.Count == 0) return;
            if (data.Affinity < 80) return;

            var hidden = data.Secrets.Where(s => !s.IsRevealed).ToList();
            if (hidden.Count == 0) return;

            RevealSecret(npc, data, hidden.First(), manager, "trusts you deeply and reveals");
        }

        // ─── Core reveal logic ─────────────────────────────────────────────────────

        private static void RevealSecret(GameCharacter npc, NPCData data, NPCData.NPCSecret secret,
            GameplayManager manager, string reason)
        {
            secret.IsRevealed = true;
            NPCData.Save(npc.uuid, data);

            _ = manager.gameLogView.LogTextCompat(
                $"<color=#c890ff>[SECRET] {npc.GetPrettyName()} {reason}:\n" +
                $"[{secret.Category}] {secret.Text}</color>");

            RelationshipArcSystem.RecordMilestone(npc.uuid, data,
                $"Learned {npc.GetPrettyName()}'s secret [{secret.Category}]");

            if (secret.Category == "Ability")
                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#c890ff>{npc.GetPrettyName()} offers to share their hidden technique. Ask them to teach you!</color>");

            Debug.Log($"[SecretSystem] Revealed secret for {npc.GetPrettyName()}: {secret.Text}");
        }

        // ─── Prompt injection (revealed secrets visible to AI) ────────────────────

        public static string BuildRevealedContext(NPCData data)
        {
            if (data.Secrets == null) return "";
            var revealed = data.Secrets.Where(s => s.IsRevealed).ToList();
            if (revealed.Count == 0) return "";
            return "Known secrets: " + string.Join("; ", revealed.Select(s => $"[{s.Category}] {s.Text}"));
        }
    }
}
