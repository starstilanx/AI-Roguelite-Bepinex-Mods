namespace AIROG_Settlement
{
    public partial class SettlementPlugin
    {
        public void BuildBuilding(string buildingId)
        {
            if (!HasActiveSettlement) { Log.LogWarning("Cannot build: no settlement established."); return; }
            var def = BuildingCatalog.Get(buildingId);
            if (def == null) { Log.LogError($"Unknown building ID: {buildingId}"); return; }
            if (CurrentSettlement.HasBuilding(buildingId)) { Log.LogWarning($"{buildingId} already built."); return; }
            if (!def.CanAfford(CurrentSettlement)) { Log.LogWarning($"Cannot afford {buildingId}."); return; }

            foreach (var kv in def.Cost)
                CurrentSettlement.Resources[kv.Key] -= kv.Value;

            CurrentSettlement.Buildings.Add(new BuildingInstance
            {
                BuildingID = def.ID,
                Name = def.Name,
                Level = 1,
                IsComplete = true,
                ConstructionProgress = 100f
            });

            CurrentSettlement.RecalculateLevel();
            CurrentSettlement.UpdateHappiness();
            Log.LogInfo($"Built {def.Name} at {CurrentSettlement.Name}.");
            SaveSettlementData();
        }

        public void UpgradeBuilding(string buildingId)
        {
            if (!HasActiveSettlement) return;
            var def = BuildingCatalog.Get(buildingId);
            var instance = CurrentSettlement.GetBuilding(buildingId);
            if (def == null || instance == null) return;
            if (instance.Level >= BuildingDefinition.MAX_LEVEL) return;

            var cost = def.GetUpgradeCost(instance.Level);
            if (!BuildingDefinition.CanAffordCost(CurrentSettlement, cost))
            {
                Log.LogWarning($"Cannot afford upgrade for {buildingId}.");
                return;
            }

            foreach (var kv in cost)
                CurrentSettlement.Resources[kv.Key] -= kv.Value;
            instance.Level++;

            CurrentSettlement.RecalculateLevel();
            Log.LogInfo($"Upgraded {def.Name} to level {instance.Level}.");
            SaveSettlementData();
        }
    }
}
