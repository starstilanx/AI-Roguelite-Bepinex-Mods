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

        public static DetectionResult DetectLoop(string generatedText, List<string> history)
        {
            if (string.IsNullOrWhiteSpace(generatedText)) return new DetectionResult { IsLoop = false };

            // 1. Direct Repetition Check (Exact/Near Exact)
            var directLoop = CheckDirectRepetition(generatedText, history);
            if (directLoop.IsLoop) return directLoop;

            // 2. Semantic/N-Gram Overlap Check
            var semanticLoop = CheckSemanticOverlap(generatedText, history);
            if (semanticLoop.IsLoop) return semanticLoop;

            // 3. Structural Repetition Check
            var structuralLoop = CheckStructuralRepetition(generatedText, history);
            if (structuralLoop.IsLoop) return structuralLoop;

            return new DetectionResult { IsLoop = false };
        }

        private static DetectionResult CheckDirectRepetition(string text, List<string> history)
        {
            string normalizedText = Normalize(text);
            foreach (var pastTurn in history.Skip(Math.Max(0, history.Count - 5))) // Check last 5 turns
            {
                string normalizedPast = Normalize(pastTurn);
                if (normalizedPast.Length < 20) continue; // Ignore very short sentences

                if (normalizedText.Contains(normalizedPast) || normalizedPast.Contains(normalizedText))
                {
                    return new DetectionResult
                    {
                        IsLoop = true,
                        Severity = 1.0f,
                        Reason = "Direct text repetition detected with recent history."
                    };
                }

                // Check for significant overlap percentage (Levenstein or similar would be better, but simple overlap for now)
                float similarity = CalculateSimilarity(normalizedText, normalizedPast);
                if (similarity > 0.85f)
                {
                    return new DetectionResult
                    {
                        IsLoop = true,
                        Severity = similarity,
                        Reason = $"High similarity ({similarity:P0}) detected with a recent turn."
                    };
                }
            }
            return new DetectionResult { IsLoop = false };
        }

        private static DetectionResult CheckSemanticOverlap(string text, List<string> history)
        {
            // Simple N-gram check (e.g., 4-word sequences)
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
                        Reason = $"Heavy n-gram overlap ({overlapRate:P0}) detected."
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
            
            if (currentLines.Count < 2) return new DetectionResult { IsLoop = false };

            // Check if sentences in the SAME generated block repeat structures
            // e.g., "The [X] is [Y]. The [A] is [B]."
            var starts = currentLines.Select(l => l.Split(' ').FirstOrDefault()?.ToLower()).ToList();
            int repeatingStarts = starts.GroupBy(s => s).Where(g => g.Count() > 2).Sum(g => g.Count());
            
            if (repeatingStarts > currentLines.Count * 0.7f)
            {
                return new DetectionResult
                {
                    IsLoop = true,
                    Severity = 0.7f,
                    Reason = "Repetitive sentence starting structure detected within output."
                };
            }

            return new DetectionResult { IsLoop = false };
        }

        private static string Normalize(string text)
        {
            if (text == null) return "";
            // Remove punctuation and whitespace for comparison
            return Regex.Replace(text.ToLower(), @"[^\w\s]", "").Trim();
        }

        private static float CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
            if (s1 == s2) return 1.0f;

            int stepsToSame = ComputeLevenshteinDistance(s1, s2);
            return 1.0f - ((float)stepsToSame / Math.Max(s1.Length, s2.Length));
        }

        private static int ComputeLevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private static HashSet<string> GetNgrams(string text, int n)
        {
            var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(w => Normalize(w))
                            .Where(w => !string.IsNullOrEmpty(w))
                            .ToList();
            
            var ngrams = new HashSet<string>();
            for (int i = 0; i <= words.Count - n; i++)
            {
                ngrams.Add(string.Join(" ", words.GetRange(i, n)));
            }
            return ngrams;
        }
    }
}
