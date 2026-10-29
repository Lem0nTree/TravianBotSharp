namespace MainCore.Services.BuildingAutomation;

public sealed record BuildingState(int Slot, BuildingEnums Type, int Level, bool UnderConstruction = false);
public sealed record BuildingSnapshot(TribeEnums Tribe, BuildingState[] Buildings, int ConstructionCount, int PendingCount, long Warehouse, long Granary);

public static class BuildingSnapshotReader
{
    public static BuildingSnapshot Read(AppDbContext db, AccountId accountId, VillageId villageId)
    {
        if (!db.Villages.AsNoTracking().Any(x => x.Id == villageId.Value && x.AccountId == accountId.Value))
            throw new InvalidOperationException("The village does not belong to this account.");
        var buildings = db.Buildings.AsNoTracking().Where(x => x.VillageId == villageId.Value).OrderBy(x => x.Location)
            .Select(x => new BuildingState(x.Location, x.Type, x.Level, x.IsUnderConstruction)).ToArray();
        if (buildings.Length < 38 || buildings.Select(x => x.Slot).Distinct().Count() != buildings.Length ||
            buildings.Any(x => x.Slot is < 1 or > 40 || x.Type == BuildingEnums.Unknown) ||
            Enumerable.Range(1, 18).Any(i => !buildings.Any(x => x.Slot == i && x.Type.IsResourceField() && x.Level >= 0)) ||
            buildings.Count(x => x.Slot > 18) < 20)
            throw new InvalidOperationException("Village buildings are incomplete. Refresh both fields and village center.");
        var storage = db.Storages.AsNoTracking().SingleOrDefault(x => x.VillageId == villageId.Value)
            ?? throw new InvalidOperationException("Resource data is missing. Refresh the village first.");
        var accountTribe = db.AccountsInfo.AsNoTracking().Where(x => x.AccountId == accountId.Value).Select(x => (TribeEnums?)x.Tribe).FirstOrDefault() ?? TribeEnums.Any;
        var tribe = (TribeEnums)(db.VillagesSetting.Where(x => x.VillageId == villageId.Value && x.Setting == VillageSettingEnums.Tribe).Select(x => (int?)x.Value).FirstOrDefault() ?? (int)accountTribe);
        if (tribe is TribeEnums.Any or TribeEnums.Nature or TribeEnums.Natars || !Enum.IsDefined(tribe))
            throw new InvalidOperationException("A supported player tribe could not be identified.");
        return new(tribe, buildings, db.QueueBuildings.Count(x => x.VillageId == villageId.Value),
            db.Jobs.Count(x => x.VillageId == villageId.Value), storage.Warehouse, storage.Granary);
    }
}

public static class BuildingRules
{
    public static bool CanUpgrade(BuildingSnapshot snapshot, BuildingState building, bool? isCapital)
    {
        var type = building.Type;
        if (!Enum.IsDefined(type) || type is BuildingEnums.Unknown or BuildingEnums.Site or BuildingEnums.Blacksmith or BuildingEnums.WW or BuildingEnums.GreatWarehouse or BuildingEnums.GreatGranary ||
            type.GetCost(1).Take(4).Sum() <= 0 || building.UnderConstruction || building.Level < 0) return false;
        if (building.Level == 0 && !type.IsResourceField()) return false;
        var max = type.IsResourceField() ? isCapital == true ? 20 : 10 : type.GetMaxLevel();
        if (building.Level >= max) return false;
        if (type.IsWall() && type != snapshot.Tribe.GetWall()) return false;
        if (type == BuildingEnums.Trapper && snapshot.Tribe != TribeEnums.Gauls || type == BuildingEnums.Brewery && snapshot.Tribe != TribeEnums.Teutons ||
            type == BuildingEnums.HorseDrinkingTrough && snapshot.Tribe != TribeEnums.Romans || type == BuildingEnums.CommandCenter && snapshot.Tribe != TribeEnums.Huns ||
            type == BuildingEnums.Waterworks && snapshot.Tribe != TribeEnums.Egyptians) return false;
        if (type is BuildingEnums.StonemasonsLodge or BuildingEnums.Brewery && isCapital != true) return false;
        if (type is BuildingEnums.GreatBarracks or BuildingEnums.GreatStable && isCapital != false) return false;
        if (type.IsResourceField() != (building.Slot <= 18) || type.IsWall() != (building.Slot == 40) ||
            (type == BuildingEnums.RallyPoint) != (building.Slot == 39) || type == BuildingEnums.MainBuilding && building.Slot != 26) return false;
        if (type.GetPrerequisiteBuildings().Any(p => !snapshot.Buildings.Any(b => b.Type == p.Type && b.Level >= p.Level))) return false;
        var cost = type.GetCost(building.Level + 1).Take(4).ToArray();
        return cost.Length == 4 && cost.Take(3).All(x => x <= snapshot.Warehouse) && cost[3] <= snapshot.Granary;
    }
}
