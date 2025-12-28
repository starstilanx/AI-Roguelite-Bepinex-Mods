using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Reflection;
using System.Threading.Tasks;

namespace AIROG_NPCExpansion
{
    public class NPCEquipmentUI : MonoBehaviour
    {
        public static NPCEquipmentUI Instance { get; private set; }

        public GameObject _window;
        public GameCharacter _currentNpc;
        private GameplayManager _manager;

        private TextMeshProUGUI _defenseText;
        private Transform _npcInvGrid;
        private TextMeshProUGUI _titleText;
        private GameObject _divider;
        
        public static GameItem PendingGiftItem;
        public static GameCharacter PendingGiftNpc;

        public static void Init()
        {
            // Optional: Pre-warm if needed
        }

        public static void OpenFor(GameCharacter npc, GameplayManager manager)
        {
            if (Instance == null)
            {
                var obj = new GameObject("NPCEquipmentUI");
                Instance = obj.AddComponent<NPCEquipmentUI>();
            }
            Instance.Show(npc, manager);
        }

        private void Show(GameCharacter npc, GameplayManager manager)
        {
            Debug.Log($"[AIROG_NPCExpansion] Opening NPCEquipmentUI for {npc?.GetPrettyName() ?? "null"}");
            _currentNpc = npc;
            _manager = manager;

            if (_window == null) CreateUI();
            
            _window.SetActive(true);
            _window.transform.SetAsLastSibling(); // Ensure it's on top
            Refresh();
        }

        private void CreateUI()
        {
            Debug.Log("[AIROG_NPCExpansion] Creating NPCEquipmentUI Root");
            // We want to create a window that looks like the game's UI
            _window = new GameObject("NPCGearWindow", typeof(RectTransform));
            _window.transform.SetParent(_manager.canvasTransform, false);
            
            // Explicitly force to front
            var rect = _window.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1000, 700); 
            rect.anchoredPosition = Vector2.zero;
            // 1. Background (Parchment) - Base layer for the whole window
            var bgObj = new GameObject("Background", typeof(RectTransform));
            bgObj.transform.SetParent(_window.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;
            var baseBg = bgObj.AddComponent<RawImage>();
            if (_manager.textureStuff != null && _manager.textureStuff.playerStatsBkgd != null)
            {
                var playerBg = _manager.textureStuff.playerStatsBkgd.GetComponent<RawImage>();
                if (playerBg != null) { baseBg.texture = playerBg.texture; baseBg.color = playerBg.color; }
            }
            else { baseBg.color = new Color(0.85f, 0.8f, 0.7f, 1f); }

            // 2. Dark Sidebar (Gear Section) - Left layer overlay
            var gearBgObj = new GameObject("GearSideBg", typeof(RectTransform));
            gearBgObj.transform.SetParent(_window.transform, false);
            var gearSideRect = gearBgObj.GetComponent<RectTransform>();
            gearSideRect.anchorMin = Vector2.zero;
            gearSideRect.anchorMax = new Vector2(0.42f, 1);
            gearSideRect.sizeDelta = Vector2.zero;
            var gearBg = gearBgObj.AddComponent<Image>();
            gearBg.color = new Color(0.12f, 0.1f, 0.08f, 1f); // Solid dark brown matching doll area
            
            // 3. Window Frame (Metallic) - Border overlay
            if (_manager.textureStuff != null)
            {
                var frameObj = new GameObject("Frame", typeof(RectTransform));
                frameObj.transform.SetParent(_window.transform, false);
                var frameRect = frameObj.GetComponent<RectTransform>();
                frameRect.anchorMin = Vector2.zero;
                frameRect.anchorMax = Vector2.one;
                frameRect.sizeDelta = Vector2.zero; // Full border
                
                var playerFrameImg = _manager.textureStuff.invFrame?.GetComponent<Image>();
                if (playerFrameImg != null)
                {
                    var frameImg = frameObj.AddComponent<Image>();
                    frameImg.sprite = playerFrameImg.sprite;
                    frameImg.type = playerFrameImg.type;
                    frameImg.color = new Color(1, 1, 1, 0.8f);
                }
                else
                {
                    var frameRaw = frameObj.AddComponent<RawImage>();
                    var pfr = _manager.textureStuff.invFrame?.GetComponent<RawImage>();
                    if (pfr != null) 
                    { 
                        frameRaw.texture = pfr.texture; 
                        frameRaw.color = new Color(1, 1, 1, 0.5f); 
                    }
                    else
                    {
                        // Final fallback if no frame texture found
                        frameRaw.color = new Color(0, 0, 0, 0.5f);
                    }
                }
            }

            // 4. Title (NPC Name)
            var titleObj = new GameObject("Title", typeof(RectTransform));
            titleObj.transform.SetParent(_window.transform, false);
            _titleText = titleObj.AddComponent<TextMeshProUGUI>();
            _titleText.text = _currentNpc != null ? _currentNpc.GetPrettyName() : "NPC Gear"; 
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.fontSize = 28;
            _titleText.color = new Color(0.15f, 0.1f, 0.05f, 1f); // Dark ink
            if (_manager.currentPlaceText != null) _titleText.font = _manager.currentPlaceText.font;
            
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.42f, 1); // Start at split
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.anchoredPosition = new Vector2(0, -45);
            titleRect.sizeDelta = new Vector2(0, 50);

            // 5. Close Button
            var closeBtnObj = new GameObject("CloseBtn", typeof(RectTransform));
            closeBtnObj.transform.SetParent(_window.transform, false);
            var closeImg = closeBtnObj.AddComponent<Image>();
            closeImg.color = new Color(0.9f, 0.1f, 0.1f, 1f);
            var closeBtn = closeBtnObj.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            closeBtn.onClick.AddListener(() => _window.SetActive(false));
            var closeRect = closeBtnObj.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1, 1);
            closeRect.anchorMax = new Vector2(1, 1);
            closeRect.anchoredPosition = new Vector2(-40, -40);
            closeRect.sizeDelta = new Vector2(35, 35);
            
            var closeTxt = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            closeTxt.transform.SetParent(closeBtnObj.transform, false);
            closeTxt.text = "X";
            closeTxt.fontSize = 20;
            closeTxt.alignment = TextAlignmentOptions.Center;
            closeTxt.color = Color.white;
            closeTxt.raycastTarget = false;
            closeTxt.rectTransform.sizeDelta = new Vector2(35, 35);

            // 6. Divider Line
            _divider = new GameObject("Divider", typeof(RectTransform));
            _divider.transform.SetParent(_window.transform, false);
            var divImg = _divider.AddComponent<Image>();
            divImg.color = new Color(0.1f, 0.08f, 0.05f, 0.3f);
            var divRect = _divider.GetComponent<RectTransform>();
            divRect.anchorMin = new Vector2(0.42f, 0.05f); 
            divRect.anchorMax = new Vector2(0.42f, 0.95f);
            divRect.sizeDelta = new Vector2(1, 0);

            // 7. Content Panels
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(_window.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = new Vector2(40, 40);
            contentRect.offsetMax = new Vector2(-40, -100);

            // Left: NPC Equipment Section
            var eqSection = new GameObject("EqSection", typeof(RectTransform));
            eqSection.transform.SetParent(content.transform, false);
            var eqRect = eqSection.GetComponent<RectTransform>();
            eqRect.anchorMin = Vector2.zero;
            eqRect.anchorMax = new Vector2(0.42f, 1); // 42% split matches frame texture
            eqRect.offsetMin = Vector2.zero;
            eqRect.offsetMax = Vector2.zero;

            var eqTitle = new GameObject("Title", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            eqTitle.transform.SetParent(eqSection.transform, false);
            eqTitle.text = "Gear";
            eqTitle.fontSize = 20;
            eqTitle.alignment = TextAlignmentOptions.Center;
            eqTitle.color = new Color(0.8f, 0.7f, 0.5f, 0.8f);
            if (_titleText != null) eqTitle.font = _titleText.font;
            eqTitle.rectTransform.anchorMin = new Vector2(0.5f, 1);
            eqTitle.rectTransform.anchorMax = new Vector2(0.5f, 1);
            eqTitle.rectTransform.anchoredPosition = new Vector2(0, -15);
            eqTitle.rectTransform.sizeDelta = new Vector2(200, 30);

            CreateEquipmentSlots(eqSection.transform);

            // Right: NPC Inventory Section
            var invSection = new GameObject("InvSection", typeof(RectTransform));
            invSection.transform.SetParent(content.transform, false);
            var invRect = invSection.GetComponent<RectTransform>();
            invRect.anchorMin = new Vector2(0.42f, 0); // Match 42% split
            invRect.anchorMax = Vector2.one;
            invRect.offsetMin = new Vector2(5, 0); 
            invRect.offsetMax = Vector2.zero;

            var invTitle = new GameObject("Title", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            invTitle.transform.SetParent(invSection.transform, false);
            invTitle.text = "Held Items";
            invTitle.fontSize = 20;
            invTitle.color = new Color(0.2f, 0.15f, 0.1f, 0.8f);
            if (_titleText != null) invTitle.font = _titleText.font;
            invTitle.rectTransform.anchorMin = new Vector2(0.5f, 1);
            invTitle.rectTransform.anchorMax = new Vector2(0.5f, 1);
            invTitle.rectTransform.anchoredPosition = new Vector2(0, -15);
            invTitle.rectTransform.sizeDelta = new Vector2(200, 30);

            _npcInvGrid = new GameObject("Grid", typeof(RectTransform)).transform;
            _npcInvGrid.SetParent(invSection.transform, false);
            var gridRect = _npcInvGrid.GetComponent<RectTransform>();
            gridRect.anchorMin = Vector2.zero;
            gridRect.anchorMax = new Vector2(1, 1);
            gridRect.offsetMin = new Vector2(0, 10);
            gridRect.offsetMax = new Vector2(-20, -50);
            
            var glg = _npcInvGrid.gameObject.AddComponent<GridLayoutGroup>();
            glg.cellSize = new Vector2(75, 75);
            glg.spacing = new Vector2(10, 10);
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = 5; // 5 columns on the right

        }

        private void CreateEquipmentSlots(Transform parent)
        {
            var container = new GameObject("SlotsContainer", typeof(RectTransform));
            container.transform.SetParent(parent, false);
            var rect = container.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            // Manual layout matching DOLL arrangement - narrow enough for the 35% side
            float centerX = 0;
            float centerY = 15;

            var positions = new Dictionary<string, Vector2>
            {
                { "HEAD",     new Vector2(centerX,       centerY + 160) },
                { "NECKLACE", new Vector2(centerX - 90,  centerY + 120) },
                { "FACE",     new Vector2(centerX + 90,  centerY + 120) },
                { "TORSO",    new Vector2(centerX,       centerY + 50) },
                { "WEAPON1",  new Vector2(centerX - 115, centerY - 25) },
                { "WEAPON2",  new Vector2(centerX + 115, centerY - 25) },
                { "PANTS",    new Vector2(centerX,       centerY - 75) },
                { "RING",     new Vector2(centerX - 90,  centerY - 135) },
                { "GLOVES",   new Vector2(centerX + 90,  centerY - 135) },
                { "BOOTS",    new Vector2(centerX,       centerY - 200) }
            };
            
            // Adjust slot size slightly smaller for the narrow side
            float slotSize = 75;

            foreach (var kvp in positions)
            {
                var obj = InstSlot(kvp.Key, container.transform);
                var sRect = obj.GetComponent<RectTransform>();
                sRect.anchorMin = new Vector2(0.5f, 0.5f);
                sRect.anchorMax = new Vector2(0.5f, 0.5f);
                sRect.anchoredPosition = kvp.Value;
                sRect.sizeDelta = new Vector2(slotSize, slotSize);
                
                // Steal placeholder icon
                TrySetPlaceholder(obj.GetComponent<ItemSlot>(), kvp.Key);
            }

            CreateDefenseDisplay(container.transform);
        }

        private void TrySetPlaceholder(ItemSlot slot, string slotType)
        {
            if (_manager.equipmentPanel == null || slot.placeholderImg == null) return;
            
            ItemSlot playerSlot = null;
            switch(slotType)
            {
                case "HEAD": playerSlot = _manager.equipmentPanel.headSlot; break;
                case "TORSO": playerSlot = _manager.equipmentPanel.torsoSlot; break;
                case "WEAPON1": playerSlot = _manager.equipmentPanel.weapon1Slot; break;
                case "WEAPON2": playerSlot = _manager.equipmentPanel.weapon2Slot; break;
                case "GLOVES": playerSlot = _manager.equipmentPanel.glovesSlot; break;
                case "BOOTS": playerSlot = _manager.equipmentPanel.bootsSlot; break;
                case "FACE": playerSlot = _manager.equipmentPanel.faceSlot; break;
                case "NECKLACE": playerSlot = _manager.equipmentPanel.necklaceSlot; break;
                case "RING": playerSlot = _manager.equipmentPanel.ringSlot; break;
                case "PANTS": playerSlot = _manager.equipmentPanel.pantsSlot; break;
            }
            
            if (playerSlot != null && playerSlot.placeholderImg != null)
            {
                slot.placeholderImg.texture = playerSlot.placeholderImg.texture;
                slot.placeholderImg.color = new Color(1, 1, 1, 0.3f); // Faded placeholder
                slot.placeholderImg.gameObject.SetActive(true);
            }
        }

        private void CreateDefenseDisplay(Transform parent)
        {
            var defObj = new GameObject("DefenseDisplay", typeof(RectTransform));
            defObj.transform.SetParent(parent, false);
            var rect = defObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0);
            rect.anchorMax = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, 50);
            rect.sizeDelta = new Vector2(150, 40);
            
            var iconObj = new GameObject("Icon", typeof(RectTransform));
            iconObj.transform.SetParent(defObj.transform, false);
            var iconImg = iconObj.AddComponent<RawImage>();
            if (_manager.playerCharacter != null && _manager.playerCharacter.dmgProtStat != null)
            {
                var playerIcon = _manager.playerCharacter.dmgProtStat.GetComponentInChildren<RawImage>();
                if (playerIcon != null) iconImg.texture = playerIcon.texture;
                else
                {
                    var playerIconFallback = _manager.playerCharacter.dmgProtStat.GetComponentInChildren<Image>();
                    if (playerIconFallback != null && playerIconFallback.sprite != null) iconImg.texture = playerIconFallback.sprite.texture;
                }
            }
            var iconRect = iconObj.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0, 0.5f);
            iconRect.anchorMax = new Vector2(0, 0.5f);
            iconRect.anchoredPosition = new Vector2(25, 0);
            iconRect.sizeDelta = new Vector2(30, 30);
            
            var textObj = new GameObject("Text", typeof(RectTransform));
            textObj.transform.SetParent(defObj.transform, false);
            _defenseText = textObj.AddComponent<TextMeshProUGUI>();
            _defenseText.text = "0.0%";
            _defenseText.fontSize = 24;
            _defenseText.color = new Color(0.2f, 0.15f, 0.1f, 1f);
            _defenseText.alignment = TextAlignmentOptions.Left;
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0, 0.5f);
            textRect.anchorMax = new Vector2(1, 0.5f);
            textRect.anchoredPosition = new Vector2(65, 0);
            textRect.sizeDelta = new Vector2(0, 30);
        }

        private void UpdateSlot(ItemSlot slot, GameItem item)
        {
            if (slot == null) return;
            
            slot.item = item;
            if (item == null)
            {
                // Deactivate all standard ItemSlot elements
                if (slot.itemTitle != null) slot.itemTitle.SetActive(false);
                if (slot.itemImg != null) slot.itemImg.SetActive(false);
                if (slot.favStar != null) ((Component)slot.favStar).gameObject.SetActive(false);
                if (slot.circleImg != null) ((Component)slot.circleImg).gameObject.SetActive(false);
                if (slot.eqIconImg != null) ((Component)slot.eqIconImg).gameObject.SetActive(false);
                if (slot.itemTitleBkg != null) ((Graphic)slot.itemTitleBkg).color = new Color(0, 0, 0, 0);
                
                // Show placeholder (if any)
                if (slot.placeholderImg != null)
                {
                    ((Component)slot.placeholderImg).gameObject.SetActive(true);
                }
            }
            else
            {
                // Standard elements back on
                if (slot.itemTitle != null) slot.itemTitle.SetActive(true);
                if (slot.itemImg != null) slot.itemImg.SetActive(true);
                if (slot.placeholderImg != null) ((Component)slot.placeholderImg).gameObject.SetActive(false);
                
                slot.UpdateDisplay();
            }
        }

        private GameObject InstSlot(string slotType, Transform parent)
        {
            var inv = _manager.inventory;
            var itemSlotPrefabField = typeof(InfiniteInventory).GetField("itemSlotPrefab", BindingFlags.NonPublic | BindingFlags.Instance);
            GameObject prefab = (GameObject)itemSlotPrefabField.GetValue(inv);

            var obj = Instantiate(prefab, parent, false);
            obj.name = "Slot_" + slotType;
            
            var slot = obj.GetComponent<ItemSlot>();
            slot.manager = _manager;
            UpdateSlot(slot, null);
            
            var btn = obj.GetComponent<Button>();
            if (btn == null) btn = obj.AddComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnEqSlotClicked(slotType));

            return obj;
        }

        private void OnEqSlotClicked(string slotType)
        {
            var data = NPCData.Load(_currentNpc.uuid);
            if (data == null) return;

            if (data.EquippedUuids.TryGetValue(slotType, out string _))
            {
                data.EquippedUuids.Remove(slotType);
                NPCData.Save(_currentNpc.uuid, data);
                Refresh();
            }
        }

        private void Refresh()
        {
            if (_window == null || _currentNpc == null) return;

            var data = NPCData.Load(_currentNpc.uuid);
            if (data == null) data = NPCData.CreateDefault(_currentNpc.GetPrettyName());

            var inv = _manager.inventory;
            var itemSlotPrefabField = typeof(InfiniteInventory).GetField("itemSlotPrefab", BindingFlags.NonPublic | BindingFlags.Instance);
            GameObject prefab = (GameObject)itemSlotPrefabField.GetValue(inv);

            // 0. Update Title
            if (_titleText != null) _titleText.text = _currentNpc.GetPrettyName();

            // 1. NPC Inventory Grid
            foreach (Transform child in _npcInvGrid) Destroy(child.gameObject);

            foreach (var item in _currentNpc.items)
            {
                bool isEquipped = data.EquippedUuids.Values.Contains(item.uuid);
                if (isEquipped) continue;

                var slotObj = Instantiate(prefab, _npcInvGrid, false);
                var slot = slotObj.GetComponent<ItemSlot>();
                slot.manager = _manager;
                UpdateSlot(slot, item);
                
                var btn = slotObj.GetComponent<Button>();
                if (btn == null) btn = slotObj.AddComponent<Button>();
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => OnInvItemClicked(item));
            }

            // 2. Equipment Slots
            UpdateEquipmentSlotDisplay(data);

            // 3. Defense
            UpdateDefenseDisplay(data);
        }

        private void UpdateDefenseDisplay(NPCData data)
        {
            if (_defenseText == null) return;
            
            double totalProt = 0;
            int baselineLvl = _manager.playerCharacter.playerLevel;
            
            foreach (var uuid in data.EquippedUuids.Values)
            {
                var item = _currentNpc.items.Find(i => i.uuid == uuid);
                if (item != null) totalProt += Utils.GetDmgProtForItem(item, baselineLvl);
            }
            
            _defenseText.text = (totalProt * 100).ToString("F1") + "%";
        }

        private void UpdateEquipmentSlotDisplay(NPCData data)
        {
            string[] slots = { "HEAD", "TORSO", "WEAPON1", "WEAPON2", "GLOVES", "BOOTS", "FACE", "NECKLACE", "RING", "PANTS" };
            foreach (var s in slots)
            {
                Transform slotTrans = _window.transform.Find("Content/EqSection/SlotsContainer/Slot_" + s);
                if (slotTrans == null) continue;

                var slot = slotTrans.GetComponent<ItemSlot>();
                GameItem item = null;
                if (data.EquippedUuids.TryGetValue(s, out string uuid))
                {
                    item = _currentNpc.items.Find(i => i.uuid == uuid);
                }
                UpdateSlot(slot, item);
            }
        }

        private void OnInvItemClicked(GameItem item)
        {
            var data = NPCData.Load(_currentNpc.uuid);
            if (data == null) data = NPCData.CreateDefault(_currentNpc.GetPrettyName());

            string slotType = GetSlotTypeForItem(item);
            if (slotType != null)
            {
                data.EquippedUuids[slotType] = item.uuid;
                NPCData.Save(_currentNpc.uuid, data);
                Refresh();
            }
        }

        private string GetSlotTypeForItem(GameItem item)
        {
            switch (item.equipmentType)
            {
                case EquipmentPanel.EquipmentType.HEAD: return "HEAD";
                case EquipmentPanel.EquipmentType.TORSO: return "TORSO";
                case EquipmentPanel.EquipmentType.GLOVES: return "GLOVES";
                case EquipmentPanel.EquipmentType.BOOTS: return "BOOTS";
                case EquipmentPanel.EquipmentType.FACE: return "FACE";
                case EquipmentPanel.EquipmentType.NECKLACE: return "NECKLACE";
                case EquipmentPanel.EquipmentType.RING: return "RING";
                case EquipmentPanel.EquipmentType.PANTS: return "PANTS";
                case EquipmentPanel.EquipmentType.WIELDABLE: 
                    var data = NPCData.Load(_currentNpc.uuid);
                    if (data == null || !data.EquippedUuids.ContainsKey("WEAPON1")) return "WEAPON1";
                    return "WEAPON2";
                case EquipmentPanel.EquipmentType.NONE:
                    if (Utils.IsWeapon(item)) return "WEAPON1";
                    return null;
            }
            return null;
        }

        public static async Task GiveItemToNPC(GameItem item, GameCharacter npc, GameplayManager manager)
        {
            if (Instance != null && Instance._window != null)
            {
                Instance._window.SetActive(false);
            }

            // Queue the gift movement to happen AFTER the undo snapshot is taken
            PendingGiftItem = item;
            PendingGiftNpc = npc;

            var data = NPCData.Load(npc.uuid);
            if (data == null) data = NPCData.CreateDefault(npc.GetPrettyName());
            data.ChangeAffinity(5, $"Given {item.GetPrettyName()} to use.");
            NPCData.Save(npc.uuid, data);

            // Trigger Story Turn
            await manager.ProcessInteractionInfo(new InteractionInfo(new InteracterInfo(InteracterInfo.InteracterType.OFFER_ITEM, item), new InteracteeInfo(npc)));

            // The actual transfer happens in the AddSavePointForUndo hook in NPCExpansionPlugin.cs
            // to ensure it is synchronized with the game's undo/redo and save systems.
        }
    }
}
