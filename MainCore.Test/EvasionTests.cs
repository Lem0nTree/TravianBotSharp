using MainCore.Entities;
using MainCore.Infrasturecture.Persistence;
using MainCore.Services.Evasion;
using MainCore.Tasks.Base;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MainCore.Test.Evasion;

public class EvasionTests
{
    private sealed class Ordinary : BaseTask { }
    private sealed class Scan : BaseTask, IEvasionTask { }
    private sealed class Deadline : BaseTask, IEvasionDeadlineTask { }

    [Fact]
    public void SchedulerPrioritizesDueEvasionAndReservesBrowserForDeadline()
    {
        var now = DateTime.Now;
        var ordinary = new Ordinary { ExecuteAt = now.AddMinutes(-1) };
        var scan = new Scan { ExecuteAt = now };
        var deadline = new Deadline { ExecuteAt = now.AddSeconds(15) };
        EvasionScheduling.Select([ordinary, scan, deadline], now, TimeSpan.FromSeconds(20)).ShouldBe(scan);
        EvasionScheduling.Select([ordinary, deadline], now, TimeSpan.FromSeconds(20)).ShouldBeNull();
        deadline.ExecuteAt = now.AddSeconds(2);
        EvasionScheduling.Select([ordinary, scan, deadline], now, TimeSpan.FromSeconds(20)).ShouldBeNull();
        EvasionScheduling.Select([ordinary, deadline], now, TimeSpan.FromSeconds(20)).ShouldBeNull();
        deadline.ExecuteAt = now;
        EvasionScheduling.Select([ordinary, scan, deadline], now, TimeSpan.FromSeconds(20)).ShouldBe(deadline);
        deadline.ExecuteAt = now.AddMinutes(1);
        EvasionScheduling.Select([ordinary, scan, deadline], now, TimeSpan.FromSeconds(20)).ShouldBe(scan);
        EvasionScheduling.Select([ordinary, deadline], now, TimeSpan.FromSeconds(20)).ShouldBe(ordinary);
    }
    [Fact]
    public void ResourceAllocationUsesStrictPriorityAndActualCapacity()
    {
        EvasionRules.Allocate([50, 70, 30, 20], 100).ShouldBe([50, 50, 0, 0]);
        EvasionRules.Allocate([0, 1, 2, 3], 100).ShouldBe([0, 1, 2, 3]);
        Should.Throw<ArgumentException>(() => EvasionRules.Allocate([1, -1, 0, 0], 10));
    }

    [Fact]
    public void WindowsDoNotSendEarlyOrAfterImpact()
    {
        var impact = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        EvasionRules.IsSendWindow(impact.AddSeconds(-31), impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)).ShouldBeFalse();
        EvasionRules.IsSendWindow(impact.AddSeconds(-30), impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)).ShouldBeTrue();
        EvasionRules.IsSendWindow(impact.AddSeconds(-27), impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3)).ShouldBeTrue();
        EvasionRules.IsSendWindow(impact.AddSeconds(-26), impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3)).ShouldBeFalse();
        EvasionRules.IsSendWindow(impact.AddSeconds(-17), impact, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3)).ShouldBeTrue();
        EvasionRules.IsSendWindow(impact.AddSeconds(-16), impact, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3)).ShouldBeFalse();
        EvasionRules.IsSendWindow(impact, impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public void CapitalUnknownOrUnownedDestinationCannotSendTroops()
    {
        int[] owned = [1, 2];
        EvasionRules.CanSendTroops(null, 1, 2, owned).ShouldBeFalse();
        EvasionRules.CanSendTroops(true, 1, 2, owned).ShouldBeFalse();
        EvasionRules.CanSendTroops(false, 1, 3, owned).ShouldBeFalse();
        EvasionRules.CanSendTroops(false, 1, 1, owned).ShouldBeFalse();
        EvasionRules.CanSendTroops(false, 1, 2, owned).ShouldBeTrue();
    }

    [Fact]
    public void MapTileUsesVerifiedSourceCoordinatesInsteadOfVillageIdOrName()
    {
        MapTileIdentity.TargetTile(92039, 9, -29, 10, -23).ShouldBe(89634L);
        MapTileIdentity.TargetTile(344039, 9, -29, 10, -23).ShouldBe(339234L);
        MapTileIdentity.TargetTile(92038, 9, -29, 10, -23).ShouldBeNull();
        MapTileIdentity.TargetTile(92039, 9, -29, 9999, -23).ShouldBeNull();
    }

    [Fact]
    public void CancellationWaitsForAllWavesAndMustFitBothLimits()
    {
        var sent = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        EvasionRules.SafeCancelAt(sent, sent.AddMinutes(3), [sent.AddSeconds(20), sent.AddSeconds(40)], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).ShouldBe(sent.AddSeconds(41));
        EvasionRules.SafeCancelAt(sent, sent.AddMinutes(3), [sent.AddSeconds(20), sent.AddSeconds(60)], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).ShouldBe(sent.AddSeconds(61));
        EvasionRules.SafeCancelAt(sent, sent.AddMinutes(3), [sent.AddSeconds(20), sent.AddSeconds(60), sent.AddSeconds(100)], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).ShouldBeNull();
        EvasionRules.SafeCancelAt(sent, sent.AddMinutes(3), [sent.AddSeconds(20), sent.AddSeconds(80)], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).ShouldBe(sent.AddSeconds(21));
        EvasionRules.SafeCancelAt(sent, sent.AddSeconds(40), [sent.AddSeconds(40)], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).ShouldBeNull();
        EvasionRules.SafeCancelAt(sent, sent.AddMinutes(3), [sent.AddSeconds(89)], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).ShouldBeNull();
        EvasionRules.SafeCancelAt(sent, sent.AddMinutes(10), [sent.AddSeconds(20), sent.AddMinutes(5)], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).ShouldBe(sent.AddSeconds(21));
    }

    [Fact]
    public void DurableTransitionsBlockDuplicateSendsAndRequireExactMovement()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using (var seed = new AppDbContext(options))
        {
            seed.Database.EnsureCreated();
            // Existing installations predate the additive journal table.
            seed.Database.ExecuteSqlRaw("DROP TABLE \"EvasionDispatches\"");
            seed.Accounts.Add(new Account { Id = 1, Username = "test", Server = "https://example.invalid" });
            seed.Villages.AddRange(new Village { Id = 1, AccountId = 1, Name = "source" }, new Village { Id = 2, AccountId = 1, Name = "target" });
            seed.SaveChanges();
        }
        var key = new EvasionDispatch { AccountId = 1, VillageId = 1, DestinationId = 2, AttackId = "attack-1", Kind = "troop", ImpactUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"), Payload = "{}" };
        using (var first = new AppDbContext(options))
        {
            EvasionStore.TryPlan(first, key).ShouldBeTrue();
            EvasionStore.TryPlan(first, key).ShouldBeFalse();
            EvasionStore.BeginSubmit(first, key).ShouldBeTrue();
        }
        using (var restarted = new AppDbContext(options))
        {
            EvasionStore.BeginSubmit(restarted, key).ShouldBeFalse();
            var now = DateTimeOffset.UtcNow;
            EvasionStore.RecordSent(restarted, key, "movement-1", now, now.AddMinutes(2)).ShouldBeTrue();
            EvasionStore.BeginCancel(restarted, key, "other").ShouldBeFalse();
            EvasionStore.BeginCancel(restarted, key, "movement-1").ShouldBeTrue();
            EvasionStore.BeginCancel(restarted, key, "movement-1").ShouldBeFalse();
            EvasionStore.RecordCancelled(restarted, key, "movement-1").ShouldBeTrue();
        }
    }

    [Fact]
    public void ReconciliationRequiresExactlyOneNewMatchingMovement()
    {
        var now = DateTimeOffset.UtcNow;
        var movement = new ObservedMovement("m1", "troop", 1, 2, [10, 20], now, now.AddMinutes(3));
        EvasionReconciliation.FindUniqueNewMovement([], [movement], "troop", 1, 2, [10, 20], now.AddSeconds(-1), now.AddSeconds(1)).ShouldBe(movement);
        EvasionReconciliation.FindUniqueNewMovement(["m1"], [movement], "troop", 1, 2, [10, 20], now.AddSeconds(-1), now.AddSeconds(1)).ShouldBeNull();
        EvasionReconciliation.FindUniqueNewMovement([], [movement, movement with { Id = "m2" }], "troop", 1, 2, [10, 20], now.AddSeconds(-1), now.AddSeconds(1)).ShouldBeNull();
        EvasionReconciliation.FindUniqueNewMovement([], [movement], "troop", 1, 3, [10, 20], now.AddSeconds(-1), now.AddSeconds(1)).ShouldBeNull();
    }
}
