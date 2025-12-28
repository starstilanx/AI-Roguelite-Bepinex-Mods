using UnityEngine;
using UnityEngine.UI;
using TMPro;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;

namespace AIROG_WorldExpansion
{
    public static class WorldLoreExpansion
    {
        public static string CurrentCategory = "All";
        private static GameObject _tabsHolder;
        private static bool _isInitialized = false;

        public static void RecordHistoricalEvent(GameplayManager manager, string desc, string category, List<string> keys = null)
        {
            if (manager == null || manager.GetCurrentUniverse() == null) return;
            var universe = manager.GetCurrentUniverse();
            if (universe.lorebook == null) universe.lorebook = new Lorebook();

            // Create lore entry
            Lorebook.LoreEntry entry = new Lorebook.LoreEntry();
            entry.val = $"[Chronicle: Turn {WorldData.CurrentState.CurrentTurn}] {desc}";
            
            if (keys != null)
            {
                foreach (var k in keys) entry.strKeys.Add(k);
            }
            else
            {
                // Basic fallback: use the category as a key or broad keywords
                entry.strKeys.Add(category);
                if (category == "Economy") entry.strKeys.Add("market");
            }

            universe.lorebook.loreEntries.Add(entry);

            // Set category in extra data
            var extra = WorldData.GetLoreExtra(entry);
            extra.Category = category;

            Debug.Log($"[WorldLoreExpansion] Recorded Historical Event: {category} - {desc}");
        }

        [HarmonyPatch(typeof(LorebookView), "Redraw")]
        [HarmonyPrefix]
        public static bool Prefix_LorebookView_Redraw(LorebookView __instance)
        {
            // Custom Redraw Logic to support filtering. 
            // We handle all initialization and cleanup within RedrawWithFiltering to avoid state desync.
            RedrawWithFiltering(__instance);
            return false; // Skip original
        }

        private static void SetupCategoryTabs(LorebookView view)
        {
            if (view == null || view.manager == null) return;

            // Find a good place to put the tabs. Usually above the list (Sibling of Content)
            // Note: If loreEntriesParent is the ScrollView Content, its parent is the Viewport.
            var parent = view.loreEntriesParent.parent;
            
            _tabsHolder = new GameObject("LoreCategoryTabs", typeof(RectTransform));
            _tabsHolder.transform.SetParent(parent, false);
            _tabsHolder.transform.SetAsFirstSibling(); // Render behind content? No, we want on TOP.
            // Wait, standard UI: Last child = Topmost render.
            // But we will set it as LastSibling in RedrawWithFiltering after content update.

            var rt = _tabsHolder.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.sizeDelta = new Vector2(0, 50);
            rt.anchoredPosition = new Vector2(0, 0);

            var hlg = _tabsHolder.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childForceExpandWidth = true;
            hlg.spacing = 10;
            hlg.padding = new RectOffset(10, 10, 5, 5);

            string[] categories = { "All", "General", "History", "Economy", "Bestiary", "Geography", "Factions" };
            foreach (var cat in categories)
            {
                var btnObj = new GameObject("Tab_" + cat, typeof(RectTransform), typeof(Button), typeof(Image));
                btnObj.transform.SetParent(_tabsHolder.transform, false);
                
                var btnImg = btnObj.GetComponent<Image>();
                btnImg.color = new Color(0.2f, 0.2f, 0.25f);

                var btnTextObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                btnTextObj.transform.SetParent(btnObj.transform, false);
                var txt = btnTextObj.GetComponent<TextMeshProUGUI>();
                txt.text = cat;
                txt.fontSize = 18;
                txt.alignment = TextAlignmentOptions.Center;
                txt.color = Color.white;

                var btn = btnObj.GetComponent<Button>();
                string capturedCat = cat;
                btn.onClick.AddListener(() => {
                    CurrentCategory = capturedCat;
                    view.Redraw();
                });

                // Highlight selected
                if (CurrentCategory == cat) btnImg.color = new Color(0.4f, 0.4f, 0.6f);
            }
        }

        private static void RedrawWithFiltering(LorebookView view)
        {
            // 1. Clean up Tabs (Aggressive search by name to prevent leaks)
            if (view.loreEntriesParent != null && view.loreEntriesParent.parent != null)
            {
                var parent = view.loreEntriesParent.parent;
                List<GameObject> tabsToKill = new List<GameObject>();
                foreach (Transform child in parent)
                {
                    if (child.name == "LoreCategoryTabs") tabsToKill.Add(child.gameObject);
                }
                foreach (var t in tabsToKill) Object.Destroy(t);
            }
            _tabsHolder = null; 

            // 2. Setup New Tabs
            SetupCategoryTabs(view);

            // 3. Clean up Content (Entries + Spacer)
            // Use backwards iteration for safer destruction while modifying collection/hierarchy
            for (int i = view.loreEntriesParent.childCount - 1; i >= 0; i--)
            {
                Transform child = view.loreEntriesParent.GetChild(i);
                if (child.name == "HeaderSpacer" || child.GetComponent<UiLoreEntry>() != null)
                {
                    Utils.DestroyWithTexture(child.gameObject);
                }
            }
            
            // Add a spacer to push content down so it doesn't start under the tabs
            GameObject spacer = new GameObject("HeaderSpacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(view.loreEntriesParent, false);
            spacer.GetComponent<LayoutElement>().minHeight = 50; // Match tab height
            spacer.transform.SetAsFirstSibling();

            var universe = view.manager.GetCurrentUniverse();
            if (universe == null || universe.lorebook == null) return;

            foreach (var entry in universe.lorebook.loreEntries)
            {
                var extra = WorldData.GetLoreExtra(entry);
                if (CurrentCategory != "All" && extra.Category != CurrentCategory) continue;

                GameObject obj = Object.Instantiate(view.loreEntryPrefab, view.loreEntriesParent);
                UiLoreEntry ui = obj.GetComponent<UiLoreEntry>();
                ui.lorebookView = view;
                ui.loreEntry = entry;
                ui.keysInput.SetTextWithoutNotify(entry.ToUiKeysStr());
                ui.valInput.SetTextWithoutNotify(entry.val);
                
                InjectExtraUi(ui);
                
                ui.HackyRefreshWordWrapping();
            }

            Utils.DeepRefreshLayout(view.transform);
            view.addBtnsTrans.SetAsLastSibling();
            
            // Ensure tabs are drawn on top of the content (Viewport z-ordering)
            if (_tabsHolder != null) _tabsHolder.transform.SetAsLastSibling();
        }

        private static void InjectExtraUi(UiLoreEntry ui)
        {
            var extra = WorldData.GetLoreExtra(ui.loreEntry);
            
            // Adjust the main value input to make room on the right
            // We assume standard anchoring where offsetMax.x is the right margin.
            // We increase the negative margin (move edge to the left) by 125 pixels (increased to avoid trash btn overlap).
            RectTransform valRt = ui.valInput.GetComponent<RectTransform>();
            if (valRt != null)
            {
                // Check if we haven't already adjusted it (in case of re-runs or pooling, though we destroy/recreate)
                // But since we instantiate new, it's fresh.
                // Standard delete button is usually ~40px. We want space for our 100px container.
                // Let's add -125 to the existing Right offset.
                Vector2 oldOffset = valRt.offsetMax;
                valRt.offsetMax = new Vector2(oldOffset.x - 125f, oldOffset.y);
            }

            // Create a container to hold our buttons
            GameObject toolsContainer = new GameObject("ToolsContainer", typeof(RectTransform));
            toolsContainer.transform.SetParent(ui.transform, false);
            
            RectTransform rt = toolsContainer.GetComponent<RectTransform>();
            // Anchor to Top-Right
            rt.anchorMin = new Vector2(1, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(1, 1);
            // Position: X = -60 (increased buffer for delete button), Y = 0 (top aligned) with a slight offset
            rt.anchoredPosition = new Vector2(-60, -5); 
            rt.sizeDelta = new Vector2(100, 70);

            var vlg = toolsContainer.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.spacing = 5;
            vlg.padding = new RectOffset(0, 0, 0, 0);

            // 1. Category Cycle Button
            GameObject catObj = new GameObject("CategoryBtn", typeof(RectTransform), typeof(Button), typeof(Image));
            catObj.transform.SetParent(toolsContainer.transform, false);
            
            var btnLe = catObj.AddComponent<LayoutElement>();
            btnLe.minHeight = 24;
            btnLe.preferredHeight = 24;

            catObj.GetComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f); // Dark background
            
            var btnTextObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            btnTextObj.transform.SetParent(catObj.transform, false);
            var txt = btnTextObj.GetComponent<TextMeshProUGUI>();
            txt.text = extra.Category;
            txt.fontSize = 12;
            txt.alignment = TextAlignmentOptions.Center;
            
            var btn = catObj.GetComponent<Button>();
            string[] cats = { "General", "History", "Economy", "Bestiary", "Geography", "Factions" };
            btn.onClick.AddListener(() => {
                int idx = System.Array.IndexOf(cats, extra.Category);
                extra.Category = cats[(idx + 1) % cats.Length];
                txt.text = extra.Category;
            });

            // 2. Image Button
            GameObject imgBtnObj = new GameObject("ImageBtn", typeof(RectTransform), typeof(Button), typeof(Image));
            imgBtnObj.transform.SetParent(toolsContainer.transform, false);
             var imgLe = imgBtnObj.AddComponent<LayoutElement>();
            imgLe.minHeight = 24;
            imgLe.preferredHeight = 24;

            imgBtnObj.GetComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f); // Dark background
            
            var imgTxtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            imgTxtObj.transform.SetParent(imgBtnObj.transform, false);
            var itxt = imgTxtObj.GetComponent<TextMeshProUGUI>();
            itxt.text = string.IsNullOrEmpty(extra.ImageUuid) ? "Gen Img" : "Show Img";
            itxt.fontSize = 12;
            itxt.alignment = TextAlignmentOptions.Center;
            
            var ibtn = imgBtnObj.GetComponent<Button>();
            ibtn.onClick.AddListener(() => {
                if (string.IsNullOrEmpty(extra.ImageUuid))
                {
                    GenerateLoreImage(ui, extra, itxt);
                }
                else
                {
                    ShowLoreImage(extra.ImageUuid);
                }
            });
        }

        private static async void GenerateLoreImage(UiLoreEntry ui, LoreExtraData extra, TextMeshProUGUI btnText)
        {
             string prompt = $"{ui.loreEntry.ToUiKeysStr()}: {ui.loreEntry.val}";
             if (prompt.Length > 1000) prompt = prompt.Substring(0, 1000);
             
             Debug.Log($"[WorldLoreExpansion] Generating image for prompt: {prompt} via configured AIAsker pipeline.");
             btnText.text = "Gen...";
             
             if (string.IsNullOrEmpty(extra.ImageUuid)) extra.ImageUuid = System.Guid.NewGuid().ToString();
             string uuid = extra.ImageUuid;

             try 
             {
                 GameplayManager gm = Object.FindObjectOfType<GameplayManager>();
                 if (gm == null) 
                 {
                     Debug.LogError("[WorldLoreExpansion] GameplayManager not found!");
                     btnText.text = "Gen Img";
                     return;
                 }

                 // Create a dummy entity to leverage AIAsker's standard pipeline
                 // We use the uuid we generated/stored
                 var ge = new ThingGameEntity(null, "LoreEntry", prompt, gm, null, false, true, uuid);
                 
                 // UI/AI code expects imgGenInfo
                 if (ge.imgGenInfo == null) 
                     ge.imgGenInfo = new GameEntity.ImgGenInfo(GameEntity.ImgType.REGULAR);

                 // Standard settings: 512x512, 28 steps
                 var imgSettings = new SettingsPojo.EntImgSettings(512, 512, 28, "${prompt}", "text, blurry, watermark, bad anatomy", false);
                 
                 // Use AIAsker.getGeneratedImage with useNewImgFileList: false
                 // This ensures it uses the ge.uuid to form the path in SS.I.saveSubDirAsArg
                 await AIAsker.getGeneratedImage(imgSettings, ge, useNewImgFileList: false);
                 
                 // AIAsker updates ge.imgGenInfo.imgGenState
                 if (ge.imgGenInfo.imgGenState == GameEntity.ImgGenState.FINISHED)
                 {
                    btnText.text = "Show Img";
                 }
                 else
                 {
                    btnText.text = "Gen Img";
                    Debug.LogWarning($"[WorldLoreExpansion] Generation finished with state: {ge.imgGenInfo.imgGenState}");
                    
                    if (ge.imgGenInfo.imgGenState == GameEntity.ImgGenState.WOMBO_FAILED || 
                        ge.imgGenInfo.imgGenState == GameEntity.ImgGenState.REGULAR_FAILED)
                    {
                        throw new System.Exception($"Generation failed with state {ge.imgGenInfo.imgGenState}. Check Player.log for details.");
                    }
                 }
             }
             catch (System.Exception ex)
             {
                 Debug.LogError("[WorldLoreExpansion] Lore image gen failed: " + ex);
                 btnText.text = "Gen Img";
                 Object.FindObjectOfType<GameplayManager>().MessageModal().ShowModal("Image generation failed.\n\nError: " + ex.Message, false, true);
             }
        }

        private static void ShowLoreImage(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            
            // Look in standard save dir
            string path = System.IO.Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, uuid + ".png");
            
            // Backward compatibility: check old custom folder
            if (!System.IO.File.Exists(path))
            {
                string oldFolder = System.IO.Path.Combine(Application.persistentDataPath, "LoreImages");
                string oldPath = System.IO.Path.Combine(oldFolder, uuid + ".png");
                if (System.IO.File.Exists(oldPath)) path = oldPath;
            }

            if (!System.IO.File.Exists(path))
            {
                Object.FindObjectOfType<GameplayManager>().MessageModal().ShowModal("Image file not found.\nIt may not have generated yet.", false, true);
                return;
            }

            // Create simple modal overlay
            GameObject canvas = GameObject.Find("Canvas");
            if (canvas == null) 
            {
                Debug.LogError("Could not find Canvas! Modal will no act right.");
                return;
            }

            GameObject modal = new GameObject("LoreImageModal", typeof(RectTransform), typeof(Image));
            modal.transform.SetParent(canvas.transform, false);
            var modalRt = modal.GetComponent<RectTransform>();
            modalRt.anchorMin = Vector2.zero;
            modalRt.anchorMax = Vector2.one;
            modalRt.offsetMin = Vector2.zero;
            modalRt.offsetMax = Vector2.zero;
            modal.GetComponent<Image>().color = new Color(0, 0, 0, 0.85f); // Dim background

            // Close button (background click)
            GameObject bgBtn = new GameObject("BgCloseBtn", typeof(RectTransform), typeof(Button));
            bgBtn.transform.SetParent(modal.transform, false);
            var bgRt = bgBtn.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;
            bgBtn.GetComponent<Button>().onClick.AddListener(() => Object.Destroy(modal));

            // Image Display
            GameObject imgObj = new GameObject("LoreImageDisplay", typeof(RectTransform), typeof(RawImage));
            imgObj.transform.SetParent(modal.transform, false);
            var imgRt = imgObj.GetComponent<RectTransform>();
            imgRt.anchorMin = new Vector2(0.5f, 0.5f);
            imgRt.anchorMax = new Vector2(0.5f, 0.5f);
            imgRt.pivot = new Vector2(0.5f, 0.5f);
            imgRt.sizeDelta = new Vector2(512, 512);

            byte[] bytes = System.IO.File.ReadAllBytes(path);
            Texture2D tex = new Texture2D(2, 2);
            tex.LoadImage(bytes);
            imgObj.GetComponent<RawImage>().texture = tex;
            
            // Allow closing by clicking image too? Or maybe separate close button?
            // Background click is enough usually. But let's add a small 'X' button top right of image just in case.
            GameObject xBtn = new GameObject("XButton", typeof(RectTransform), typeof(Image), typeof(Button));
            xBtn.transform.SetParent(imgObj.transform, false);
            var xRt = xBtn.GetComponent<RectTransform>();
            xRt.anchorMin = new Vector2(1, 1);
            xRt.anchorMax = new Vector2(1, 1);
            xRt.anchoredPosition = new Vector2(0, 0);
            xRt.sizeDelta = new Vector2(30, 30);
            xBtn.GetComponent<Image>().color = Color.red;
            xBtn.GetComponent<Button>().onClick.AddListener(() => Object.Destroy(modal));
            
            // X Text
            GameObject xTxt = new GameObject("Txt", typeof(RectTransform), typeof(TextMeshProUGUI));
            xTxt.transform.SetParent(xBtn.transform, false);
            var t = xTxt.GetComponent<TextMeshProUGUI>();
            t.text = "X";
            t.alignment = TextAlignmentOptions.Center;
            t.color = Color.white;
            t.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
        }

        [HarmonyPatch(typeof(UiLoreEntry), "OnFinishedEdit")]
        [HarmonyPrefix]
        public static void Prefix_OnFinishedEdit(UiLoreEntry __instance, out string __state)
        {
            // Store old state to update key in WorldData
            __state = $"{__instance.loreEntry.ToUiKeysStr()}_{__instance.loreEntry.val}";
        }

        [HarmonyPatch(typeof(UiLoreEntry), "OnFinishedEdit")]
        [HarmonyPostfix]
        public static void Postfix_OnFinishedEdit(UiLoreEntry __instance, string __state)
        {
            if (__instance.loreEntry != null)
            {
                // Re-key the extra data if the lore content changed
                string oldHash = __state;
                string newHash = $"{__instance.loreEntry.ToUiKeysStr()}_{__instance.loreEntry.val}";
                
                if (oldHash != newHash && WorldData.CurrentState.LoreExtras.ContainsKey(oldHash))
                {
                    var data = WorldData.CurrentState.LoreExtras[oldHash];
                    WorldData.CurrentState.LoreExtras.Remove(oldHash);
                    WorldData.CurrentState.LoreExtras[newHash] = data;
                }
            }
        }
        [HarmonyPatch(typeof(LorebookView), "OnAiGenLoreEntry")]
        [HarmonyPrefix]
        public static bool Prefix_OnAiGenLoreEntry(LorebookView __instance)
        {
            __instance.manager.soundManager.smallClickSoundFxObj.PlayNextSound();
            MainMenu.ConfirmationModal().ShowTextPromptModal(LS.I.GetLocStr("key-input-topic-for-lore-entry") ?? "", textInputActive: true, showCancelButton: true, async delegate
            {
                Lorebook.LoreEntry loreEntry = null;
                bool success = true;
                string errorMsg = "";

                // Determine context-aware prompt augmentation
                string aimCategory = WorldLoreExpansion.CurrentCategory;
                if (aimCategory == "All")
                {
                    // Pick a random specific category to force variety
                    string[] options = { "History", "Economy", "Bestiary", "Geography", "Factions", "General" };
                    aimCategory = options[UnityEngine.Random.Range(0, options.Length)];
                }

                string focusPrompt = "";
                switch (aimCategory)
                {
                    case "History": focusPrompt = "Focus on the history, ancient legends, and origins."; break;
                    case "Economy": focusPrompt = "Focus on trade values, economic impact, and rarity."; break;
                    case "Bestiary": focusPrompt = "Focus on biological traits, hunting behavior, and threat level."; break;
                    case "Geography": focusPrompt = "Focus on the terrain features, climate, and location."; break;
                    case "Factions": focusPrompt = "Focus on the organizational structure, goals, and political relations."; break;
                    default: focusPrompt = "Focus on unique details, cultural significance, and obscure facts."; break;
                }

                string originalInput = MainMenu.ConfirmationModal().textInput1.text;
                string augmentedInput = $"{originalInput}. {focusPrompt} Ensure this entry is distinct and highlights specific details relevant to {aimCategory}. (Uniqueness ID: {UnityEngine.Random.Range(0, 99999)})";

                await Utils.DoTaskWLoadScrn(async delegate
                {
                    try
                    {
                        loreEntry = await AIAsker.GenerateLoreEntry(SS.I.ngLangCode, SS.I.ngCustomLang, 
                            __instance.manager.GetCurrentUniverse().GetPrettyName(), 
                            __instance.manager.GetCurrentUniverse().GetPotentiallyNullDescription(), 
                            __instance.manager.GetCurrentVoronoiWorld().GetPrettyName(), 
                            __instance.manager.GetCurrentVoronoiWorld().GetWorldBkgd(), 
                            augmentedInput);
                        
                        // Auto-tag with the category we aimed for
                        if (loreEntry != null)
                        {
                            var extra = WorldData.GetLoreExtra(loreEntry);
                            // If we randomized 'All', maybe we shouldn't force the category? 
                            // Actually user might appreciate it being auto-sorted.
                            // But usually 'All' means we are just viewing all. 
                            // Let's set it to the detected category if it's not "General".
                            if (aimCategory != "General") extra.Category = aimCategory;
                            else extra.Category = "General";
                        }
                    }
                    catch (System.Exception ex)
                    {
                        success = false;
                        errorMsg = ex.Message;
                        Debug.LogError("[WorldLoreExpansion] Lore generation failed: " + ex);
                    }
                }, __instance.manager);

                if (success && loreEntry != null)
                {
                    __instance.manager.GetCurrentUniverse().lorebook.loreEntries.Add(loreEntry);
                    // Refresh view
                    __instance.Redraw();
                }
                else if (!success)
                {
                    __instance.manager.MessageModal().ShowModal("Failed to generate lore entry. The AI response was malformed or timed out.\n\nError: " + errorMsg, false, true);
                }
            });
            return false;
        }
    }
}
