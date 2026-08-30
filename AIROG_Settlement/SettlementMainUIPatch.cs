using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace AIROG_Settlement
{
    // Builds the HUD button and the settlement modal (background, tabs, sidebar, overview
    // tab content) the first time MainLayouts initializes. Split out of SettlementPlugin.cs.
    [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
    public static class Patch_MainLayouts_InitCommonAnchs
    {
        public static void Postfix(MainLayouts __instance)
        {
            SettlementPlugin.Log.LogInfo("MainLayouts.InitCommonAnchs Postfix: Injecting Settlement UI");
            CreateSettlementUI(__instance);
        }

        private static void CreateSettlementUI(MainLayouts layout)
        {
            if (SettlementPlugin.Instance.SettlementButtonSprite == null)
            {
                SettlementPlugin.Log.LogError("Sprites not loaded, cannot create UI");
                return;
            }

            // ---- HUD Button ----
            Transform parent = layout.buttonsHolderHolder;
            if (parent == null)
            {
                SettlementPlugin.Log.LogError("buttonsHolderHolder is NULL");
                return;
            }
            if (parent.Find("SettlementButton") != null) return;

            GameObject btnObj = new GameObject("SettlementButton", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            btnObj.GetComponent<Image>().sprite = SettlementPlugin.Instance.SettlementButtonSprite;
            btnObj.GetComponent<Image>().preserveAspect = true;
            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 60;
            le.preferredHeight = le.minHeight = 60;
            btnObj.GetComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.ToggleSettlementView());
            btnObj.transform.SetAsLastSibling();
            SettlementPlugin.Instance.SettlementButtonObj = btnObj;

            // ---- Modal Root ----
            if (layout.mainHolder.Find("SettlementModal") != null)
            {
                SettlementPlugin.Instance.SettlementModalObj = layout.mainHolder.Find("SettlementModal").gameObject;
                return;
            }

            GameObject modalObj = new GameObject("SettlementModal", typeof(RectTransform));
            modalObj.transform.SetParent(layout.mainHolder, false);
            var modalRect = modalObj.GetComponent<RectTransform>();
            modalRect.anchorMin = modalRect.anchorMax = new Vector2(0.5f, 0.5f);
            modalRect.sizeDelta = new Vector2(SettlementUIHelper.CANVAS_WIDTH, SettlementUIHelper.CANVAS_HEIGHT);

            // Layer 1: opaque meadow background
            SettlementUIHelper.CreateUIElement("Background", modalObj.transform,
                0, 0, 1024, 559, SettlementPlugin.Instance.SettlementBkgSprite);

            // Layer 2: UI chrome (tabs, center frame border, right drawer panel)
            SettlementUIHelper.CreateUIElement("Frame", modalObj.transform,
                0, 0, 1024, 559, SettlementPlugin.Instance.SettlementUISprite);

            // ---- Tab buttons ----
            for (int i = 0; i < 5; i++)
            {
                var tr = SettlementUIHelper.Slots.TopTab(i);
                int idx = i;
                GameObject tabBtn = SettlementUIHelper.CreateUIElement($"Tab_{i}", modalObj.transform,
                    tr.x, tr.y, tr.width, tr.height, SettlementPlugin.Instance.TabSprites[i], Color.white);
                var tabImg = tabBtn.GetComponent<Image>();
                tabImg.type = Image.Type.Simple;
                tabImg.preserveAspect = true;
                tabBtn.AddComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.SwitchTab(idx));
            }

            // ---- Sidebar slot overlays (nearly transparent) ----
            for (int i = 0; i < 10; i++)
            {
                var r = SettlementUIHelper.Slots.LeftSidebarItem(i);
                SettlementUIHelper.CreateUIElement($"SB_L_{i}", modalObj.transform,
                    r.x, r.y, r.width, r.height, null, new Color(1, 1, 1, 0.02f));
            }
            for (int i = 0; i < 5; i++)
            {
                var r = SettlementUIHelper.Slots.RightSidebarItem(i);
                SettlementUIHelper.CreateUIElement($"SB_R_{i}", modalObj.transform,
                    r.x, r.y, r.width, r.height, null, new Color(1, 1, 1, 0.02f));
            }

            // ---- Persistent: settlement name above the center frame ----
            // Center frame starts at y=124; name sits in the 27px gap below the tabs (tabs end ~y=103)
            GameObject nameObj = new GameObject("SettlementName", typeof(RectTransform), typeof(TextMeshProUGUI));
            nameObj.transform.SetParent(modalObj.transform, false);
            SettlementPlugin.Instance.OverviewNameText = nameObj.GetComponent<TextMeshProUGUI>();
            SettlementPlugin.Instance.OverviewNameText.text = "Settlement";
            SettlementPlugin.Instance.OverviewNameText.fontSize = 18;
            SettlementPlugin.Instance.OverviewNameText.fontStyle = FontStyles.Bold;
            SettlementPlugin.Instance.OverviewNameText.alignment = TextAlignmentOptions.Center;
            SettlementPlugin.Instance.OverviewNameText.color = new Color(0.98f, 0.92f, 0.72f);
            SettlementPlugin.Instance.OverviewNameText.outlineWidth = 0.2f;
            SettlementPlugin.Instance.OverviewNameText.outlineColor = Color.black;
            // x=316, y=97, w=390, h=25 — sits in the gap between tabs and center frame
            SettlementUIHelper.SetRect(nameObj.GetComponent<RectTransform>(), 316, 97, 390, 25);

            // ---- Persistent: resources in right sidebar slots 0-2 ----
            // Slot dimensions: x=826, y=106+(slot*45), w=164, h=42
            SettlementPlugin.Instance.GoldText  = CreateSidebarText(modalObj.transform, "SidebarGold",
                826, 128, 164, 42, GoldIcon: SettlementPlugin.Instance.GoldIcon);
            SettlementPlugin.Instance.WoodText  = CreateSidebarText(modalObj.transform, "SidebarWood",
                826, 173, 164, 42, GoldIcon: SettlementPlugin.Instance.WoodIcon);
            SettlementPlugin.Instance.StoneText = CreateSidebarText(modalObj.transform, "SidebarStone",
                826, 218, 164, 42, GoldIcon: SettlementPlugin.Instance.StoneIcon);
            SettlementPlugin.Instance.PopulationText = CreateSidebarText(modalObj.transform, "SidebarPop",
                826, 263, 164, 42);
            SettlementPlugin.Instance.KnowledgeText = CreateSidebarText(modalObj.transform, "SidebarKnowledge",
                826, 308, 164, 42);

            // ---- Tab Content objects ----
            SettlementPlugin.Instance.TabContentObjects.Clear();
            for (int i = 0; i < 5; i++)
            {
                GameObject tabContent = new GameObject($"TabContent_{i}", typeof(RectTransform));
                tabContent.transform.SetParent(modalObj.transform, false);
                SettlementUIHelper.SetRect(tabContent.GetComponent<RectTransform>(), 0, 0, 1024, 559);
                tabContent.SetActive(i == SettlementPlugin.Instance.SelectedTab);
                SettlementPlugin.Instance.TabContentObjects.Add(tabContent);

                if (i == 0) BuildOverviewTabContent(tabContent.transform);
                // Tab 1 (Buildings): populated on demand by RefreshBuildingsTab()
                // Tabs 2-4: reserved
            }

            // ---- Close button ----
            GameObject closeBtn = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
            closeBtn.transform.SetParent(modalObj.transform, false);
            closeBtn.GetComponent<Image>().color = Color.clear;
            closeBtn.GetComponent<Button>().onClick.AddListener(() => SettlementPlugin.Instance.ToggleSettlementView());
            SettlementUIHelper.SetRect(closeBtn.GetComponent<RectTransform>(), 967, 10, 50, 50);

            GameObject xObj = new GameObject("X", typeof(RectTransform), typeof(TextMeshProUGUI));
            xObj.transform.SetParent(closeBtn.transform, false);
            var xTxt = xObj.GetComponent<TextMeshProUGUI>();
            xTxt.text = "X"; xTxt.fontSize = 30;
            xTxt.alignment = TextAlignmentOptions.Center;
            xTxt.color = new Color(0.9f, 0.2f, 0.2f);
            xTxt.outlineWidth = 0.2f; xTxt.outlineColor = Color.black;
            var xr = xObj.GetComponent<RectTransform>();
            xr.anchorMin = Vector2.zero; xr.anchorMax = Vector2.one;
            xr.offsetMin = xr.offsetMax = Vector2.zero;

            modalObj.SetActive(false);
            SettlementPlugin.Instance.SettlementModalObj = modalObj;

            CreateEventPopup(layout);

            SettlementPlugin.Instance.UpdateOverviewUI();
        }

        // -----------------------------------------------------------------------
        // Event popup: a small backdrop + panel built once, independent of the Settlement
        // modal (it can be shown whether or not the Settlement UI is open). It's only ever
        // actually shown once the player is standing at the settlement's location though —
        // see SettlementPlugin.Update()/IsPlayerAtSettlement() — so it never interrupts the
        // player somewhere else in the story. Choice buttons are (re)built per event by
        // SettlementPlugin.ShowEventPopup.
        // -----------------------------------------------------------------------
        private static void CreateEventPopup(MainLayouts layout)
        {
            if (layout.mainHolder.Find("SettlementEventPopup") != null)
            {
                SettlementPlugin.Instance.EventPopupObj = layout.mainHolder.Find("SettlementEventPopup").gameObject;
                return;
            }

            GameObject popupObj = new GameObject("SettlementEventPopup", typeof(RectTransform));
            popupObj.transform.SetParent(layout.mainHolder, false);
            SettlementUIHelper.SetRect(popupObj.GetComponent<RectTransform>(), 0, 0, 1024, 559);

            // Backdrop — blocks clicks to whatever is behind it (default Image raycastTarget)
            // and forces a choice; no button/close, deliberately.
            SettlementUIHelper.CreateUIElement("Backdrop", popupObj.transform,
                0, 0, 1024, 559, null, new Color(0, 0, 0, 0.6f));

            SettlementUIHelper.CreateUIElement("Panel", popupObj.transform,
                262, 150, 500, 270, null, new Color(0.06f, 0.06f, 0.10f, 0.97f));

            GameObject titleObj = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI));
            titleObj.transform.SetParent(popupObj.transform, false);
            var titleTxt = titleObj.GetComponent<TextMeshProUGUI>();
            titleTxt.fontSize = 20;
            titleTxt.fontStyle = FontStyles.Bold;
            titleTxt.alignment = TextAlignmentOptions.Center;
            titleTxt.color = new Color(0.95f, 0.85f, 0.5f);
            titleTxt.outlineWidth = 0.15f;
            titleTxt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(titleObj.GetComponent<RectTransform>(), 262, 164, 500, 28);
            SettlementPlugin.Instance.EventPopupTitleText = titleTxt;

            GameObject flavorObj = new GameObject("Flavor", typeof(RectTransform), typeof(TextMeshProUGUI));
            flavorObj.transform.SetParent(popupObj.transform, false);
            var flavorTxt = flavorObj.GetComponent<TextMeshProUGUI>();
            flavorTxt.fontSize = 13;
            flavorTxt.alignment = TextAlignmentOptions.Center;
            flavorTxt.color = new Color(0.82f, 0.82f, 0.82f);
            SettlementUIHelper.SetRect(flavorObj.GetComponent<RectTransform>(), 284, 196, 456, 68);
            SettlementPlugin.Instance.EventPopupFlavorText = flavorTxt;

            popupObj.SetActive(false);
            SettlementPlugin.Instance.EventPopupObj = popupObj;
        }

        // -----------------------------------------------------------------------
        // Overview tab — settlement image fills the center frame interior exactly.
        // Frame inner bounds (measured from SettlementUI.png): x=315, y=133, w=387, h=306
        // -----------------------------------------------------------------------
        private static void BuildOverviewTabContent(Transform parent)
        {
            // Settlement image — fills center frame interior
            GameObject imgObj = new GameObject("SettlementImage", typeof(RectTransform), typeof(RawImage));
            imgObj.transform.SetParent(parent, false);
            SettlementUIHelper.SetRect(imgObj.GetComponent<RectTransform>(), 315, 133, 387, 306);
            SettlementPlugin.Instance.SettlementImageDisplay = imgObj.GetComponent<RawImage>();
            SettlementPlugin.Instance.SettlementImageDisplay.color = new Color(0, 0, 0, 0.4f);

            // Regenerate button — bottom-right corner just below the frame
            GameObject regBtn = SettlementUIHelper.CreateUIElement("RegenerateButton", parent,
                578, 447, 124, 30, null, new Color(0.08f, 0.08f, 0.08f, 0.75f));
            regBtn.AddComponent<Button>().onClick.AddListener(
                () => SettlementPlugin.Instance.TriggerImageGeneration());

            GameObject regTxt = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
            regTxt.transform.SetParent(regBtn.transform, false);
            var rTxt = regTxt.GetComponent<TextMeshProUGUI>();
            rTxt.text = "Regenerate Image"; rTxt.fontSize = 12;
            rTxt.alignment = TextAlignmentOptions.Center; rTxt.color = Color.white;
            var rtr = regTxt.GetComponent<RectTransform>();
            rtr.anchorMin = Vector2.zero; rtr.anchorMax = Vector2.one;
            rtr.offsetMin = rtr.offsetMax = Vector2.zero;
        }

        // Creates a text element sized to fit inside a right-sidebar slot.
        // The slot's wooden drawer texture (from SettlementUI.png) provides the visual frame.
        // Optional icon is placed on the left if the sprite is not null.
        private static TextMeshProUGUI CreateSidebarText(Transform parent, string name,
            float slotX, float slotY, float slotW, float slotH, Sprite GoldIcon = null)
        {
            // Optional icon (left edge of slot)
            if (GoldIcon != null)
            {
                GameObject iconObj = new GameObject(name + "_Icon", typeof(RectTransform), typeof(Image));
                iconObj.transform.SetParent(parent, false);
                iconObj.GetComponent<Image>().sprite = GoldIcon;
                iconObj.GetComponent<Image>().preserveAspect = true;
                // 24×24 icon, vertically centered in slot, 4px from left
                SettlementUIHelper.SetRect(iconObj.GetComponent<RectTransform>(),
                    slotX + 4, slotY + (slotH - 24f) * 0.5f, 24, 24);
            }

            float textOffsetX = GoldIcon != null ? 32f : 4f;

            GameObject txtObj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            txtObj.transform.SetParent(parent, false);
            var txt = txtObj.GetComponent<TextMeshProUGUI>();
            txt.text = "—";
            txt.fontSize = 15;
            txt.fontStyle = FontStyles.Bold;
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            txt.color = Color.white;
            txt.outlineWidth = 0.15f;
            txt.outlineColor = Color.black;
            SettlementUIHelper.SetRect(txtObj.GetComponent<RectTransform>(),
                slotX + textOffsetX, slotY, slotW - textOffsetX - 4f, slotH);
            return txt;
        }
    }
}
