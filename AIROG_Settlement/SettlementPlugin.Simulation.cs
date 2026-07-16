using System;
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
                self.ScheduleUiUpdate();
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"Settlement turn tick failed: {ex.Message}");
            }
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

            // Job comes from a random completed building
            var built = s.Buildings.FindAll(b => b.IsComplete);
            if (built.Count == 0) return;
            var employer = BuildingCatalog.Get(built[UnityEngine.Random.Range(0, built.Count)].BuildingID);

            var resident = new ResidentData
            {
                Name = NameFirst[UnityEngine.Random.Range(0, NameFirst.Length)] + " " +
                       NameLast[UnityEngine.Random.Range(0, NameLast.Length)],
                Job = employer?.ResidentJob ?? "Laborer",
                Happiness = 50
            };
            s.Residents.Add(resident);
            s.UpdateHappiness();

            Log.LogInfo($"New resident arrived: {resident.Name} ({resident.Job})");
            var gameLog = SS.I?.hackyManager?.gameLogView;
            if (gameLog != null)
                _ = gameLog.LogText($"<color=#a0d8a0>[{s.Name}] A new resident has settled here: {resident.Name}, {resident.Job}.</color>");
        }
    }
}
