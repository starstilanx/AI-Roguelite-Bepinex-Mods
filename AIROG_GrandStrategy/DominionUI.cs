using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AIROG_GrandStrategy
{
    // ─── Dominion Panel ──────────────────────────────────────────────────────────
    // A "DOM" button on the world map (beside WorldExpansion's "POL" political lens
    // button) toggles a control panel: founding, status, every order as a button,
    // tax edicts, and petition judgments. Same procedural-UI conventions as
    // StrategicMapUI: cloned frame for the button, layout-group panel on mapViewTrans.
    // Split via `partial` across DominionUI.*.cs — the shared static toggle/pick-mode state
    // below is read/written from nearly every method, so a verbatim method-group move (same
    // class, same fields) is far lower-risk here than introducing new class boundaries.
    // See DominionUI.Button.cs (toggle button + capital marker), DominionUI.Panel.cs (panel
    // build/populate), DominionUI.Actions.cs (order dispatch), DominionUI.Primitives.cs
    // (generic UI-primitive helpers).
    public static partial class DominionUI
    {
        private const string BUTTON_NAME = "DominionButton_Mod";
        private const string PANEL_NAME  = "DominionPanel_Mod";
        private const string MARKER_NAME = "DominionMarker_Mod";

        private static bool _panelOn;
        private static int _targetIdx;
        private static int _impIdx;
        private static int _wonderIdx;
        private static int _holdingIdx;
        private static int _advisorRoleIdx;
        private static string _lastResult = "";

        // Map-click targeting: a panel toggle arms pick mode, then the next left-click on a
        // MapLocation (intercepted via Harmony below) supplies a specific place instead of
        // ANNEX/CAMPAIGN's automatic nearest/adjacent pick.
        private enum PickMode { None, Annex, Campaign }
        private static PickMode _pickMode = PickMode.None;
        private static string _pickedAnnexUuid;
        private static string _pickedCampaignUuid;

        private static readonly Color GOLD    = new Color(1f, 0.85f, 0.3f);
        private static readonly Color MUTED   = new Color(0.78f, 0.78f, 0.83f);
        private static readonly Color BTN_FACE = new Color(0.16f, 0.16f, 0.24f, 0.95f);
        private static readonly Color BTN_WARM = new Color(0.28f, 0.16f, 0.10f, 0.95f);
        private static readonly Color DIVIDER = new Color(0.45f, 0.42f, 0.30f);

        // ─── MapModal hooks (mirrors StrategicMapUI) ──────────────────────────────

        [HarmonyPatch(typeof(MapModal), "ShowWorldView")]
        [HarmonyPostfix]
        public static void Postfix_ShowWorldView(MapModal __instance, VoronoiWorld vw)
        {
            try
            {
                EnsureDominionButton(__instance);
                EnsureCapitalMarker(__instance);
                if (_panelOn) BuildPanel(__instance);
                else ClearPanel(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GrandStrategy] Dominion panel failed: {e}");
            }
        }

        [HarmonyPatch(typeof(MapModal), "ShowDetachedView")]
        [HarmonyPostfix]
        public static void Postfix_ShowDetachedView(MapModal __instance)
        {
            SetButtonVisible(__instance, false);
            ClearPanel(__instance);
            ClearCapitalMarker(__instance);
            _pickMode = PickMode.None;
        }

        [HarmonyPatch(typeof(MapModal), "ShowUniv")]
        [HarmonyPostfix]
        public static void Postfix_ShowUniv(MapModal __instance)
        {
            SetButtonVisible(__instance, false);
            ClearPanel(__instance);
            ClearCapitalMarker(__instance);
            _pickMode = PickMode.None;
        }

        [HarmonyPatch(typeof(MapModal), "HideMapModal")]
        [HarmonyPostfix]
        public static void Postfix_HideMapModal(MapModal __instance)
        {
            ClearPanel(__instance);
            ClearCapitalMarker(__instance);
            _pickMode = PickMode.None;
        }

        // A left-click on the world map, while a pick mode is armed from the panel, supplies
        // a specific place to ANNEX/CAMPAIGN instead of triggering the native travel/select
        // behavior. Consumed (returns false) only while a pick is actually pending.
        [HarmonyPatch(typeof(MapLocation), "OnPointerUp")]
        [HarmonyPrefix]
        public static bool Prefix_MapLocationPointerUp(MapLocation __instance, PointerEventData eventData)
        {
            if (_pickMode == PickMode.None) return true;
            if (eventData.button != PointerEventData.InputButton.Left) return true;

            Place place = __instance.GetPlace();
            if (place == null) return true;

            if (_pickMode == PickMode.Annex)     _pickedAnnexUuid = place.uuid;
            else if (_pickMode == PickMode.Campaign) _pickedCampaignUuid = place.uuid;

            var modal = __instance.mapModal;
            _lastResult = $"Target set: {place.GetPrettyName()}.";
            _pickMode = PickMode.None;
            if (modal != null) { Click(modal); BuildPanel(modal); }
            return false;
        }

        private static void Click(MapModal modal)
        {
            modal.manager?.soundManager?.smallClickSoundFxObj?.PlayNextSound();
        }
    }
}
