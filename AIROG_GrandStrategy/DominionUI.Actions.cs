using System.Collections.Generic;
using System.Linq;
using AIROG_WorldExpansion;

namespace AIROG_GrandStrategy
{
    // Order dispatch (button click handlers call into these) and petition resolution.
    public static partial class DominionUI
    {
        private static void DoOrder(MapModal modal, string type, string arg, string placeUuid = null)
        {
            if (string.IsNullOrEmpty(arg) && RequiresTarget(type))
                _lastResult = "No target faction available.";
            else
                _lastResult = OrderSystem.Issue(modal.manager, type, arg, placeUuid);

            // Map-click picks are single-use regardless of outcome — a failed order just
            // means the player re-picks (or lets ANNEX/CAMPAIGN fall back to automatic).
            if (type == "ANNEX")    _pickedAnnexUuid = null;
            if (type == "CAMPAIGN") _pickedCampaignUuid = null;

            Click(modal);
            BuildPanel(modal);
        }

        private static bool RequiresTarget(string type)
        {
            switch (type)
            {
                case "ENVOY": case "FABRICATE": case "WAR": case "CAMPAIGN":
                case "PILLAGE": case "PEACE": case "VASSAL":
                case "INCITE": case "SABOTAGE": case "SCOUT":
                case "PACT": case "TRADE_DEAL":
                    return true;
                default:
                    return false;
            }
        }

        private static void DoPetition(MapModal modal, bool accept)
        {
            string r = CourtSystem.Resolve(GrandStrategyData.State, accept);
            _lastResult = r != null && r.StartsWith("!") ? r.Substring(1) : (r ?? "No petition awaits.");
            Click(modal);
            BuildPanel(modal);
        }

        private static List<Faction> EligibleTargets(GameplayManager manager)
        {
            var s = GrandStrategyData.State;
            return (manager.GetCurrentFactions() ?? new List<Faction>())
                .Where(f => f != null && f.uuid != s.FactionUuid && f.GetPrettyName() != "Player"
                            && !WorldData.CurrentState.EliminatedFactions.Contains(f.uuid))
                .ToList();
        }
    }
}
