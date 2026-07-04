using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_WorldExpansion;

namespace AIROG_GrandStrategy
{
    // Petitions: dilemmas brought before the dominion's ruler between strategic ticks.
    // At most one is open at a time; ignoring it past its expiry sours the populace.
    // All flavor text is voiced through the theme lexicon so it fits any setting.
    public static class CourtSystem
    {
        private static readonly System.Random rng = new System.Random();

        private const double PETITION_CHANCE = 0.35; // per strategic tick, when none is pending
        private const int    PETITION_LIFETIME_TURNS = 15; // ~3 strategic ticks
        private const int    IGNORED_UNREST = 5;

        // Called every strategic tick: expire a stale petition, or maybe raise a new one.
        public static void Tick(DominionState s)
        {
            var L = GrandStrategyData.L;
            int turn = GrandStrategyData.WorldExpansionTurn();

            if (s.PendingPetition != null)
            {
                if (turn < s.PendingPetition.ExpiresTurn) return;

                foreach (var h in s.Holdings.Values) h.Unrest += IGNORED_UNREST;
                GrandStrategyData.LogDeed($"A {L.PetitionNoun} to the leadership of {s.DominionName} went unanswered — the petitioners left embittered.");
                WorldData.LogEvent($"{s.DominionName} let a {L.PetitionNoun} lapse unheard. Word of its {L.RulerTitle}'s indifference spreads.", "DOMINION");
                s.PendingPetition = null;
                WorldEventsUI.MarkDirty();
                return;
            }

            if (rng.NextDouble() > PETITION_CHANCE) return;

            s.PendingPetition = Generate(s, turn);
            if (s.PendingPetition == null) return;
            WorldData.QueuePlayerEvent(
                $"A {L.PetitionNoun} is brought before you, {L.RulerTitle} of {s.DominionName}: {s.PendingPetition.Text} (GS_PETITION ACCEPT or GS_PETITION REJECT)",
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
                return $"!The treasury cannot bear it ({s.Treasury} on hand, {-gold} needed).";

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
        // Deliberately setting-neutral: traders instead of guilds, food shortages instead
        // of blighted harvests, backers instead of nobles — the lexicon supplies the rest.

        private static PetitionData Generate(DominionState s, int turn)
        {
            var L = GrandStrategyData.L;
            string holding = s.Holdings.Values.OrderBy(_ => rng.Next())
                .Select(h => h.Name).FirstOrDefault() ?? s.CapitalName;
            string realm = s.DominionName;
            string cur   = L.CurrencyWord;

            var pool = new List<PetitionData>
            {
                new PetitionData
                {
                    Role       = "STEWARD",
                    Text       = $"The traders of {holding} beg relief from {realm}'s tariffs, claiming commerce withers under them.",
                    AcceptText = $"{realm} lowered its tariffs — the traders rejoice, though the treasury feels it.",
                    RejectText = $"{realm} kept its tariffs. The traders of {holding} mutter that they are being bled dry.",
                    AcceptGold = -20, AcceptUnrest = -8,
                    RejectGold = +10, RejectUnrest = +8,
                },
                new PetitionData
                {
                    Role       = "MARSHAL",
                    Text       = $"Scarred veterans of your campaigns demand compensation for their wounds and lost comrades.",
                    AcceptText = $"{realm} gave its veterans their due — old {L.SoldiersNoun} now speak proudly of its {L.RulerTitle}.",
                    RejectText = $"{realm} turned its veterans away. Some turn to banditry; the ranks grumble.",
                    AcceptGold = -25, AcceptArmy = +5,
                    RejectArmy = -5, RejectUnrest = +5,
                },
                new PetitionData
                {
                    Role       = "STEWARD",
                    Text       = $"Food has run short around {holding}; its people plead for relief from {realm}'s stores.",
                    AcceptText = $"Supplies under {realm}'s {L.BannersNoun} reached {holding} — the people bless their {L.RulerTitle}'s name.",
                    RejectText = $"No relief came to {holding}. The hungry remember who let them starve.",
                    AcceptGold = -15, AcceptUnrest = -6,
                    RejectUnrest = +12,
                },
                new PetitionData
                {
                    Role       = "CHANCELLOR",
                    Text       = $"Wealthy backers offer {realm} a windfall of {cur} in exchange for hollow honors and official favor.",
                    AcceptText = $"{realm} sold its favors — the coffers swell, but common folk sneer at the new favorites.",
                    RejectText = $"{realm} refused to cheapen its honors. The backers withdraw, respect grudgingly intact.",
                    AcceptGold = +40, AcceptUnrest = +8,
                    RejectUnrest = 0,
                },
                new PetitionData
                {
                    Role       = "CHANCELLOR",
                    Text       = $"Spiritual leaders ask {realm} to fund a public observance through every holding it controls.",
                    AcceptText = $"The observance wound through {realm}'s holdings — the faithful credit their {L.RulerTitle}'s devotion.",
                    RejectText = $"The faithful of {realm} were refused. Their sermons grow pointed about ungrateful rulers.",
                    AcceptGold = -20, AcceptUnrest = -10,
                    RejectUnrest = +5,
                },
                new PetitionData
                {
                    Role       = "MARSHAL",
                    Text       = $"A seasoned band of mercenaries offers its {L.WeaponsNoun} to {realm} — for a price, and quartering through the lean season.",
                    AcceptText = $"The mercenaries took {realm}'s {cur} — hardened {L.SoldiersNoun} now serve under your {L.BannersNoun}.",
                    RejectText = $"The mercenaries shrugged and moved on; perhaps a rival paid better.",
                    AcceptGold = -30, AcceptArmy = +8, AcceptUnrest = +3,
                    RejectGold = 0,
                },
                new PetitionData
                {
                    Role       = "SPYMASTER",
                    Text       = $"Your {L.RoleTitle("SPYMASTER").ToLower()} requests {cur} to bury a conspiracy before it ripens — pay for silence, or make a public example of the plotters?",
                    AcceptText = $"Payment bought silence — the conspirators vanished without a trace, and {realm}'s grip tightens unseen.",
                    RejectText = $"The plotters were dragged out and made an example of — {realm} takes note, uneasily.",
                    AcceptGold = -25, AcceptUnrest = -3,
                    RejectUnrest = +10,
                },
                new PetitionData
                {
                    Role       = "SPYMASTER",
                    Text       = $"Foreign agents offer to sell {realm} intelligence on a rival's war plans — for a price no one can vouch is honest.",
                    AcceptText = $"The intelligence proved true — {realm} now holds a rival's secrets in hand.",
                    RejectText = $"{realm} refused the offer — if it was true, the secrets went to someone else.",
                    AcceptGold = -20, AcceptArmy = +2,
                    RejectGold = 0,
                },
            };

            // A recruited advisor triples the odds their own domain's petitions surface —
            // the inner circle reflects who actually has the ruler's ear
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
