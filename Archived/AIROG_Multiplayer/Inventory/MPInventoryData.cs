using System;
using System.Collections.Generic;

namespace AIROG_Multiplayer.Inventory
{
    /// <summary>
    /// A lightweight representation of a single item in a player's mod-tracked inventory.
    /// Mirrors the data we care about from GameItem without referencing Unity/game types.
    /// </summary>
    [Serializable]
    public class MPItem
    {
        public string Name = "";
        public string Description = "";
        /// <summary>Common, Uncommon, Rare, Epic, Legendary, Mundane, Trash</summary>
        public string Quality = "Common";
        /// <summary>Wieldable, Head, Torso, Gloves, Boots, Pants, Face, Necklace, Ring, Consumable, Misc, None</summary>
        public string ItemType = "None";
        public int Quantity = 1;
        public long GoldValue = -1;
        public bool IsEquipped;
        /// <summary>Unix timestamp (ms) when item was acquired (game's acquireTime field).</summary>
        public long AcquiredTime;
    }

    /// <summary>
    /// Inventory for a single player (host or client).
    /// </summary>
    [Serializable]
    public class PlayerInventory
    {
        public string PlayerId = "";
        public string CharacterName = "";
        public List<MPItem> Items = new List<MPItem>();
        public long Gold;
        public long LastUpdated;
    }

    /// <summary>
    /// The full multiplayer inventory database — one entry per connected player.
    /// Saved to {saveSubDir}/mp_inventory.json and broadcast as InventorySync packets.
    /// </summary>
    [Serializable]
    public class MPInventoryDatabase
    {
        /// <summary>Key: PlayerId ("host" for the host player, or a short GUID for clients).</summary>
        public Dictionary<string, PlayerInventory> Players = new Dictionary<string, PlayerInventory>();
        public long LastUpdated;
    }
}
