using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Usable-ability layer: every allocated Keystone (and, optionally, Confluence) grants an
    /// ACTIVE perk the player can use on any target through the game's own interaction pipeline
    /// (roll bonus, cooldown, tooltips, art all handled natively).
    ///
    /// Since the game's Perks v2 update (11/18/2026) active abilities ARE perks: the targeting
    /// API takes a PerkNode, GameAbility is legacy, and there is a 16-slot perk hotbar. So the
    /// granted ability is a real PerkNode living in the web owner's
    /// <c>PlayableCharacterData.legacyPerks</c> - the game's flat list of perks that belong to no
    /// tree. That list is what makes a perk slottable on the hotbar
    /// (<c>GameCharacter.CanSlotOnHotbar</c>), it costs no perk points at rank 1
    /// (<c>GetAvailPerkPts</c> counts <c>rank - 1</c> for legacy perks), and the game saves and
    /// restores it itself.
    ///
    /// The awkward part is exactly that last point. The perk is game state: it is in the save and
    /// in every Undo/Retry snapshot, and it comes and goes with them. SkillWeb.json is not rolled
    /// back with it. So this class never assumes the two agree - every sync derives what should
    /// exist from the web, compares it with what the character actually has, and repairs the
    /// difference in both directions (see <see cref="SkillWebData.everGrantedPerkUuids"/>).
    /// </summary>
    public partial class SkillWebPlugin
    {
        /// <summary>Live granted perks, keyed by owning WebNode.id. Rebuilt by every SyncAbilities.</summary>
        private readonly Dictionary<string, PerkNode> _grantedAbilities = new Dictionary<string, PerkNode>();

        /// <summary>Node ids whose AI description is currently being generated (dedupe guard).</summary>
        private readonly HashSet<string> _abilityDescInFlight = new HashSet<string>();

        /// <summary>Node ids whose description was already requested this session (one attempt per session each).</summary>
        private readonly HashSet<string> _abilityDescRequested = new HashSet<string>();

        private bool NodeGrantsAbility(WebNode node)
        {
            if (node == null || !node.unlocked) return false;
            if (node.type == WebNodeType.Keystone) return true;
            if (node.type == WebNodeType.Confluence && SkillConfig.AbilitiesFromConfluences) return true;
            return false;
        }

        private static string AbilityNameFor(WebNode node)
            => string.IsNullOrWhiteSpace(node.abilityName) ? node.name : node.abilityName;

        /// <summary>
        /// A legacy perk has no parent, so this is its whole depth: it sets the roll bonus
        /// ((5 + depth) x rank) and the cooldown (3 + 2 x depth turns). Tier 1 = depth 0, i.e. the
        /// same +5 the pre-v2 learned abilities gave.
        /// </summary>
        private static int DepthFor(WebNode node) => Mathf.Clamp(node.tier - 1, 0, 2);

        /// <summary>
        /// Makes the owner's legacy perks match the web: one ACTIVE perk per allocated
        /// Keystone/Confluence, none for anything else SkillWeb ever granted. Called from the tail
        /// of SyncBonuses (load, Undo/Retry, every allocation/refund) and directly when the mod is
        /// switched off for the save.
        /// </summary>
        public void SyncAbilities()
        {
            if (Data == null) return;

            var manager = SS.I?.hackyManager;
            var pd = WebOwner()?.playableData;
            if (manager == null || pd == null || pd.legacyPerks == null) return;

            // Perk lists are host-authoritative in co-op; a client editing its copy would desync.
            if (MpSession.IsClient) return;

            bool featureOn = SkillConfig.GrantUsableAbilities && ModSwitch.Enabled;
            bool changed = false;

            RetireLegacyAbilities();

            // 1. Every qualifying node gets its perk: the one the game restored, or a new one.
            _grantedAbilities.Clear();
            var claimed = new HashSet<string>();
            if (featureOn)
            {
                foreach (var node in Data.nodes)
                {
                    if (!NodeGrantsAbility(node)) continue;
                    try
                    {
                        PerkNode perk = string.IsNullOrEmpty(node.grantedPerkUuid)
                            ? null
                            : pd.legacyPerks.FirstOrDefault(p => p != null && p.uuid == node.grantedPerkUuid);

                        if (perk == null)
                        {
                            perk = MintAbility(manager, pd, node);
                            changed = true;
                        }
                        else if (AlignPerkWithNode(manager, node, perk))
                        {
                            changed = true;
                        }

                        _grantedAbilities[node.id] = perk;
                        claimed.Add(perk.uuid);
                        if (!Data.everGrantedPerkUuids.Contains(perk.uuid)) Data.everGrantedPerkUuids.Add(perk.uuid);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[SkillWeb] Could not grant the ability for '{node.name}': {ex.Message}");
                    }
                }
            }

            // 2. Take back every perk SkillWeb ever granted that no node claims now: refunded or
            //    regenerated nodes, the feature or the mod switched off - and perks that a game
            //    rollback (Undo/Retry, or a crash before the next save) put back after we removed them.
            var ever = new HashSet<string>(Data.everGrantedPerkUuids);
            for (int i = pd.legacyPerks.Count - 1; i >= 0; i--)
            {
                var stray = pd.legacyPerks[i];
                if (stray == null || claimed.Contains(stray.uuid) || !ever.Contains(stray.uuid)) continue;

                var formerNode = Data.nodes.Find(n => n.grantedPerkUuid == stray.uuid);
                if (formerNode != null) formerNode.abilityCooldownRemaining = stray.cooldownTurnsRemaining;
                RemoveGrantedPerk(pd, stray);
                changed = true;
            }

            if (changed)
            {
                RefreshNativePerkUi(manager);
                SkillAbilityBar.Instance?.RefreshIfShowing();
            }
        }

        private PerkNode MintAbility(GameplayManager manager, PlayableCharacterData pd, WebNode node)
        {
            string abilityName = AbilityNameFor(node);
            string desc = !string.IsNullOrEmpty(node.grantedAbilityDesc)
                ? node.grantedAbilityDesc
                : (node.description ?? "");

            // Re-create the perk under the uuid this node has always used (see WebNode.grantedPerkUuid).
            string uuid = string.IsNullOrEmpty(node.grantedPerkUuid) ? Guid.NewGuid().ToString() : node.grantedPerkUuid;

            GameEntity registered;
            lock (SS.I.uuidToGameEntityMap) SS.I.uuidToGameEntityMap.TryGetValue(uuid, out registered);
            PerkNode perk = registered as PerkNode;
            if (registered != null && perk == null)
            {
                // Some other kind of entity owns that uuid. Never fight the game for one; start a new identity.
                uuid = Guid.NewGuid().ToString();
            }

            if (perk == null)
            {
                // skipAddingToUuidMap so the constructor's random uuid never reaches the map (and no
                // art is auto-requested); the perk is registered under ITS uuid right after.
                perk = new PerkNode(abilityName, desc, manager, skipAddingToUuidMap: true, skipAutoAssetGen: true);
                perk.uuid = uuid;
                perk.EnsureRegisteredInUuidMap();
                perk.cooldownTurnsRemaining = Mathf.Max(0, node.abilityCooldownRemaining);
            }

            perk.perkType = PerkNode.PerkType.ACTIVE;
            perk.isLearned = true;
            if (perk.rank < 1) perk.rank = 1;
            perk.legacyDepth = DepthFor(node);

            // Art is cached on disk by uuid, so a perk coming back under its old uuid (or replacing
            // a pre-v2 ability of the same uuid) already has its picture. Only ask for one if not.
            try
            {
                perk.LoadAssetsIfExistent();
                if (!Utils.EntityHasRenderableImg(perk.uuid, SS.I.saveSubDirAsArg))
                    AssetGenService.I.EnqueueImgAndSprite(perk);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[SkillWeb] ability art setup failed: " + ex.Message);
            }

            if (!pd.legacyPerks.Contains(perk)) pd.legacyPerks.Add(perk);

            // Put it on the hotbar if there is room, so allocating a Keystone visibly gives something.
            var hotbar = pd.GetHotbar();
            int slot = hotbar.IndexOf(perk);
            if (slot < 0)
            {
                slot = hotbar.IndexOf(null);
                if (slot >= 0) hotbar[slot] = perk;
            }

            node.grantedPerkUuid = perk.uuid;
            node.grantedPerkName = abilityName;

            RequestAbilityDescIfMissing(manager, node, perk);

            Logger.LogInfo($"[SkillWeb] Granted active perk '{abilityName}' ({node.type}) uuid={perk.uuid} depth={perk.legacyDepth}" +
                           (slot >= 0 ? $", hotbar slot {slot + 1}." : ", hotbar full."));
            return perk;
        }

        /// <summary>
        /// Brings a perk the game restored back in line with its node: the node may have been
        /// renamed by AI refinement or tier-upgraded since the perk was created. Returns true if
        /// anything the hotbar shows changed.
        /// </summary>
        private bool AlignPerkWithNode(GameplayManager manager, WebNode node, PerkNode perk)
        {
            bool changed = false;

            perk.perkType = PerkNode.PerkType.ACTIVE;
            perk.isLearned = true;
            if (perk.rank < 1) perk.rank = 1;

            int depth = DepthFor(node);
            if (perk.legacyDepth != depth) { perk.legacyDepth = depth; changed = true; }

            string want = AbilityNameFor(node);
            if (node.grantedPerkName == null) node.grantedPerkName = perk.name;
            if (!string.Equals(want, node.grantedPerkName, StringComparison.Ordinal))
            {
                // Follow the node only while the perk still carries the name SkillWeb gave it. If
                // the player renamed the perk (or locked its details) in the game's own editor, it
                // is theirs now.
                if (string.Equals(perk.name, node.grantedPerkName, StringComparison.Ordinal) && !perk.lockNameAndDesc)
                {
                    perk.SetName(want);
                    // The cached description was written for the old name.
                    node.grantedAbilityDesc = null;
                    perk.description = node.description ?? "";
                    _abilityDescRequested.Remove(node.id);
                    changed = true;
                }
                node.grantedPerkName = want;
            }

            RequestAbilityDescIfMissing(manager, node, perk);
            return changed;
        }

        private void RequestAbilityDescIfMissing(GameplayManager manager, WebNode node, PerkNode perk)
        {
            if (!SkillConfig.UseAIGeneration || !string.IsNullOrEmpty(node.grantedAbilityDesc)) return;
            if (!_abilityDescRequested.Add(node.id)) return; // one attempt per session; a failing backend is not retried every sync
            _ = GenerateAbilityDescAsync(manager, node, perk);
        }

        private void RemoveGrantedPerk(PlayableCharacterData pd, PerkNode perk)
        {
            if (perk == null) return;
            pd.legacyPerks.Remove(perk);
            pd.RemoveFromHotbar(slotted => slotted == perk);
            // TearDown() unsubscribes TurnHappened and removes the entity from the uuid map.
            try { perk.TearDown(); }
            catch (Exception ex) { Logger.LogWarning("[SkillWeb] ability teardown failed: " + ex.Message); }
        }

        /// <summary>
        /// One-time cleanup for saves made before Perks v2. Back then each granted ability was a
        /// GameAbility registered straight into the game's uuid map, which is serialized wholesale,
        /// so those saves still carry them as orphan entities. Tear the orphan down and hand its
        /// uuid to the node as its perk uuid: the replacement perk is created under the same uuid
        /// and so inherits the ability's cached art for free.
        /// </summary>
        private void RetireLegacyAbilities()
        {
            foreach (var node in Data.nodes)
            {
                if (string.IsNullOrEmpty(node.grantedAbilityUuid)) continue;
                try
                {
                    GameEntity orphan;
                    lock (SS.I.uuidToGameEntityMap) SS.I.uuidToGameEntityMap.TryGetValue(node.grantedAbilityUuid, out orphan);
                    if (orphan is GameAbility ga) ga.TearDown();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[SkillWeb] legacy ability cleanup failed: " + ex.Message);
                }
                if (string.IsNullOrEmpty(node.grantedPerkUuid)) node.grantedPerkUuid = node.grantedAbilityUuid;
                node.grantedAbilityUuid = null;
            }
        }

        private void RefreshNativePerkUi(GameplayManager manager)
        {
            try
            {
                if (manager.perkHotbar != null) manager.perkHotbar.Refresh();
                if (MpSession.IsHost) MpStateSync.Poke();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[SkillWeb] perk hotbar refresh failed: " + ex.Message);
            }
        }

        private async Task GenerateAbilityDescAsync(GameplayManager manager, WebNode node, PerkNode ability)
        {
            if (!_abilityDescInFlight.Add(node.id)) return;
            var dataAtStart = Data;
            try
            {
                string abilityName = AbilityNameFor(node);
                string about = !string.IsNullOrEmpty(node.keystoneRule) ? node.keystoneRule : node.description;
                string seed = abilityName + " (granted by " + node.name + ")" + (string.IsNullOrEmpty(about) ? "" : ": " + about);
                string ctx = manager.GetLastNStoryTurnsAsCombinedStrNoNewlines(2);
                string turnTxt = string.IsNullOrWhiteSpace(ctx) ? seed : (seed + "\n\n" + ctx);

                string desc = await AIAsker.GetAbilityDescFromStory(manager, abilityName, turnTxt);

                // Another save was loaded (or the web regenerated) while the request was in flight:
                // this node object no longer belongs to the live web, so do not write through it.
                if (!ReferenceEquals(Data, dataAtStart) || Data.GetNode(node.id) != node) return;
                // The node was renamed while we waited; this text describes the old ability.
                if (!string.Equals(AbilityNameFor(node), abilityName, StringComparison.Ordinal)) return;

                if (!string.IsNullOrWhiteSpace(desc))
                {
                    node.grantedAbilityDesc = desc;
                    // Write to whichever perk the node has NOW (it may have been re-created since).
                    if (_grantedAbilities.TryGetValue(node.id, out var live) && live != null) live.description = desc;
                    else if (ability != null) ability.description = desc;
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

        /// <summary>
        /// Mirrors live cooldowns onto the owning nodes. The game persists a perk's cooldown itself;
        /// this copy only matters if the perk is removed and granted again (mod switched off and on).
        /// </summary>
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

        /// <summary>Ordered (node, perk) pairs currently usable - for the ability bar UI.</summary>
        public List<KeyValuePair<WebNode, PerkNode>> GetUsableAbilities()
        {
            var result = new List<KeyValuePair<WebNode, PerkNode>>();
            if (Data == null) return result;
            foreach (var kv in _grantedAbilities)
            {
                var node = Data.GetNode(kv.Key);
                if (node != null && kv.Value != null && NodeGrantsAbility(node))
                    result.Add(new KeyValuePair<WebNode, PerkNode>(node, kv.Value));
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
