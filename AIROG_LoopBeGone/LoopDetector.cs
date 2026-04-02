using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIROG_LoopBeGone
{
    public static class LoopDetector
    {
        public class DetectionResult
        {
            public bool IsLoop { get; set; }
            public float Severity { get; set; } // 0.0 to 1.0
            public string Reason { get; set; }
        }

        // Levenshtein is O(n*m) — skip it for long strings to avoid freezes
        private const int MaxLevenshteinLength = 400;

        public static DetectionResult DetectLoop(string generatedText, List<string> history)
        {
            if (string.IsNullOrWhiteSpace(generatedText)) return new DetectionResult { IsLoop = false };

            var directLoop = CheckDirectRepetition(generatedText, history);
            if (directLoop.IsLoop) return directLoop;

            var semanticLoop = CheckSemanticOverlap(generatedText, history);
            if (semanticLoop.IsLoop) return semanticLoop;

            var structuralLoop = CheckStructuralRepetition(generatedText, history);
            if (structuralLoop.IsLoop) return structuralLoop;

            return new DetectionResult { IsLoop = false };
        }

        private static DetectionResult CheckDirectRepetition(string text, List<string> history)
        {
            string normalizedText = Normalize(text);

            foreach (var pastTurn in history.Skip(Math.Max(0, history.Count - 5)))
            {
                string normalizedPast = Normalize(pastTurn);
                if (normalizedPast.Length < 20) continue;

                if (normalizedText.Contains(normalizedPast) || normalizedPast.Contains(normalizedText))
                {
                    return new DetectionResult
                    {
                        IsLoop = true,
                        Severity = 1.0f,
                        Reason = "Direct text repetition detected with recent history."
                    };
                }

                // Only run Levenshtein if both strings are short enough
                if (normalizedText.Length <= MaxLevenshteinLength && normalizedPast.Length <= MaxLevenshteinLength)
                {
                    float similarity = CalculateSimilarity(normalizedText, normalizedPast);
                    if (similarity > 0.85f)
                    {
                        return new DetectionResult
                        {
                            IsLoop = true,
                            Severity = similarity,
                            Reason = $"High similarity ({similarity:P0}) with a recent turn."
                        };
                    }
                }
            }
            return new DetectionResult { IsLoop = false };
        }

        private static DetectionResult CheckSemanticOverlap(string text, List<string> history)
        {
            var currentNgrams = GetNgrams(text, 4);
            if (currentNgrams.Count == 0) return new DetectionResult { IsLoop = false };

            foreach (var pastTurn in history.Skip(Math.Max(0, history.Count - 3)))
            {
                var pastNgrams = GetNgrams(pastTurn, 4);
                int matches = currentNgrams.Intersect(pastNgrams).Count();
                float overlapRate = (float)matches / currentNgrams.Count;

                if (overlapRate > 0.6f && matches >= 3)
                {
                    return new DetectionResult
                    {
                        IsLoop = true,
                        Severity = overlapRate,
                        Reason = $"Heavy n-gram overlap ({overlapRate:P0}) with recent history."
                    };
                }
            }
            return new DetectionResult { IsLoop = false };
        }

        private static DetectionResult CheckStructuralRepetition(string text, List<string> history)
        {
            var currentLines = text.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(l => l.Trim())
                                  .Where(l => l.Length > 5)
                                  .ToList();

            if (currentLines.Count < 3) return new DetectionResult { IsLoop = false };

            // Flag if the same word starts 4+ sentences AND makes up >70% of all sentence starts
            var starts = currentLines
                .Select(l => l.Split(' ').FirstOrDefault()?.ToLower())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            int repeatingStarts = starts.GroupBy(s => s).Where(g => g.Count() >= 4).Sum(g => g.Count());

            if (repeatingStarts > currentLines.Count * 0.7f)
            {
                return new DetectionResult
                {
                    IsLoop = true,
                    Severity = 0.7f,
                    Reason = "Repetitive sentence structure detected within response."
                };
            }

            return new DetectionResult { IsLoop = false };
        }

        private static string Normalize(string text)
        {
            if (text == null) return "";
            return Regex.Replace(text.ToLower(), @"[^\w\s]", "").Trim();
        }

        private static float CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
            if (s1 == s2) return 1.0f;

            int steps = ComputeLevenshteinDistance(s1, s2);
            return 1.0f - ((float)steps / Math.Max(s1.Length, s2.Length));
        }

        private static int ComputeLevenshteinDistance(string s, string t)
        {
            int n = s.Length, m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;

            // Use two-row rolling array instead of full n*m matrix
            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = t[j - 1] == s[i - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var temp = prev; prev = curr; curr = temp;
            }
            return prev[m];
        }

        private static HashSet<string> GetNgrams(string text, int n)
        {
            // Normalize once, then split — avoids per-word regex calls
            string normalized = Normalize(text);
            var words = normalized.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            var ngrams = new HashSet<string>();
            for (int i = 0; i <= words.Length - n; i++)
            {
                ngrams.Add(string.Join(" ", words, i, n));
            }
            return ngrams;
        }
    }
}
