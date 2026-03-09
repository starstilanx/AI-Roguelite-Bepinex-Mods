using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace AIROG_Multiplayer.Inventory
{
    /// <summary>
    /// Manages the multiplayer inventory database: load/save JSON, sync from game, CRUD.
    ///
    /// Architecture:
    ///   - Host: syncs its own entry from GameplayManager on every WriteSaveFile call,
    ///           saves to {saveSubDir}/mp_inventory.json, then broadcasts InventorySync.
    ///   - Client: receives the full DB via InventorySync; saves locally to mp_client/mp_inventory.json.
    ///   - Both: the in-memory _db is the single source of truth; UI reads from it.
    /// </summary>
    public static class MPInventoryManager
    {
        public const string HOST_PLAYER_ID = "host";
        private const string DB_FILENAME = "mp_inventory.json";

        private static MPInventoryDatabase _db = new MPInventoryDatabase();
        private static string _filePath;

        private static ManualLogSource Log => MultiplayerPlugin.Instance?.Log;

        /// <summary>Fired (on the main thread) when the database changes — UI should refresh.</summary>
        public static event Action OnInventoryChanged;

        public static MPInventoryDatabase Database => _db;

        // -----------------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called on StartHost or StartClient.
        /// Sets the JSON file path and loads any existing data.
        /// </summary>
        public static void Initialize(string saveTopLvlDir, string saveSubDir)
        {
            string saveDir = Path.Combine(saveTopLvlDir, saveSubDir);
            try { Directory.CreateDirectory(saveDir); } catch { /* ignore */ }

            _filePath = Path.Combine(saveDir, DB_FILENAME);
            _db = new MPInventoryDatabase(); // Fresh DB — load will populate if file exists
            Load();
        }

        /// <summary>Clears in-memory state and file path (call on disconnect/stop).</summary>
        public static void Reset()
        {
            _db = new MPInventoryDatabase();
            _filePath = null;
        }

        // -----------------------------------------------------------------------
        // Persistence
        // -----------------------------------------------------------------------

        public static void Load()
        {
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
            try
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                var loaded = JsonConvert.DeserializeObject<MPInventoryDatabase>(json);
                if (loaded != null)
                    _db = loaded;
                Log?.LogInfo($"[Inventory] Loaded {_db.Players.Count} player inventories from {_filePath}.");
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[Inventory] Load error: {ex.Message}");
            }
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            try
            {
                _db.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json = JsonConvert.SerializeObject(_db, Formatting.Indented);
                File.WriteAllText(_filePath, json, Encoding.UTF8);
                Log?.LogInfo($"[Inventory] Saved {_db.Players.Count} player inventories.");
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[Inventory] Save error: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // CRUD
        // -----------------------------------------------------------------------

        public static PlayerInventory GetOrCreate(string playerId, string charName = "")
        {
            if (!_db.Players.ContainsKey(playerId))
            {
                _db.Players[playerId] = new PlayerInventory
                {
                    PlayerId = playerId,
                    CharacterName = charName,
                    Items = new List<MPItem>(),
                    Gold = 0,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            else if (!string.IsNullOrEmpty(charName))
            {
                _db.Players[playerId].CharacterName = charName;
            }
            return _db.Players[playerId];
        }

        public static void AddItem(string playerId, MPItem item)
        {
            var inv = GetOrCreate(playerId);
            inv.Items.Add(item);
            inv.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Removes the first item matching the name (case-insensitive). Returns true if found.</summary>
        public static bool RemoveItem(string playerId, string itemName)
        {
            if (!_db.Players.TryGetValue(playerId, out var inv)) return false;
            int idx = inv.Items.FindIndex(i =>
                string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            inv.Items.RemoveAt(idx);
            inv.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        }

        public static void SetGold(string playerId, long gold)
        {
            var inv = GetOrCreate(playerId);
            inv.Gold = gold;
            inv.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // -----------------------------------------------------------------------
        // Host → Game sync
        // -----------------------------------------------------------------------

        /// <summary>
        /// Reads the host's in-game inventory (bag + equipped) and gold into the host's DB entry.
        /// Called from WriteSaveFilePatch.Postfix so data is always fresh after a save.
        /// </summary>
        public static void SyncHostFromGame(GameplayManager manager)
        {
            if (manager == null) return;
            try
            {
                var pc = manager.playerCharacter?.pcGameEntity;
                string charName = pc?.name ?? "Host";
                var inv = GetOrCreate(HOST_PLAYER_ID, charName);
                inv.Items.Clear();

                // Bag items (unequipped)
                var bagItems = manager.inventory?.GetAllItems();
                if (bagItems != null)
                    foreach (var item in bagItems)
                        inv.Items.Add(GameItemToMPItem(item, equipped: false));

                // Equipped items
                var equippedItems = manager.equipmentPanel?.GetItems();
                if (equippedItems != null)
                    foreach (var item in equippedItems)
                        inv.Items.Add(GameItemToMPItem(item, equipped: true));

                inv.Gold = pc?.numGold ?? 0;
                inv.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                Log?.LogInfo($"[Inventory] Synced host: {inv.Items.Count} items, {inv.Gold} gold.");
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[Inventory] SyncHostFromGame error: {ex.Message}");
            }
        }

        private static MPItem GameItemToMPItem(GameItem item, bool equipped)
        {
            return new MPItem
            {
                Name        = item.GetPrettyName() ?? item.name ?? "",
                Description = item.GetPotentiallyNullDescription() ?? "",
                Quality     = item.GetQualityStr(),
                ItemType    = item.equipmentType.ToString(),
                Quantity    = 1,
                GoldValue   = item.goldVal,
                IsEquipped  = equipped,
                AcquiredTime = item.acquireTime
            };
        }

        // -----------------------------------------------------------------------
        // Network serialization
        // -----------------------------------------------------------------------

        /// <summary>Serializes the entire database to compact JSON for network broadcast.</summary>
        public static string SerializeToJson()
        {
            try
            {
                _db.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return JsonConvert.SerializeObject(_db, Formatting.None);
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[Inventory] SerializeToJson error: {ex.Message}");
                return "{}";
            }
        }

        /// <summary>
        /// Replaces the in-memory DB from received JSON.
        /// Fired on the main thread from AIROGClient's OnInventoryReceived handler.
        /// </summary>
        public static bool LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var loaded = JsonConvert.DeserializeObject<MPInventoryDatabase>(json);
                if (loaded == null) return false;
                _db = loaded;
                OnInventoryChanged?.Invoke();
                Log?.LogInfo($"[Inventory] Received sync: {_db.Players.Count} player inventories.");
                return true;
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[Inventory] LoadFromJson error: {ex.Message}");
                return false;
            }
        }
    }
}
