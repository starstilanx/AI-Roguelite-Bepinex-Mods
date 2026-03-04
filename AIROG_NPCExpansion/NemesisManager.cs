using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using AIROG_NPCExpansion;

namespace AIROG_NPCExpansion
{
    public static class NemesisManager
    {
        private static readonly List<string> Titles = new List<string>
        {
            "the Player-Slayer",
            "the Kingslayer",
            "the Render",
            "the Unbroken",
            "the Vanquisher",
            "the Ascendant",
            "the Doom-Bringer",
            "the Grim",
            "the Executioner",
            "the Victorious"
        };

        private static readonly List<string> NamePrefixes = new List<string> { "Karg", "Morg", "Thrak", "Gorm", "Azog", "Urok", "Zog", "Brak" };
        private static readonly List<string> NameSuffixes = new List<string> { "ul", "or", "ar", "esh", "ath", "oz", "ur", "mak" };

        public static void PromoteKiller(GameCharacter killer, GameplayManager manager = null)
        {
            if (killer == null) return;
            if (manager == null) manager = killer.manager;

            // 1. Load or Create Data
            NPCData data = NPCData.Load(killer.uuid);
            if (data == null)
            {
                data = new NPCData();
                data.Name = killer.GetPrettyName();
            }

            string oldName = killer.GetPrettyName();

            // Handle repeat kills — smaller boost, no rename
            if (data.IsNemesis)
            {
                Debug.Log($"[Nemesis] {oldName} is already a Nemesis — applying repeat-kill boost.");
                killer.level += 1;
                killer.damage += (long)(killer.damage * 0.1f);
                long repeatHp = (long)(killer.GetMaxHealth() * 1.1f);
                killer.SetMaxHealth(repeatHp);
                killer.SetHealth(repeatHp);

                if (data.LongTermMemories == null) data.LongTermMemories = new List<string>();
                data.LongTermMemories.Add($"I defeated the player again on {DateTime.Now.ToShortDateString()}. My legend grows.");
                if (data.LongTermMemories.Count > 10) data.LongTermMemories.RemoveAt(0);

                NPCData.Save(killer.uuid, data);

                if (manager?.gameLogView != null)
                    _ = manager.gameLogView.LogText(GameLogView.AiDecision(
                        $"[NEMESIS] {oldName} has defeated you again and grows ever stronger!"));
                return;
            }

            Debug.Log($"[Nemesis] Promoting {oldName} to Nemesis status!");

            // 2. Renaming Logic
            string title = Titles[UnityEngine.Random.Range(0, Titles.Count)];
            string newName = oldName;

            if (IsGenericName(oldName))
            {
                string generatedName = NamePrefixes[UnityEngine.Random.Range(0, NamePrefixes.Count)] +
                                       NameSuffixes[UnityEngine.Random.Range(0, NameSuffixes.Count)];
                newName = $"{generatedName} {title}";
            }
            else if (!oldName.Contains("the "))
            {
                newName = $"{oldName} {title}";
            }

            // Apply Name
            data.Name = newName;
            if (killer.ParentInGameEnt() != null)
                killer.ParentInGameEnt().name = newName;

            // 3. Stat Boost
            int boost = 5;
            if (data.Attributes == null) data.Attributes = new Dictionary<SS.PlayerAttribute, long>();
            foreach (SS.PlayerAttribute attr in Enum.GetValues(typeof(SS.PlayerAttribute)))
            {
                if (!data.Attributes.ContainsKey(attr)) data.Attributes[attr] = 10;
                data.Attributes[attr] += boost;
            }

            killer.level += 2;
            killer.damage += (long)(killer.damage * 0.2f);
            long newMaxHp = (long)(killer.GetMaxHealth() * 1.2f);
            killer.SetMaxHealth(newMaxHp);
            killer.SetHealth(newMaxHp); // Full heal on promotion

            // 4. Memory Injection
            if (data.LongTermMemories == null) data.LongTermMemories = new List<string>();
            data.LongTermMemories.Add($"I defeated the player in single combat on {DateTime.Now.ToShortDateString()}.");
            data.LongTermMemories.Add("I feel invincible. The player is no match for me.");

            // 5. Nemesis Flag & Loot Logic
            data.IsNemesis = true;
            if (IsNemesisLootingEnabled())
                LootPlayer(killer, manager);

            // 6. Save
            NPCData.Save(killer.uuid, data);

            // 7. Dramatic notification
            Debug.Log($"[Nemesis] {oldName} is now known as {newName}.");
            if (manager?.gameLogView != null)
                _ = manager.gameLogView.LogText(GameLogView.AiDecision(
                    $"[NEMESIS BORN] {oldName} has slain you and risen as {newName}! " +
                    $"They will remember this victory."));
        }

        private static void LootPlayer(GameCharacter killer, GameplayManager manager)
        {
            if (manager == null || manager.equipmentPanel == null) return;

            var slots = manager.equipmentPanel.GetAllEquipmentSlots();
            var equippedItems = slots.Where(s => s.item != null).ToList();
            if (equippedItems.Count == 0) return;

            var victimSlot = equippedItems[UnityEngine.Random.Range(0, equippedItems.Count)];
            var item = victimSlot.item;

            Debug.Log($"[Nemesis] Looting {item.GetPrettyName()} from player!");

            // Remove from player equipment slot
            victimSlot.item = null;

            // Transfer to killer
            item.SetParentEnt(killer);
            item.itemState = GameItem.ItemState.INVENTORY;
            if (killer.items == null) killer.items = new List<GameItem>();
            killer.items.Add(item);

            // Record the trophy
            NPCData data = NPCData.Load(killer.uuid);
            if (data != null)
            {
                data.LongTermMemories.Add($"I took {item.GetPrettyName()} as a trophy from the player.");
                NPCData.Save(killer.uuid, data);
            }

            if (manager?.gameLogView != null)
                _ = manager.gameLogView.LogText(GameLogView.AiDecision(
                    $"[NEMESIS] {killer.GetPrettyName()} takes your {item.GetPrettyName()} as a trophy!"));
        }

        private static bool IsGenericName(string name)
        {
            string lower = name.ToLower();
            return lower.Contains("bandit") || lower.Contains("guard") || lower.Contains("goblin") ||
                   lower.Contains("wolf") || lower.Contains("skeleton") || lower.Contains("zombie") ||
                   lower.Contains("orc") || lower.Contains("soldier") || lower.Contains("thug");
        }

        private static bool IsNemesisLootingEnabled()
        {
            try { return AIROG_GenContext.ContextManager.GetGlobalSetting("NemesisLooting"); }
            catch { return true; } // Default ON if GenContext unavailable
        }
    }
}
