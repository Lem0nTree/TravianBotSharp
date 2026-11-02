namespace MainCore.Services.ChatControl;

/// <summary>The only village settings exposed for remote edits and their shared training policy.</summary>
public static class VillageControlPolicy
{
    public sealed record TrainingBuilding(VillageSettingEnums TroopKey, BuildingEnums Building,
        VillageSettingEnums Minimum, VillageSettingEnums Maximum);

    public static readonly TrainingBuilding[] Buildings =
    [
        new(VillageSettingEnums.BarrackTroop, BuildingEnums.Barracks, VillageSettingEnums.BarrackAmountMin, VillageSettingEnums.BarrackAmountMax),
        new(VillageSettingEnums.StableTroop, BuildingEnums.Stable, VillageSettingEnums.StableAmountMin, VillageSettingEnums.StableAmountMax),
        new(VillageSettingEnums.GreatBarrackTroop, BuildingEnums.GreatBarracks, VillageSettingEnums.GreatBarrackAmountMin, VillageSettingEnums.GreatBarrackAmountMax),
        new(VillageSettingEnums.GreatStableTroop, BuildingEnums.GreatStable, VillageSettingEnums.GreatStableAmountMin, VillageSettingEnums.GreatStableAmountMax),
        new(VillageSettingEnums.WorkshopTroop, BuildingEnums.Workshop, VillageSettingEnums.WorkshopAmountMin, VillageSettingEnums.WorkshopAmountMax)
    ];
    public static readonly VillageSettingEnums[] Editable =
    [
        VillageSettingEnums.AutoBuildEnable, VillageSettingEnums.AutoRefreshEnable, VillageSettingEnums.AutoRefreshMin,
        VillageSettingEnums.AutoRefreshMax, VillageSettingEnums.AutoClaimQuestEnable, VillageSettingEnums.TrainTroopEnable,
        .. Buildings.Select(x => x.TroopKey)
    ];
    public static bool IsTraining(VillageSettingEnums key) => key == VillageSettingEnums.TrainTroopEnable || Buildings.Any(x => x.TroopKey == key);
    public static bool IsValidValue(VillageSettingEnums key, int value) => key switch
    {
        VillageSettingEnums.AutoRefreshMin or VillageSettingEnums.AutoRefreshMax => value is >= 1 and <= 1440,
        VillageSettingEnums.AutoBuildEnable or VillageSettingEnums.AutoRefreshEnable or VillageSettingEnums.AutoClaimQuestEnable or VillageSettingEnums.TrainTroopEnable => value is 0 or 1,
        _ => Buildings.Any(x => x.TroopKey == key) && value >= 0 && Enum.IsDefined((TroopEnums)value)
    };
    public static List<TroopEnums> Choices(TribeEnums tribe, BuildingEnums building) => building switch
    {
        BuildingEnums.Barracks or BuildingEnums.GreatBarracks => tribe.GetInfantryTroops(),
        BuildingEnums.Stable or BuildingEnums.GreatStable => tribe.GetCavalryTroops(),
        BuildingEnums.Workshop => tribe.GetSiegeTroops(),
        _ => []
    };
    public static object Options(TribeEnums tribe) => new
    {
        Tribe = tribe.ToString(),
        Buildings = Buildings.Select(b => new { Key = b.TroopKey.ToString(), Building = b.Building.ToString(), Choices = new[] { TroopEnums.None }.Concat(Choices(tribe, b.Building)).Select(t => new { Id = (int)t, Name = t.ToString() }).ToArray() }).ToArray()
    };
    public static string? ValidateTraining(AppDbContext db, int villageId, IReadOnlyDictionary<VillageSettingEnums, int> values)
    {
        var tribe = (TribeEnums)values[VillageSettingEnums.Tribe];
        var enabled = values[VillageSettingEnums.TrainTroopEnable] == 1;
        var any = false;
        var persisted = db.VillagesSetting.AsNoTracking().Where(x => x.VillageId == villageId).GroupBy(x => x.Setting).Select(g => new { Key = g.Key, Count = g.Count() }).ToDictionary(x => x.Key, x => x.Count);
        bool HasSingle(VillageSettingEnums key) => persisted.GetValueOrDefault(key) == 1;
        foreach (var b in Buildings)
        {
            var selected = (TroopEnums)values[b.TroopKey];
            if (selected == TroopEnums.None) continue;
            any = true;
            if (!Choices(tribe, b.Building).Contains(selected)) return $"{b.TroopKey} is not a valid troop for this tribe and building.";
            if (!enabled) continue;
            if (!db.Buildings.Any(x => x.VillageId == villageId && x.Type == b.Building && x.Level > 0)) return $"A cached {b.Building} is required for its selected troop.";
            if (!HasSingle(b.Minimum) || !HasSingle(b.Maximum) || values[b.Minimum] < 1 || values[b.Maximum] < values[b.Minimum]) return $"Invalid existing training amount range for {b.Building}.";
        }
        if (!enabled) return null;
        if (!any) return "Select at least one troop before enabling training.";
        var min = values[VillageSettingEnums.TrainTroopRepeatTimeMin];
        var max = values[VillageSettingEnums.TrainTroopRepeatTimeMax];
        if (!HasSingle(VillageSettingEnums.TrainTroopRepeatTimeMin) || !HasSingle(VillageSettingEnums.TrainTroopRepeatTimeMax) || min < 1 || max < min || max > int.MaxValue / 60) return "Invalid existing training repeat interval.";
        return null;
    }
}
