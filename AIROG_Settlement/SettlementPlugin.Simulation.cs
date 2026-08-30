using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_Settlement
{
    // Per-Turn Simulation. Production runs on the game's turn event (NOT WriteSaveFile,
    // which can fire several times — or not at all — per turn and made income erratic).
    public partial class SettlementPlugin
    {
        private static readonly string[] NameFirst = { "Bren", "Mara", "Tolim", "Eska", "Dorn", "Lyra", "Fenwick", "Hilda", "Osric", "Petra", "Quinn", "Sable" };
        private static readonly string[] NameLast  = { "Ashfoot", "Briarwood", "Coppervein", "Dunmore", "Emberly", "Fallowfield", "Greenbottle", "Hollowbrook", "Ironwhistle", "Thistledown" };

        public static void OnTurnHappened(int numTurns, long secs)
        {
            try
            {
                var self = Instance;
                if (self == null) return;
                var s = self.CurrentSettlement;
                if (s == null || string.IsNullOrEmpty(s.LocationUuid)) return;

                s.ProduceResources(numTurns);
                s.UpdateHappiness();
                self.TryResidentArrival(numTurns);
                self.UpdateTradePrices(numTurns);
                self.TryTriggerEvent(numTurns);
                self.ScheduleUiUpdate();
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"Settlement turn tick failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a new resident with a random name, a job drawn from a random completed
        /// building, a random personality trait, and starting happiness seeded from the
        /// current town baseline. Returns null if there's no completed building to employ
        /// them at yet. Shared by natural arrival and event outcomes that grant a resident.
        /// </summary>
        internal static ResidentData CreateNewResident(SettlementState s)
        {
            var built = s.Buildings.FindAll(b => b.IsComplete);
            if (built.Count == 0) return null;

            var employer = BuildingCatalog.Get(built[UnityEngine.Random.Range(0, built.Count)].BuildingID);
            string trait = TraitCatalog.Random();
            int happiness = Math.Max(0, Math.Min(100, s.GetBaseHappiness() + TraitCatalog.GetModifier(trait)));

            return new ResidentData
            {
                Name = NameFirst[UnityEngine.Random.Range(0, NameFirst.Length)] + " " +
                       NameLast[UnityEngine.Random.Range(0, NameLast.Length)],
                Job = employer?.ResidentJob ?? "Laborer",
                Trait = trait,
                Happiness = happiness
            };
        }

        /// <summary>
        /// New residents drift in over time: requires a Farm (food), free capacity,
        /// and a 20% chance per elapsed turn.
        /// </summary>
        private void TryResidentArrival(int numTurns)
        {
            var s = CurrentSettlement;
            if (!s.HasBuilding("farm")) return;
            if (s.Residents.Count >= s.GetPopulationCap()) return;

            float arrivalChance = 1f - Mathf.Pow(0.8f, numTurns); // 20% per turn, compounded
            if (UnityEngine.Random.value > arrivalChance) return;

            var resident = CreateNewResident(s);
            if (resident == null) return;
            s.Residents.Add(resident);
            s.UpdateHappiness();

            Log.LogInfo($"New resident arrived: {resident.Name} ({resident.Job}, {resident.Trait})");
            var gameLog = SS.I?.hackyManager?.gameLogView;
            if (gameLog != null)
                _ = gameLog.LogText($"<color=#a0d8a0>[{s.Name}] A new resident has settled here: {resident.Name}, {resident.Job}.</color>");
        }

        /// <summary>
        /// Drifts Wood/Stone trade prices by a small random walk each turn, clamped to a
        /// band around the baseline multiplier of 1.0. Unlike happiness, this has no
        /// determinism requirement, so ordinary RNG is fine here.
        /// </summary>
        private void UpdateTradePrices(int numTurns)
        {
            var s = CurrentSettlement;
            if (string.IsNullOrEmpty(s.LocationUuid)) return;

            var keys = new List<string>(s.TradePrices.Keys);
            foreach (var key in keys)
            {
                float drift = UnityEngine.Random.Range(-0.04f, 0.04f) * numTurns;
                s.TradePrices[key] = Mathf.Clamp(s.TradePrices[key] + drift, 0.7f, 1.5f);
            }
        }
    }
}
