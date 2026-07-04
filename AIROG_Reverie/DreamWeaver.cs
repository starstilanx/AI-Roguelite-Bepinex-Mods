using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Reverie
{
    /// <summary>
    /// Composes a dream locally (no AI call) from Chronicle beats + a theme table.
    /// The AI narrator does the actual weaving in-scene; this just supplies the loom.
    /// </summary>
    public static class DreamWeaver
    {
        private class DreamTheme
        {
            public string Name;
            public string Flavor;   // dreamscape texture
            public string Core;     // the central confrontation the dreamer must resolve
        }

        private static readonly DreamTheme[] Themes =
        {
            new DreamTheme { Name = "The Pursuit",
                Flavor = "Something vast and unseen hunts the dreamer through corridors built from places they have been, each doorway opening onto somewhere they should not be able to reach from here.",
                Core   = "turn and face the pursuer, and learn what it wears as a face" },
            new DreamTheme { Name = "The Drowned Feast",
                Flavor = "A long banquet table stretches under dark water. The guests are people from the dreamer's past, eating and laughing as if the water were air.",
                Core   = "take the empty seat and hear the toast being made in the dreamer's honor" },
            new DreamTheme { Name = "The Locked Door",
                Flavor = "Every wall in the dream has the same small door, always locked, warm to the touch. The keyhole shows only candlelight and a moving shadow.",
                Core   = "find the key, which is always something the dreamer once gave away" },
            new DreamTheme { Name = "The Double",
                Flavor = "The dreamer keeps glimpsing themselves ahead in the crowd — same face, same scars — living a version of their life where every choice went the other way.",
                Core   = "catch the double and settle which of them is the echo" },
            new DreamTheme { Name = "The Unraveling Road",
                Flavor = "The road home unpicks itself behind the dreamer like thread pulled from cloth. Landmarks from their travels drift past in the wrong order, mislabeled and slightly wrong.",
                Core   = "reach the end of the road before it finishes unraveling beneath them" },
            new DreamTheme { Name = "The Court of the Forgotten",
                Flavor = "A throne room of gray figures — everyone the dreamer's story has left behind — sits in silent session. The dreamer stands at the center as if summoned.",
                Core   = "answer the court's single question honestly" },
            new DreamTheme { Name = "The Molting",
                Flavor = "The dreamer's own equipment, scars, and titles hang on hooks in an endless cloakroom, and the attendant insists at least one thing must be left behind.",
                Core   = "choose what to surrender and what to fight to keep" },
            new DreamTheme { Name = "The Rehearsal",
                Flavor = "Actors on a crooked stage perform scenes from the dreamer's future, badly, stopping to argue about how the dreamer will really behave when the time comes.",
                Core   = "step onto the stage and play the scene the way it should go" },
            new DreamTheme { Name = "The Tooth of the World",
                Flavor = "A mountain of black glass grinds slowly across a plain of everything the dreamer has ever broken, and it is grinding in their direction.",
                Core   = "climb it, or stop it, or learn why it chose this path" },
            new DreamTheme { Name = "The Borrowed Voice",
                Flavor = "Everyone in the dream speaks with the dreamer's voice, and the dreamer speaks with someone else's — someone they know, though they cannot place whose.",
                Core   = "find the one keeping the dreamer's true voice and bargain for it" },
            new DreamTheme { Name = "The Garden of Hours",
                Flavor = "A walled garden where each plant is one of the dreamer's days, some flowering, some blighted, one enormous and thorned at the center, still growing.",
                Core   = "prune the thorned day at the garden's heart, or feed it" },
            new DreamTheme { Name = "The Ferryman's Ledger",
                Flavor = "A river of ink, a patient ferryman, and a ledger listing everything the dreamer owes and is owed. Several entries are written in a hand the dreamer recognizes as their own, but does not remember writing.",
                Core   = "settle one debt from the ledger before the crossing ends" },
        };

        private static readonly System.Random Rng = new System.Random();

        /// <summary>Weave a new dream from Chronicle memories (if available) + a random theme.</summary>
        public static DreamRecord Weave(ReverieState state)
        {
            var theme = Themes[Rng.Next(Themes.Length)];
            var beats = PickMemoryThreads();

            string playerName = null, placeName = null;
            try
            {
                playerName = SS.I?.hackyManager?.playerCharacter?.name;
                placeName = SS.I?.hackyManager?.currentPlace?.GetPrettyName();
            }
            catch { }

            var premise = theme.Flavor;
            if (beats.Count > 0)
                premise += " Threads of true memory are woven through it: " +
                           string.Join(" ", beats.Select(b => $"\"{b.TrimEnd('.')}\" —")).TrimEnd('—', ' ') + ".";
            else if (!string.IsNullOrEmpty(placeName))
                premise += $" The dream borrows its bones from {placeName}, rebuilt wrong.";

            var dream = new DreamRecord
            {
                Theme = theme.Name,
                Premise = premise,
                Core = theme.Core,
                Progress = 0,
                Lucidity = ReverieManager.START_LUCIDITY,
                DreamTurnsRemaining = ReverieManager.DREAM_LENGTH,
                StartedTurn = state.GlobalTurn,
            };

            Debug.Log($"[Reverie] Wove dream \"{theme.Name}\" for {playerName ?? "the dreamer"} " +
                      $"({beats.Count} memory threads).");
            return dream;
        }

        /// <summary>
        /// Pick 2-3 beats from chronicle.json, weighted: milestones ×3, deaths ×2.
        /// Returns an empty list when Chronicle isn't installed or has no beats yet.
        /// </summary>
        private static List<string> PickMemoryThreads()
        {
            var result = new List<string>();
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return result;
                string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "chronicle.json");
                if (!File.Exists(path)) return result;

                var chron = JsonConvert.DeserializeObject<RvChronicleState>(File.ReadAllText(path));
                if (chron == null) return result;

                var pool = new List<RvChronicleBeat>();
                if (chron.ClosedChapters != null)
                    foreach (var ch in chron.ClosedChapters)
                        if (ch?.Beats != null) pool.AddRange(ch.Beats);
                if (chron.CurrentChapter?.Beats != null)
                    pool.AddRange(chron.CurrentChapter.Beats);

                pool.RemoveAll(b => string.IsNullOrWhiteSpace(b?.Summary));
                if (pool.Count == 0) return result;

                // Weighted sampling without replacement
                var weighted = new List<RvChronicleBeat>();
                foreach (var b in pool)
                {
                    int w = 1;
                    if (b.IsMilestone) w = 3;
                    else if (b.Type == "Death" || b.Type == "3") w = 2;
                    for (int i = 0; i < w; i++) weighted.Add(b);
                }

                int want = Math.Min(pool.Count, 2 + Rng.Next(2)); // 2-3
                var chosen = new HashSet<RvChronicleBeat>();
                int guard = 0;
                while (chosen.Count < want && guard++ < 100)
                    chosen.Add(weighted[Rng.Next(weighted.Count)]);

                foreach (var b in chosen) result.Add(b.Summary);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Reverie] Could not read chronicle.json for dream material: {ex.Message}");
            }
            return result;
        }

        /// <summary>Fallback haunting text when a nightmare ends without a usable last event.</summary>
        public static string DefaultHauntingText(DreamRecord dream)
        {
            return $"Something from the dream called \"{dream.Theme}\" did not stay behind when the dreamer woke. " +
                   "It has no face yet. It is patient.";
        }
    }
}
