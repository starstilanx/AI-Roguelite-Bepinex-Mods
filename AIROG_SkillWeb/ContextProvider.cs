using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public class SkillWebContextProvider : AIROG_GenContext.IContextProvider
    {
        public int Priority => 85;
        public string Name => "Skill Web Constellation";
        public string Description => "Narrative traits, active Keystone rules, and milestone disciplines from the player's star map.";

        public string GetContext(string prompt, int maxTokens)
        {
            var plugin = SkillWebPlugin.Instance;
            if (plugin?.Data == null) return "";

            var data = plugin.Data;

            // 1. Collect unlocked traits (capped at 12)
            var traits = new List<string>();
            foreach (var node in data.nodes)
            {
                if (node.unlocked && node.ring > 0 && node.type != WebNodeType.Anchor)
                {
                    foreach (var trait in node.traits)
                    {
                        if (!string.IsNullOrEmpty(trait) && !traits.Contains(trait))
                        {
                            traits.Add(trait);
                        }
                    }
                }
            }
            var recentTraits = traits.Take(12).ToList();

            // 2. Collect active Keystone rules
            var keystones = data.nodes.FindAll(n => n.unlocked && n.type == WebNodeType.Keystone);

            // 3. Identify milestone discipline (sector with deepest unlocked node)
            WebSector deepestSector = null;
            int deepestUnlockedRing = 0;
            foreach (var node in data.nodes)
            {
                if (node.unlocked && node.ring > deepestUnlockedRing && !string.IsNullOrEmpty(node.sectorId))
                {
                    var sector = data.GetSector(node.sectorId);
                    if (sector != null)
                    {
                        deepestUnlockedRing = node.ring;
                        deepestSector = sector;
                    }
                }
            }

            // Build injection text
            var sb = new StringBuilder();
            bool hasContent = false;

            if (recentTraits.Count > 0)
            {
                sb.AppendLine($"Web traits: {string.Join(", ", recentTraits)}");
                hasContent = true;
            }

            if (keystones.Count > 0)
            {
                sb.AppendLine("[Active Keystones]");
                foreach (var ks in keystones)
                {
                    if (!string.IsNullOrEmpty(ks.keystoneRule))
                    {
                        sb.AppendLine($"- {ks.name}: {ks.keystoneRule}");
                    }
                }
                hasContent = true;
            }

            if (deepestSector != null)
            {
                sb.AppendLine($"They are furthest advanced in the discipline of {deepestSector.name}.");
                hasContent = true;
            }

            if (!hasContent) return "";

            return "\n\n[CONSTELLATION SKILL WEB STATUS]\n" + sb.ToString();
        }
    }

    public static class GenContextIntegration
    {
        public static void Register()
        {
            try
            {
                // Unregister the legacy Skill Web provider to avoid duplicate traits blocks
                var providers = AIROG_GenContext.ContextManager.GetProviders();
                int removed = providers.RemoveAll(p => p.Name == "Skill Web Traits");
                if (removed > 0)
                {
                    Debug.Log($"[SkillWeb] Unregistered legacy GenContext provider 'Skill Web Traits'.");
                }

                // Register our new v4 provider
                AIROG_GenContext.ContextManager.RegisterProvider(new SkillWebContextProvider());
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] Failed to register GenContext provider: " + ex.Message);
            }
        }
    }
}
