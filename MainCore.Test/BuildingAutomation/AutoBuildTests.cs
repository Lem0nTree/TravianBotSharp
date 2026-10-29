using MainCore.Entities;
using MainCore.Enums;
using MainCore.Infrasturecture.Persistence;
using MainCore.Services.BuildingAutomation;
using MainCore.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MainCore.Test.BuildingAutomation;

public static class BuildingFixture
{
    public static BuildingSnapshot Snapshot()
    {
        var buildings = Enumerable.Range(1, 40).Select(slot => new BuildingState(slot,
            slot <= 18 ? (BuildingEnums)((slot - 1) % 4 + 1) : BuildingEnums.Site, slot <= 18 ? 1 : 0)).ToArray();
        buildings[18] = new(19, BuildingEnums.Warehouse, 20);
        buildings[19] = new(20, BuildingEnums.Granary, 20);
        buildings[20] = new(21, BuildingEnums.Residence, 9);
        buildings[25] = new(26, BuildingEnums.MainBuilding, 5);
        buildings[30] = new(31, BuildingEnums.Barracks, 3);
        buildings[38] = new(39, BuildingEnums.RallyPoint, 1);
        buildings[39] = new(40, BuildingEnums.EarthWall, 0);
        return new(TribeEnums.Teutons, buildings, 0, 0, 80000, 80000);
    }
}

public class AutoBuildTests
{
    [Fact]
    public void ChoosesLowestLevelThenSlotIncludingLevelZeroField()
    {
        var snapshot = BuildingFixture.Snapshot();
        snapshot.Buildings[5] = new(6, BuildingEnums.ClayPit, 0);
        var plan = AutoBuildSelector.Select(snapshot, false);
        plan.ShouldNotBeNull();
        plan.Location.ShouldBe(6);
        plan.Level.ShouldBe(1);
    }

    [Fact]
    public void DoesNotChooseWhenEitherQueueHasWorkOrBuildingIsUnderConstruction()
    {
        var snapshot = BuildingFixture.Snapshot();
        AutoBuildSelector.Select(snapshot with { PendingCount = 1 }, false).ShouldBeNull();
        AutoBuildSelector.Select(snapshot with { ConstructionCount = 1 }, false).ShouldBeNull();
        snapshot.Buildings[0] = snapshot.Buildings[0] with { UnderConstruction = true };
        AutoBuildSelector.Select(snapshot, false).ShouldBeNull();
    }

    [Fact]
    public void CapitalFieldAboveTenRequiresExplicitCapitalConfirmation()
    {
        var snapshot = BuildingFixture.Snapshot();
        var buildings = snapshot.Buildings.Select(x => x.Type.IsResourceField() ? x with { Level = 10 } : x with { Type = BuildingEnums.Site, Level = 0 }).ToArray();
        snapshot = snapshot with { Buildings = buildings };
        AutoBuildSelector.Select(snapshot, null).ShouldBeNull();
        AutoBuildSelector.Select(snapshot, false).ShouldBeNull();
        AutoBuildSelector.Select(snapshot, true).ShouldNotBeNull();
    }

    [Fact]
    public void RulesRejectUnsupportedWrongTribeSlotMissingPrerequisitesAndStorage()
    {
        var snapshot = BuildingFixture.Snapshot();
        BuildingRules.CanUpgrade(snapshot, new(22, BuildingEnums.WW, 1), true).ShouldBeFalse();
        BuildingRules.CanUpgrade(snapshot, new(40, BuildingEnums.CityWall, 1), false).ShouldBeFalse();
        BuildingRules.CanUpgrade(snapshot, new(22, BuildingEnums.MainBuilding, 1), false).ShouldBeFalse();
        BuildingRules.CanUpgrade(snapshot, new(22, BuildingEnums.Workshop, 1), false).ShouldBeFalse();
        BuildingRules.CanUpgrade(snapshot with { Warehouse = 1, Granary = 1 }, snapshot.Buildings[0], false).ShouldBeFalse();
        BuildingRules.CanUpgrade(snapshot, snapshot.Buildings[0], false).ShouldBeTrue();
    }
}

public sealed class AutoBuildDatabaseTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    public AutoBuildDatabaseTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw("CREATE TABLE AiPlanReceipts (PlanId TEXT PRIMARY KEY, Description TEXT)");
        _db.Database.ExecuteSqlRaw("INSERT INTO AiPlanReceipts VALUES ('old-receipt', 'keep')");
        _db.Accounts.Add(new Account { Id = 1, Username = "test", Server = "https://example.invalid" });
        _db.Villages.Add(new Village { Id = 1, Name = "Test", AccountId = 1 });
        _db.AccountsInfo.Add(new AccountInfo { AccountId = 1, Tribe = TribeEnums.Teutons });
        _db.VillagesSetting.Add(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.Tribe, Value = (int)TribeEnums.Teutons });
        _db.VillagesSetting.Add(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.AutoBuildEnable, Value = 1 });
        foreach (var b in BuildingFixture.Snapshot().Buildings)
            _db.Buildings.Add(new Building { VillageId = 1, Location = b.Slot, Type = b.Type, Level = b.Level });
        _db.Storages.Add(new Storage { VillageId = 1, Warehouse = 80000, Granary = 80000, FreeCrop = 100 });
        _db.SaveChanges();
    }

    [Fact]
    public void QueuesOnlyOnceAndPreservesExistingWorkAndHistoricalTable()
    {
        AutoBuildQueue.TryQueue(_db, new(1), new(1)).ShouldNotBeNull();
        var before = _db.Jobs.Single().Content;
        AutoBuildQueue.TryQueue(_db, new(1), new(1)).ShouldBeNull();
        _db.Jobs.Single().Content.ShouldBe(before);
        _db.Jobs.RemoveRange(_db.Jobs);
        _db.QueueBuildings.Add(new QueueBuilding { VillageId = 1, Location = 1, Type = BuildingEnums.Woodcutter, Level = 2 });
        _db.SaveChanges();
        AutoBuildQueue.TryQueue(_db, new(1), new(1)).ShouldBeNull();
        _db.Jobs.ShouldBeEmpty();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Description FROM AiPlanReceipts WHERE PlanId = 'old-receipt'";
        command.ExecuteScalar().ShouldBe("keep");
    }

    [Fact]
    public void RejectsWrongOwnerAndIncompleteVillageWithoutQueuing()
    {
        Should.Throw<InvalidOperationException>(() => AutoBuildQueue.TryQueue(_db, new(2), new(1)));
        _db.Buildings.Remove(_db.Buildings.Single(x => x.VillageId == 1 && x.Location == 1));
        _db.SaveChanges();
        Should.Throw<InvalidOperationException>(() => AutoBuildQueue.TryQueue(_db, new(1), new(1)));
        _db.Jobs.ShouldBeEmpty();
    }

    [Fact]
    public void NewSettingsAreInitializedPerVillageWithoutChangingExistingValues()
    {
        _db.Villages.Add(new Village { Id = 2, Name = "Second", AccountId = 1 });
        _db.SaveChanges();
        _db.FillVillageSettings();
        _db.VillagesSetting.Single(x => x.VillageId == 1 && x.Setting == VillageSettingEnums.AutoBuildEnable).Value.ShouldBe(1);
        _db.VillagesSetting.Single(x => x.VillageId == 2 && x.Setting == VillageSettingEnums.AutoBuildEnable).Value.ShouldBe(0);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
