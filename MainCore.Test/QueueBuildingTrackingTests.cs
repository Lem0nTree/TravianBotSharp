using System.Reflection;
using MainCore.Commands.Update;
using MainCore.DTO;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Models;

namespace MainCore.Test;

public class QueueBuildingTrackingTests
{
    private static List<BuildingDto> Fields(int count, int level = 7) => Enumerable.Range(1, count)
        .Select(slot => new BuildingDto { Location = slot, Type = BuildingEnums.ClayPit, Level = level, IsUnderConstruction = true }).ToList();
    private static List<QueueBuildingDto> Queue(int count, int level = 8) => Enumerable.Range(0, count)
        .Select(index => new QueueBuildingDto { Type = nameof(BuildingEnums.ClayPit), Level = level, CompleteTime = DateTime.Now.AddMinutes(10 + index) }).ToList();
    private static void Update(TaskExecutionHarness h, List<BuildingDto> fields, List<QueueBuildingDto> queue)
    {
        var command = new UpdateBuildingCommand(h.Browser, h.Factory, h.Notifications);
        typeof(UpdateBuildingCommand).GetMethod("UpdateToDatabase", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command, [new VillageId(1), fields, queue, TribeEnums.Any]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void IdenticalUpgradesHaveDistinctSlotsAcrossRepeatedScans(int count)
    {
        using var h = new TaskExecutionHarness();
        Update(h, Fields(count), Queue(count));
        Update(h, Fields(count), Queue(count));
        using var db = h.Factory.CreateDbContext();
        var rows = db.QueueBuildings.ToList();
        rows.Count.ShouldBe(count);
        rows.Select(x => x.Location).Order().ShouldBe(Enumerable.Range(1, count));
    }

    [Fact]
    public void RepairsPreviouslyDuplicatedLocations()
    {
        using var h = new TaskExecutionHarness();
        using (var db = h.Factory.CreateDbContext())
        {
            db.QueueBuildings.AddRange(Queue(2).Select(x => new QueueBuilding { VillageId = 1, Location = 1, Type = BuildingEnums.ClayPit, Level = x.Level, CompleteTime = x.CompleteTime }));
            db.SaveChanges();
        }
        Update(h, Fields(2), Queue(2));
        using var result = h.Factory.CreateDbContext();
        result.QueueBuildings.Select(x => x.Location).OrderBy(x => x).ToArray().ShouldBe([1, 2]);
    }

    [Fact]
    public void AmbiguousPartialMappingRemainsUnknown()
    {
        using var h = new TaskExecutionHarness();
        Update(h, Fields(2), Queue(1));
        using var db = h.Factory.CreateDbContext();
        db.QueueBuildings.Single().Location.ShouldBe(-1);
    }

    [Fact]
    public void KnownSlotIsReservedForItsExistingRow()
    {
        using var h = new TaskExecutionHarness();
        using (var db = h.Factory.CreateDbContext())
        {
            db.QueueBuildings.Add(new QueueBuilding { VillageId = 1, Location = 2, Type = BuildingEnums.ClayPit, Level = 8, CompleteTime = DateTime.Now.AddMinutes(10) });
            db.SaveChanges();
        }
        Update(h, Fields(2), Queue(2));
        using var result = h.Factory.CreateDbContext();
        result.QueueBuildings.OrderBy(x => x.Id).Select(x => x.Location).ToArray().ShouldBe([2, 1]);
    }

    [Fact]
    public void ShrinkingDuplicateGroupRemovesSurplusAndInfersFromFreshConstruction()
    {
        using var h = new TaskExecutionHarness();
        Update(h, Fields(3), Queue(3));
        var fields = Fields(3);
        fields[0].IsUnderConstruction = false;
        fields[0].Level = 8;
        Update(h, fields, Queue(2));
        using var db = h.Factory.CreateDbContext();
        db.QueueBuildings.Select(x => x.Location).OrderBy(x => x).ToArray().ShouldBe([2, 3]);
        db.Buildings.Single(x => x.Location == 1).Level.ShouldBe(8);
    }

    [Fact]
    public void CachedOtherViewDoesNotSupplyConstructionEvidence()
    {
        using var h = new TaskExecutionHarness();
        Update(h, Fields(1), []);
        Update(h, [new BuildingDto { Location = 19, Type = BuildingEnums.Warehouse, Level = 1 }], Queue(1));
        using var db = h.Factory.CreateDbContext();
        db.QueueBuildings.Single().Location.ShouldBe(-1);
    }

    [Fact]
    public void InterchangeableExpiredTimerCannotCompleteTheWrongSlot()
    {
        using var h = new TaskExecutionHarness();
        Update(h, Fields(2), Queue(2));
        using (var db = h.Factory.CreateDbContext())
        {
            db.QueueBuildings.OrderBy(x => x.Id).First().CompleteTime = DateTime.Now.AddMinutes(-1);
            db.SaveChanges();
        }
        Update(h, Fields(2), Queue(2));
        using var result = h.Factory.CreateDbContext();
        result.Buildings.Select(x => x.Level).ToArray().ShouldBe([7, 7]);
        result.QueueBuildings.Select(x => x.Location).OrderBy(x => x).ToArray().ShouldBe([1, 2]);
    }

    [Fact]
    public void TwoQueuedLevelsCanShareASingleSlot()
    {
        using var h = new TaskExecutionHarness();
        Update(h, Fields(1), [.. Queue(1), .. Queue(1, 9)]);
        using var db = h.Factory.CreateDbContext();
        db.QueueBuildings.Select(x => x.Location).ToArray().ShouldBe([1, 1]);
    }
}
