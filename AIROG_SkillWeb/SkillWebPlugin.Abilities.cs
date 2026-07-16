using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Usable-ability layer: mints native GameAbility (LEARNED) entities from unlocked
    /// Keystone/Confluence nodes so they can be cast on any target through the game's own
    /// interaction pipeline (cooldowns, roll bonus, tooltips, art all handled natively).
    /// SkillWeb owns these abilities entirely — they are NOT added to the native abilityPool,
    /// so they stay out of the level-gated learned-ability slots and out of the native save;
    /// SkillWeb re-mints them each session and persists cooldown on the owning WebNode.
    /// </summary>
    public partial class SkillWebPlugin
    {
        /// <summary>Live minted usable abilities, keyed by owning WebNode.id.</summary>
        private readonly Dictionary<string, GameAbility> _grantedAbilities = new Dictionary<string, GameAbility>();

        /// <summary>Node ids whose AI description is currently being generated (dedupe guard).</summary>
        private readonly HashSet<string> _abilityDescInFlight = new HashSet<string>();

        private bool NodeGrantsAbility(WebNode node)
        {
            if (node == null || !node.unlocked) return false;
            if (node.type == WebNodeType.Keystone) return true;
            if (node.type == WebNodeType.Confluence && SkillConfig.AbilitiesFromConfluences) return true;
            return false;
        }

        /// <summary>
        /// Reconciles unlocked Keystone/Confluence nodes with live usable GameAbility entities.
        /// Called from the tail of SyncBonuses (runs on load and on every unlock/refund).
        /// </summary>
        public void SyncAbilities()
        {
            if (Data == null) return;

            // Feature disabled: tear down anything we minted and bail.
            if (!SkillConfig.GrantUsableAbilities)
            {
                foreach (var kv in _grantedAbilities.ToList()) TearDownAbility(kv.Value);
                _grantedAbilities.Clear();
                return;
            }

            var manager = SS.I?.hackyManager;
            if (manager == null) return;

            // 1. Tear down abilities whose node no longer exists or no longer qualifies.
            foreach (var nodeId in _grantedAbilities.Keys.ToList())
            {
                var n = Data.GetNode(nodeId);
                if (n == null || !NodeGrantsAbility(n))
                {
                    TearDownAbility(_grantedAbilities[nodeId]);
                    _grantedAbilities.Remove(nodeId);
                    if (n != null) n.grantedAbilityUuid = null;
                }
            }

            // 2. Mint abilities for qualifying nodes that don't have a live one this session.
            bool changed = false;
            foreach (var node in Data.nodes)
            {
                if (!NodeGrantsAbility(node)) continue;
                if (_grantedAbilities.ContainsKey(node.id)) continue;
                MintAbility(manager, node);
                changed = true;
            }

            if (changed) SkillAbilityBar.Instance?.RefreshIfShowing();
        }

        private void MintAbility(GameplayManager manager, WebNode node)
        {
            bool isFirstEver = string.IsNullOrEmpty(node.grantedAbilityUuid);
            string uuid = isFirstEver ? Guid.NewGuid().ToString() : node.grantedAbilityUuid;

            string desc = !string.IsNullOrEmpty(node.grantedAbilityDesc)
                ? node.grantedAbilityDesc
                : (node.description ?? "");

            // Mastery tier maps to the native LEARNED ability tier (0-based, capped at 2).
            int tier = Mathf.Clamp(node.tier - 1, 0, 2);

            var ability = new GameAbility(node.name, desc, manager,
                GameAbility.AbilityType.LEARNED, null, tier, skipAddingToUuidMap: true);
            ability.uuid = uuid;
            lock (SS.I.uuidToGameEntityMap) { SS.I.uuidToGameEntityMap[uuid] = ability; }

            // Restore persisted cooldown so it survives save/load within a run.
            ability.cooldownTurnsRemaining = Mathf.Max(0, node.abilityCooldownRemaining);

            node.grantedAbilityUuid = uuid;
            _grantedAbilities[node.id] = ability;

            // Only enqueue art on the very first mint; thereafter the image is disk-cached by uuid.
            if (isFirstEver)
            {
                try { AssetGenService.I.EnqueueImgAndSprite(ability); }
                catch (Exception ex) { Logger.LogWarning("[SkillWeb] ability art enqueue failed: " + ex.Message); }
            }

            // Generate rich AI flavor/rules text once, then cache it on the node.
            if (string.IsNullOrEmpty(node.grantedAbilityDesc) && SkillConfig.UseAIGeneration)
            {
                _ = GenerateAbilityDescAsync(manager, node, ability);
            }

            Logger.LogInfo($"[SkillWeb] Minted usable ability '{node.name}' ({node.type}) uuid={uuid} tier={tier}.");
        }

        private async Task GenerateAbilityDescAsync(GameplayManager manager, WebNode node, GameAbility ability)
        {
            if (!_abilityDescInFlight.Add(node.id)) return;
            try
            {
                string seed = string.IsNullOrEmpty(node.description) ? node.name : (node.name + ": " + node.description);
                string ctx = manager.GetLastNStoryTurnsAsCombinedStrNoNewlines(2);
                string turnTxt = string.IsNullOrWhiteSpace(ctx) ? seed : (seed + "\n\n" + ctx);

                string desc = await AIAsker.GetAbilityDescFromStory(manager, node.name, turnTxt);
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    node.grantedAbilityDesc = desc;
                    if (ability != null) ability.description = desc;
                    SaveData();
                    SkillAbilityBar.Instance?.RefreshIfShowing();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[SkillWeb] ability desc gen failed for " + node.name + ": " + ex.Message);
            }
            finally
            {
                _abilityDescInFlight.Remove(node.id);
            }
        }

        private void TearDownAbility(GameAbility ability)
        {
            if (ability == null) return;
            // TearDown() unsubscribes TurnHappened and removes the entity from the uuid map.
            try { ability.TearDown(); }
            catch (Exception ex) { Logger.LogWarning("[SkillWeb] ability teardown failed: " + ex.Message); }
        }

        /// <summary>Copies live cooldown back onto the owning nodes so it survives save/load.</summary>
        public void PersistAbilityCooldowns()
        {
            if (Data == null) return;
            foreach (var kv in _grantedAbilities)
            {
                var node = Data.GetNode(kv.Key);
                if (node != null && kv.Value != null)
                    node.abilityCooldownRemaining = kv.Value.cooldownTurnsRemaining;
            }
        }

        /// <summary>Ordered (node, ability) pairs currently usable — for the ability bar UI.</summary>
        public List<KeyValuePair<WebNode, GameAbility>> GetUsableAbilities()
        {
            var result = new List<KeyValuePair<WebNode, GameAbility>>();
            if (Data == null) return result;
            foreach (var kv in _grantedAbilities)
            {
                var node = Data.GetNode(kv.Key);
                if (node != null && kv.Value != null && NodeGrantsAbility(node))
                    result.Add(new KeyValuePair<WebNode, GameAbility>(node, kv.Value));
            }
            // Confluences first (rarer), then alphabetical.
            return result
                .OrderByDescending(p => p.Key.type == WebNodeType.Confluence)
                .ThenBy(p => p.Value.GetPrettyName())
                .ToList();
        }

        /// <summary>True if the player has at least one usable granted ability.</summary>
        public bool HasUsableAbilities() => _grantedAbilities.Count > 0;
    }
}
