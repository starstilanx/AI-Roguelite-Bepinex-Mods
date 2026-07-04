using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_WorldExpansion;

namespace AIROG_GrandStrategy
{
    // Court petitions: dilemmas brought before the sovereign between strategic ticks.
    // At most one is open at a time; ignoring it past its expiry sours the realm.
    public static class CourtSystem
    {
        private static readonly System.Random rng = new System.Random();

        private const double PETITION_CHANCE = 0.35; // per strategic tick, when none is pending
        private const int    PETITION_LIFETIME_TURNS = 15; // ~3 strategic ticks
        private const int    IGNORED_UNREST = 5;

        // Called every strategic tick: expire a stale petition, or maybe raise a new one.
        public static void Tick(DominionState s)
        {
            int turn = GrandStrategyData.WorldExpansionTurn();

            if (s.PendingPetition != null)
            {
                if (turn < s.PendingPetition.ExpiresTurn) return;

                foreach (var h in s.Holdings.Values) h.Unrest += IGNORED_UNREST;
                GrandStrategyData.LogDeed($"A petition to the throne of {s.DominionName} went unanswered — the petitioners left embittered.");
                WorldData.LogEvent($"The court of {s.DominionName} let a petition lapse unheard. Word of the sovereign's indifference spreads.", "DOMINION");
                s.PendingPetition = null;
                WorldEventsUI.MarkDirty();
                return;
            }

            if (rng.NextDouble() > PETITION_CHANCE) return;

            s.PendingPetition = Generate(s, turn);
            if (s.PendingPetition == null) return;
            WorldData.QueuePlayerEvent(
                $"A petition is brought before the throne of {s.DominionName}: {s.PendingPetition.Text} (GS_PETITION ACCEPT or GS_PETITION REJECT)",
                "DOMINION_PETITION");
            WorldEventsUI.MarkDirty();
        }

        // Applies the chosen branch. Returns the narrative outcome (null if no petition pending).
        public static string Resolve(DominionState s, bool accept)
        {
            var p = s.PendingPetition;
            if (p == null) return null;

            int gold   = accept ? p.AcceptGold   : p.RejectGold;
            int unrest = accept ? p.AcceptUnrest : p.RejectUnrest;
            int army   = accept ? p.AcceptArmy   : p.RejectArmy;
            string outcome = accept ? p.AcceptText : p.RejectText;

            if (gold < 0 && s.Treasury + gold < 0)
                return $"!The treasury cannot bear it ({s.Treasury}g on hand, {-gold}g needed).";

            s.Treasury     += gold;
            s.ArmyStrength  = Math.Max(0, s.ArmyStrength + army);
            foreach (var h in s.Holdings.Values)
                h.Unrest = Math.Max(0, h.Unrest + unrest);

            s.PendingPetition = null;
            GrandStrategyData.LogDeed(outcome);
            WorldData.LogEvent(outcome, "DOMINION");
            WorldEventsUI.MarkDirty();
            GrandStrategyData.SaveToCurrentDir();
            WorldData.SaveToCurrentDir();
            return outcome;
        }

        // ─── Templates ────────────────────────────────────────────────────────────

        private static PetitionData Generate(DominionState s, int turn)
        {
            string holding = s.Holdings.Values.OrderBy(_ => rng.Next())
                .Select(h => h.Name).FirstOrDefault() ?? s.CapitalName;
            string realm = s.DominionName;

            var pool = new List<PetitionData>
            {
                new PetitionData
                {
                    Role       = "STEWARD",
                    Text       = $"The merchant guilds of {holding} beg relief from road tolls, claiming trade withers under them.",
                    AcceptText = $"{realm} lowered the tolls — the guilds rejoice, though the treasury feels it.",
                    RejectText = $"{realm} kept its tolls. The guilds of {holding} mutter that the crown milks them dry.",
                    AcceptGold = -20, AcceptUnrest = -8,
                    RejectGold = +10, RejectUnrest = +8,
                },
                new PetitionData
                {
                    Role       = "MARSHAL",
                    Text       = "Grizzled veterans of your campaigns demand pensions for their scars and lost brothers.",
                    AcceptText = $"{realm} granted its veterans their due — old soldiers now speak proudly of the crown.",
                    RejectText = $"{realm} turned its veterans away. Some drift into banditry; the ranks grumble.",
                    AcceptGold = -25, AcceptArmy = +5,
                    RejectArmy = -5, RejectUnrest = +5,
                },
                new PetitionData
                {
                    Role       = "STEWARD",
                    Text       = $"A blight has ruined the harvest around {holding}; its people plead for grain from the crown's stores.",
                    AcceptText = $"Grain wagons under {realm}'s banner reached {holding} — the people bless the sovereign's name.",
                    RejectText = $"No relief came to {holding}. The hungry remember who let them starve.",
                    AcceptGold = -15, AcceptUnrest = -6,
                    RejectUnrest = +12,
                },
                new PetitionData
                {
                    Role       = "CHANCELLOR",
                    Text       = "Minor nobles offer a chest of gold in exchange for hollow court titles and the sovereign's favor.",
                    AcceptText = $"{realm} sold its titles — the coffers swell, but the smallfolk sneer at the new 'lords'.",
                    RejectText = $"{realm} refused to cheapen its honors. The nobles withdraw, respect grudgingly intact.",
                    AcceptGold = +40, AcceptUnrest = +8,
                    RejectUnrest = 0,
                },
                new PetitionData
                {
                    Role       = "CHANCELLOR",
                    Text       = "The temples ask the crown to fund a holy procession through every holding of the realm.",
                    AcceptText = $"Censers and hymns wound through {realm} — the faithful credit the sovereign's piety.",
                    RejectText = $"The temples of {realm} were refused. Sermons grow pointed about ungodly rulers.",
                    AcceptGold = -20, AcceptUnrest = -10,
                    RejectUnrest = +5,
                },
                new PetitionData
                {
                    Role       = "MARSHAL",
                    Text       = $"A famed mercenary company offers its swords to {realm} — for a price, and winter quartering.",
                    AcceptText = $"The free company took {realm}'s coin — hardened blades now march under your banner.",
                    RejectText = $"The mercenaries shrugged and moved on; perhaps a rival paid better.",
                    AcceptGold = -30, AcceptArmy = +8, AcceptUnrest = +3,
                    RejectGold = 0,
                },
                new PetitionData
                {
                    Role       = "SPYMASTER",
                    Text       = "Your spymaster requests coin to root out a conspiracy before it ripens — pay for silence, or let the plotters be dealt with in the open square?",
                    AcceptText = $"Gold bought silence — the conspirators disappeared without a trace, and {realm}'s grip tightens unseen.",
                    RejectText = $"The plotters were dragged into the square and made an example of — {realm} takes note, uneasily.",
                    AcceptGold = -25, AcceptUnrest = -3,
                    RejectUnrest = +10,
                },
                new PetitionData
                {
                    Role       = "SPYMASTER",
                    Text       = $"Foreign agents offer to sell {realm} intelligence on a rival's war plans — for a price only your spymaster can vouch is honest.",
                    AcceptText = $"The intelligence proved true — {realm}'s spymaster now holds a rival's secrets in hand.",
                    RejectText = $"{realm} refused the offer — if it was true, the secrets went to someone else.",
                    AcceptGold = -20, AcceptArmy = +2,
                    RejectGold = 0,
                },
            };

            // A recruited advisor triples the odds their own domain's petitions surface —
            // the court reflects who actually has the sovereign's ear
            var weighted = new List<PetitionData>();
            foreach (var p in pool)
            {
                int weight = !string.IsNullOrEmpty(p.Role) && s.Advisors.Any(a => a.Role == p.Role) ? 3 : 1;
                for (int i = 0; i < weight; i++) weighted.Add(p);
            }

            var pick = weighted[rng.Next(weighted.Count)];
            pick.ExpiresTurn = turn + PETITION_LIFETIME_TURNS;
            return pick;
        }
    }
}
