using System.Text.Json;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Services.ChatControl;
using MainCore.Tasks;

namespace MainCore.Test;

public class ChatTrainingSettingsTests
{
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    private static async Task<string> Revision(ApplicationControlService service) => Json((await service.ExecuteAsync("get_village_settings", Json(new { accountId = 1, villageId = 1 }), TestContext.Current.CancellationToken)).Result).GetProperty("Data").GetProperty("Revision").GetString()!;
    private static async Task<TravianBotSharp.ControlProtocol.ControlResponse> Patch(ApplicationControlService service, object patch, string rev, string id) => await service.ExecuteAsync("update_village_settings", Json(new { accountId = 1, villageId = 1, patch, expectedRevision = rev, requestId = id }), TestContext.Current.CancellationToken);
    private static ApplicationControlService Prepare(TaskExecutionHarness h)
    {
        using var db = h.Factory.CreateDbContext();
        db.VillagesSetting.AddRange(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.Tribe, Value = (int)TribeEnums.Teutons }, new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.BarrackTroop, Value = (int)TroopEnums.Scout }, new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.TrainTroopEnable, Value = 1 });
        db.VillagesSetting.AddRange(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.BarrackAmountMin, Value = 10 }, new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.BarrackAmountMax, Value = 50 }, new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.TrainTroopRepeatTimeMin, Value = 60 }, new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.TrainTroopRepeatTimeMax, Value = 120 });
        db.Buildings.Add(new Building { VillageId = 1, Location = 20, Type = BuildingEnums.Barracks, Level = 3 });
        db.SaveChanges();
        h.Tasks.SetStatus(new(1), StatusEnums.Paused);
        var mutations = new ControlMutations(h.Factory, h.Tasks, h.Notifications);
        return new ApplicationControlService(h.Factory, h.Tasks, mutations, new ObservationTracker(h.Notifications));
    }
    [Fact]
    public async Task SelectSpearmanResetsPartialWaitingTaskOnceAndPreservesRanges()
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h);
        var old = new TrainTroopTask.Task(new(1), new(1)) { ExecuteAt = DateTime.Now.AddHours(1) };
        var completed = (HashSet<BuildingEnums>)typeof(TrainTroopTask.Task).GetProperty("CompletedBuildings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(old)!;
        completed.Add(BuildingEnums.Barracks); h.Tasks.Add(old);
        var query = Json((await service.ExecuteAsync("get_village_settings", Json(new { accountId = 1, villageId = 1 }), TestContext.Current.CancellationToken)).Result).GetProperty("Data");
        var options = query.GetProperty("TrainingOptions").GetProperty("Buildings");
        options[0].GetProperty("Choices").EnumerateArray().Any(x => x.GetProperty("Id").GetInt32() == 12 && x.GetProperty("Name").GetString() == "Spearman").ShouldBeTrue();
        var revision = query.GetProperty("Revision").GetString()!;
        var patch = new { BarrackTroop = 12, TrainTroopEnable = 1, StableTroop = 0, GreatBarrackTroop = 0, GreatStableTroop = 0, WorkshopTroop = 0 };
        var result = await Patch(service, patch, revision, "spears"); result.ErrorCode.ShouldBeNull();
        var now = h.Tasks.GetTaskList(new(1)).OfType<TrainTroopTask.Task>().Single();
        now.ShouldNotBeSameAs(old); now.ExecuteAt.ShouldBeLessThan(DateTime.Now.AddSeconds(5));
        var newCompleted = (HashSet<BuildingEnums>)typeof(TrainTroopTask.Task).GetProperty("CompletedBuildings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(now)!;
        newCompleted.ShouldBeEmpty();
        (await Patch(service, patch, revision, "spears")).ErrorCode.ShouldBeNull();
        h.Tasks.GetTaskList(new(1)).OfType<TrainTroopTask.Task>().Single().ShouldBeSameAs(now);
        using var db = h.Factory.CreateDbContext();
        db.VillagesSetting.Single(x => x.VillageId == 1 && x.Setting == VillageSettingEnums.BarrackTroop).Value.ShouldBe(12);
        db.VillagesSetting.Single(x => x.VillageId == 1 && x.Setting == VillageSettingEnums.BarrackAmountMin).Value.ShouldBe(10);
        db.VillagesSetting.Single(x => x.VillageId == 1 && x.Setting == VillageSettingEnums.BarrackAmountMax).Value.ShouldBe(50);
        db.VillagesSetting.Single(x => x.VillageId == 1 && x.Setting == VillageSettingEnums.TrainTroopRepeatTimeMin).Value.ShouldBe(60);
    }
    [Theory]
    [InlineData(1)] // Roman infantry, wrong tribe
    [InlineData(15)] // Teuton cavalry, wrong building
    [InlineData(999)]
    public async Task InvalidTroopsRejectedAtomically(int troop)
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h); var rev = await Revision(service);
        var response = await Patch(service, new { BarrackTroop = troop, AutoBuildEnable = 1 }, rev, "invalid");
        response.ErrorCode.ShouldBe("invalid_settings"); (await Revision(service)).ShouldBe(rev);
    }
    [Fact]
    public async Task EnableWithNoneOrMissingBuildingRejected()
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h); var rev = await Revision(service);
        (await Patch(service, new { BarrackTroop = 0, TrainTroopEnable = 1 }, rev, "empty")).ErrorCode.ShouldBe("invalid_settings");
        using (var db = h.Factory.CreateDbContext()) { db.Buildings.RemoveRange(db.Buildings); db.SaveChanges(); }
        (await Patch(service, new { BarrackTroop = 12 }, rev, "missing")).ErrorCode.ShouldBe("invalid_settings");
        (await Revision(service)).ShouldBe(rev);
    }
    [Theory]
    [InlineData(StatusEnums.Online)] [InlineData(StatusEnums.Starting)] [InlineData(StatusEnums.Pausing)] [InlineData(StatusEnums.Stopping)]
    public async Task TrainingEditsRequireQuiescence(StatusEnums status)
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h); h.Tasks.SetStatus(new(1), status); var rev = await Revision(service);
        (await Patch(service, new { BarrackTroop = 12 }, rev, "blocked")).ErrorCode.ShouldBe("account_not_quiescent"); (await Revision(service)).ShouldBe(rev);
    }
    [Fact]
    public async Task ExecutingRejectedUnsafeKeysBlockedAndLegacySettingsStillEditableOnline()
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h); var rev = await Revision(service);
        h.Tasks.GetTaskQueue(new(1)).IsExecuting = true;
        (await Patch(service, new { BarrackTroop = 12 }, rev, "executing")).ErrorCode.ShouldBe("account_not_quiescent");
        h.Tasks.GetTaskQueue(new(1)).IsExecuting = false; h.Tasks.SetStatus(new(1), StatusEnums.Online);
        (await Patch(service, new { TrainWhenLowResource = 1 }, rev, "unsafe")).ErrorCode.ShouldBe("invalid_settings");
        (await Patch(service, new { AutoBuildEnable = 1 }, rev, "legacy")).ErrorCode.ShouldBeNull();
    }
    [Fact]
    public async Task HeldTrainingRemainsHeldWhenReplaced()
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h);
        var future = DateTime.Now.AddMinutes(30);
        h.Tasks.Add(new TrainTroopTask.Task(new(1), new(1)) { ExecuteAt = future, IsQuarantined = true, FailureStatus = "held" });
        var rev = await Revision(service); (await Patch(service, new { BarrackTroop = 12 }, rev, "held")).ErrorCode.ShouldBeNull();
        var task = h.Tasks.GetTaskList(new(1)).OfType<TrainTroopTask.Task>().Single();
        task.IsQuarantined.ShouldBeTrue(); task.FailureStatus.ShouldBe("held"); task.ExecuteAt.ShouldBe(future);
    }
    [Fact]
    public async Task MissingOrInvalidSavedTrainingRangesAreRejected()
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h); var rev = await Revision(service);
        using (var db = h.Factory.CreateDbContext()) { db.VillagesSetting.Remove(db.VillagesSetting.Single(x => x.VillageId == 1 && x.Setting == VillageSettingEnums.BarrackAmountMin)); db.SaveChanges(); }
        rev = await Revision(service);
        (await Patch(service, new { BarrackTroop = 12 }, rev, "missing-amount")).ErrorCode.ShouldBe("invalid_settings");
        using (var db = h.Factory.CreateDbContext()) { db.VillagesSetting.Add(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.BarrackAmountMin, Value = 10 }); db.VillagesSetting.Single(x => x.VillageId == 1 && x.Setting == VillageSettingEnums.TrainTroopRepeatTimeMin).Value = 0; db.SaveChanges(); }
        rev = await Revision(service);
        (await Patch(service, new { BarrackTroop = 12 }, rev, "invalid-repeat")).ErrorCode.ShouldBe("invalid_settings");
    }
    [Fact]
    public async Task OfflineCanEnableAndHeldDisableIsRejected()
    {
        using var h = new TaskExecutionHarness(); var service = Prepare(h); h.Tasks.SetStatus(new(1), StatusEnums.Offline);
        var rev = await Revision(service);
        (await Patch(service, new { BarrackTroop = 12 }, rev, "offline")).ErrorCode.ShouldBeNull();
        var task = h.Tasks.GetTaskList(new(1)).OfType<TrainTroopTask.Task>().Single();
        task.IsQuarantined = true; task.FailureStatus = "held";
        rev = await Revision(service);
        (await Patch(service, new { TrainTroopEnable = 0 }, rev, "held-disable")).ErrorCode.ShouldBe("task_held");
        h.Tasks.GetTaskList(new(1)).OfType<TrainTroopTask.Task>().Single().ShouldBeSameAs(task);
    }
}
