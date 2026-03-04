using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AIROG_Multiplayer.Network;

namespace AIROG_Multiplayer.Patches
{
    /// <summary>
    /// Patches the main menu to inject a "Multiplayer" button,
    /// which opens the Host/Join lobby panel.
    /// </summary>
    [HarmonyPatch(typeof(MainMenu), "Start")]
    public static class MainMenuMultiplayerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MainMenu __instance)
        {
            try
            {
                InjectMultiplayerButton(__instance);
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance.Log.LogError($"[Multiplayer] MainMenu.Start patch error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void InjectMultiplayerButton(MainMenu mainMenu)
        {
            // Find the main menu buttons container
            // The game has a canvas with various buttons — find the "New World" or "Load" button to anchor near
            Transform buttonsContainer = FindButtonsContainer(mainMenu);
            if (buttonsContainer == null)
            {
                MultiplayerPlugin.Instance.Log.LogWarning("[Multiplayer] Could not find main menu button container.");
                return;
            }

            // Don't inject twice
            if (buttonsContainer.Find("MultiplayerBtn") != null) return;

            // Clone an existing button if possible (to inherit the game's styling)
            Button existingBtn = buttonsContainer.GetComponentInChildren<Button>();
            if (existingBtn == null) return;

            GameObject btnGo = UnityEngine.Object.Instantiate(existingBtn.gameObject, buttonsContainer);
            btnGo.name = "MultiplayerBtn";

            // Update label
            TMP_Text label = btnGo.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = "⚔ Multiplayer";

            // Clear old listeners and add ours
            Button btn = btnGo.GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                MultiplayerPlugin.Instance.Log.LogInfo("[Multiplayer] Multiplayer button clicked.");
                LobbyPanel.Show(mainMenu);
            });

            // Place it at the end of the button list
            btnGo.transform.SetAsLastSibling();

            MultiplayerPlugin.Instance.Log.LogInfo("[Multiplayer] Multiplayer button injected into main menu.");
        }

        private static Transform FindButtonsContainer(MainMenu mainMenu)
        {
            // Try to find the container by traversing common button names
            string[] candidateNames = { "ButtonsHolder", "MainButtons", "MenuButtons", "Buttons", "Panel" };
            foreach (string name in candidateNames)
            {
                Transform t = mainMenu.transform.FindDeep(name);
                if (t != null && t.GetComponentInChildren<Button>() != null)
                    return t;
            }

            // Fallback: find any transform that has >= 2 direct Button children
            return mainMenu.transform.FindFirst(t =>
            {
                int btnCount = 0;
                for (int i = 0; i < t.childCount; i++)
                    if (t.GetChild(i).GetComponent<Button>() != null) btnCount++;
                return btnCount >= 2;
            });
        }
    }

    /// <summary>Extension helpers for Transform traversal.</summary>
    public static class TransformExtensions
    {
        public static Transform FindDeep(this Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform found = child.FindDeep(name);
                if (found != null) return found;
            }
            return null;
        }

        public static Transform FindFirst(this Transform parent, Func<Transform, bool> predicate)
        {
            if (predicate(parent)) return parent;
            foreach (Transform child in parent)
            {
                Transform found = child.FindFirst(predicate);
                if (found != null) return found;
            }
            return null;
        }
    }
}
