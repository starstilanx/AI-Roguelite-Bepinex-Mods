using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AIROG_TavernImport
{
    /// <summary>
    /// Adds an "Import Lorebook" button beside the native add-entry buttons on the journal's
    /// lorebook tab and the new-game lorebook screen. The button is a clone of a native one, so
    /// it inherits styling and layout; its inspector-wired callbacks are switched off (see
    /// gotcha: RemoveAllListeners does not remove persistent listeners).
    /// </summary>
    public static class ImportButtons
    {
        const string ButtonName = "TavernImportLorebookBtn";
        const string Label = "Import Lorebook";

        public static void LorebookView_Redraw_Postfix(LorebookView __instance)
        {
            if (!TavernImportPlugin.Enabled.Value) return;
            try
            {
                var view = __instance;
                var btn = Ensure(view.addBtnsTrans, () => OnInGameImport(view));
                if (btn != null) btn.interactable = view.manager != null && view.manager.WorldEditsAllowed();
            }
            catch (Exception ex) { TavernImportPlugin.LogWarn($"Journal lorebook button: {ex.Message}"); }
        }

        public static void NgLorebook_UpdateDisplay_Postfix(NgLorebook __instance)
        {
            if (!TavernImportPlugin.Enabled.Value) return;
            try
            {
                Ensure(__instance.addBtnHolder, OnNewGameImport);
                __instance.addBtnHolder.SetAsLastSibling();
            }
            catch (Exception ex) { TavernImportPlugin.LogWarn($"New-game lorebook button: {ex.Message}"); }
        }

        static void OnInGameImport(LorebookView view)
        {
            GameplayManager gm = view.manager ?? SS.I.hackyManager;
            if (gm == null) return;
            if (MpSession.IsClient) { GameImporter.Show("Only the host can import lorebooks."); return; }
            SoundManager.I.smallClickSoundFxObj.PlayNextSound();
            string path = GameImporter.PickBook();
            if (path == null) return;
            try
            {
                string msg = GameImporter.ImportBookInGame(gm, TavernReader.ReadBook(path));
                view.Redraw();
                GameImporter.Show(msg);
            }
            catch (Exception ex)
            {
                TavernImportPlugin.LogWarn($"Lorebook import failed for {path}: {ex}");
                GameImporter.Show($"Couldn't import that lorebook:\n{ex.Message}");
            }
        }

        static void OnNewGameImport()
        {
            MainMenu menu = SS.I.hackyMenu;
            if (menu == null) return;
            SoundManager.I.smallClickSoundFxObj.PlayNextSound();
            string path = GameImporter.PickBook();
            if (path == null) return;
            try
            {
                GameImporter.Show(GameImporter.ImportBookNewGame(menu, TavernReader.ReadBook(path)));
            }
            catch (Exception ex)
            {
                TavernImportPlugin.LogWarn($"Lorebook import failed for {path}: {ex}");
                GameImporter.Show($"Couldn't import that lorebook:\n{ex.Message}");
            }
        }

        /// <summary>Finds or creates the import button under <paramref name="holder"/>.</summary>
        static Button Ensure(Transform holder, Action onClick)
        {
            if (holder == null) return null;
            var existing = holder.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == ButtonName);
            if (existing != null) { SetLabel(existing.gameObject); return existing; }

            var template = holder.GetComponentsInChildren<Button>(true).FirstOrDefault();
            if (template == null) return null;

            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
            clone.name = ButtonName;
            clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            clone.SetActive(true);

            foreach (var b in clone.GetComponentsInChildren<Button>(true))
            {
                DisablePersistent(b.onClick);
                b.onClick.RemoveAllListeners();
            }
            // The game's static labels are driven by LocalizeStringEvent components, which
            // rewrite the text whenever the panel is enabled. Left on the clone, it would put
            // the template button's own caption back over ours.
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name == "LocalizeStringEvent") UnityEngine.Object.DestroyImmediate(mb);
            var btn = clone.GetComponent<Button>() ?? clone.GetComponentInChildren<Button>(true);
            btn.onClick.AddListener(new UnityAction(onClick));
            SetLabel(clone);
            return btn;
        }

        static void DisablePersistent(UnityEventBase ev)
        {
            for (int i = ev.GetPersistentEventCount() - 1; i >= 0; i--)
                ev.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        /// <summary>Re-applied on every redraw in case a localiser resets the cloned text.</summary>
        static void SetLabel(GameObject go)
        {
            var tmp = go.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) { tmp.text = Label; return; }
            var legacy = go.GetComponentInChildren<Text>(true);
            if (legacy != null) { legacy.text = Label; return; }

            // Icon-only template: give the clone its own caption so it isn't a silent duplicate.
            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(go.transform, false);
            var rt = (RectTransform)label.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6, 2); rt.offsetMax = new Vector2(-6, -2);
            var t = label.AddComponent<TextMeshProUGUI>();
            t.text = Label;
            t.alignment = TextAlignmentOptions.Center;
            t.enableAutoSizing = true;
            t.fontSizeMin = 10; t.fontSizeMax = 22;
            t.raycastTarget = false;
            foreach (var img in go.GetComponentsInChildren<Image>(true))
                if (img.gameObject != go) img.enabled = false; // hide the cloned icon
        }
    }
}
