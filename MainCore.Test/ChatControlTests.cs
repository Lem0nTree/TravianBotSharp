using System.Text.Json;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Services.ChatControl;

namespace MainCore.Test;

public class ChatControlTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    private static (ApplicationControlService Service, ControlMutations Mutations) Control(TaskExecutionHarness h)
    {
        var mutations = new ControlMutations(h.Factory, h.Tasks, h.Notifications);
        return (new ApplicationControlService(h.Factory, h.Tasks, mutations, new ObservationTracker(h.Notifications)), mutations);
    }
    [Fact]
    public async Task QueryRequiresVillageOwnershipAndDoesNotExposeAccesses()
    {
        using var h = new TaskExecutionHarness();
        var (service, _) = Control(h);
        var accounts = await service.ExecuteAsync("list_accounts", Args(new { }), TestContext.Current.CancellationToken);
        Json(accounts.Result).GetRawText().ShouldNotContain("Accesses");
        var wrong = await service.ExecuteAsync("get_village_snapshot", Args(new { accountId = 1, villageId = 999 }), TestContext.Current.CancellationToken);
        wrong.ErrorCode.ShouldBe("village_not_found");
        var snapshot = await service.ExecuteAsync("get_village_snapshot", Args(new { accountId = 1, villageId = 1 }), TestContext.Current.CancellationToken);
        Json(snapshot.Result).GetProperty("Data").GetProperty("ObservedAt").GetProperty("Resources").ValueKind.ShouldBe(JsonValueKind.Null);
    }
    [Fact]
    public async Task QueueEditsRequireQuiescenceAndRevisionAndReplayOnce()
    {
        using var h = new TaskExecutionHarness();
        using (var db = h.Factory.CreateDbContext())
        {
            db.Jobs.AddRange(new Job { VillageId = 1, Position = 0, Type = JobTypeEnums.NormalBuild, Content = "{}" }, new Job { VillageId = 1, Position = 1, Type = JobTypeEnums.NormalBuild, Content = "{}" });
            db.SaveChanges();
        }
        var (service, _) = Control(h);
        var queue = await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), TestContext.Current.CancellationToken);
        var data = Json(queue.Result).GetProperty("Data");
        var revision = data.GetProperty("Revision").GetString()!;
        var job = data.GetProperty("Jobs")[0].GetProperty("Id").GetInt32();
        var input = Args(new { accountId = 1, villageId = 1, jobId = job, expectedRevision = revision, requestId = "one" });
        (await service.ExecuteAsync("remove_build_job", input, TestContext.Current.CancellationToken)).ErrorCode.ShouldBe("account_not_quiescent");
        h.Tasks.SetStatus(new(1), StatusEnums.Paused);
        var first = await service.ExecuteAsync("remove_build_job", input, TestContext.Current.CancellationToken);
        first.ErrorCode.ShouldBeNull();
        var replay = await service.ExecuteAsync("remove_build_job", input, TestContext.Current.CancellationToken);
        Json(replay.Result).GetRawText().ShouldBe(Json(first.Result).GetRawText());
        var stale = await service.ExecuteAsync("remove_build_job", Args(new { accountId = 1, villageId = 1, jobId = job, expectedRevision = revision, requestId = "two" }), TestContext.Current.CancellationToken);
        stale.ErrorCode.ShouldBe("revision_conflict");
        var collision = await service.ExecuteAsync("remove_build_job", Args(new { accountId = 1, villageId = 1, jobId = 999, expectedRevision = revision, requestId = "one" }), TestContext.Current.CancellationToken);
        collision.ErrorCode.ShouldBe("request_id_collision");
        using var verify = h.Factory.CreateDbContext();
        verify.Jobs.Single().Position.ShouldBe(0);
    }
    [Fact]
    public async Task InvalidSettingsAreAtomic()
    {
        using var h = new TaskExecutionHarness();
        var (service, _) = Control(h);
        var before = await service.ExecuteAsync("get_village_settings", Args(new { accountId = 1, villageId = 1 }), TestContext.Current.CancellationToken);
        var revision = Json(before.Result).GetProperty("Data").GetProperty("Revision").GetString();
        var invalid = await service.ExecuteAsync("update_village_settings", Args(new { accountId = 1, villageId = 1, patch = new { AutoBuildEnable = 1, AutoRefreshMin = -1 }, expectedRevision = revision, requestId = "setting" }), TestContext.Current.CancellationToken);
        invalid.ErrorCode.ShouldBe("invalid_settings");
        var after = await service.ExecuteAsync("get_village_settings", Args(new { accountId = 1, villageId = 1 }), TestContext.Current.CancellationToken);
        Json(after.Result).GetProperty("Data").GetProperty("Revision").GetString().ShouldBe(revision);
    }
    [Theory]
    [InlineData(StatusEnums.Online)]
    [InlineData(StatusEnums.Starting)]
    [InlineData(StatusEnums.Pausing)]
    [InlineData(StatusEnums.Stopping)]
    public async Task QueueChangesRejectNonQuiescentStates(StatusEnums status)
    {
        using var h = new TaskExecutionHarness();
        using (var db = h.Factory.CreateDbContext()) { db.Jobs.Add(new Job { VillageId = 1, Position = 0, Type = JobTypeEnums.NormalBuild, Content = "{}" }); db.SaveChanges(); }
        var (service, _) = Control(h);
        var queue = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), TestContext.Current.CancellationToken)).Result).GetProperty("Data");
        h.Tasks.SetStatus(new(1), status);
        var response = await service.ExecuteAsync("remove_build_job", Args(new { accountId = 1, villageId = 1, jobId = queue.GetProperty("Jobs")[0].GetProperty("Id").GetInt32(), expectedRevision = queue.GetProperty("Revision").GetString(), requestId = "blocked" }), TestContext.Current.CancellationToken);
        response.ErrorCode.ShouldBe("account_not_quiescent");
    }
    [Fact]
    public async Task SettingsAndMoveUpdateRevisionsAndRejectForeignJobs()
    {
        using var h = new TaskExecutionHarness();
        h.Tasks.SetStatus(new(1), StatusEnums.Paused);
        using (var db = h.Factory.CreateDbContext()) { db.Jobs.AddRange(new Job { VillageId = 1, Position = 0, Type = JobTypeEnums.NormalBuild, Content = "{}" }, new Job { VillageId = 1, Position = 1, Type = JobTypeEnums.NormalBuild, Content = "{}" }, new Job { VillageId = 2, Position = 0, Type = JobTypeEnums.NormalBuild, Content = "{}" }); db.SaveChanges(); }
        var (service, _) = Control(h); var ct = TestContext.Current.CancellationToken;
        var settings = Json((await service.ExecuteAsync("get_village_settings", Args(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        var revision = settings.GetProperty("Revision").GetString();
        var changed = await service.ExecuteAsync("update_village_settings", Args(new { accountId = 1, villageId = 1, patch = new { AutoRefreshMin = 20, AutoRefreshMax = 25 }, expectedRevision = revision, requestId = "settings" }), ct);
        changed.ErrorCode.ShouldBeNull();
        Json(changed.Result).GetProperty("Data").GetProperty("Revision").GetString().ShouldNotBe(revision);
        var queue = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        var queueRevision = queue.GetProperty("Revision").GetString();
        var moving = queue.GetProperty("Jobs")[1].GetProperty("Id").GetInt32();
        var other = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 2 }), ct)).Result).GetProperty("Data").GetProperty("Jobs")[0].GetProperty("Id").GetInt32();
        var wrong = await service.ExecuteAsync("move_build_job", Args(new { accountId = 1, villageId = 1, jobId = other, position = 0, expectedRevision = queueRevision, requestId = "foreign" }), ct);
        wrong.ErrorCode.ShouldBe("job_not_found");
        var moved = await service.ExecuteAsync("move_build_job", Args(new { accountId = 1, villageId = 1, jobId = moving, position = 0, expectedRevision = queueRevision, requestId = "move" }), ct);
        moved.ErrorCode.ShouldBeNull();
        var after = Json(moved.Result).GetProperty("Data");
        after.GetProperty("Revision").GetString().ShouldNotBe(queueRevision);
        var reread = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        reread.GetProperty("Jobs")[0].GetProperty("Id").GetInt32().ShouldBe(moving);
    }
    [Fact]
    public async Task LargeQueueRevisionIncludesRowsBeyondVisibleLimit()
    {
        using var h = new TaskExecutionHarness(); h.Tasks.SetStatus(new(1), StatusEnums.Paused);
        using (var db = h.Factory.CreateDbContext()) { db.Jobs.AddRange(Enumerable.Range(0, 501).Select(i => new Job { VillageId = 1, Position = i, Type = JobTypeEnums.NormalBuild, Content = "{}" })); db.SaveChanges(); }
        var (service, _) = Control(h); var ct = TestContext.Current.CancellationToken;
        var queue = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        queue.GetProperty("Total").GetInt32().ShouldBe(501); queue.GetProperty("Truncated").GetBoolean().ShouldBeTrue();
        var job = queue.GetProperty("Jobs")[0].GetProperty("Id").GetInt32();
        var result = await service.ExecuteAsync("remove_build_job", Args(new { accountId = 1, villageId = 1, jobId = job, expectedRevision = queue.GetProperty("Revision").GetString(), requestId = "large" }), ct);
        result.ErrorCode.ShouldBeNull();
    }
    [Fact]
    public async Task ResumeIsRejectedWhilePausedTaskStillFinishing()
    {
        using var h = new TaskExecutionHarness(); h.Tasks.SetStatus(new(1), StatusEnums.Paused); h.Tasks.GetTaskQueue(new(1)).IsExecuting = true;
        var (service, _) = Control(h);
        var response = await service.ExecuteAsync("set_account_paused", Args(new { accountId = 1, paused = false, requestId = "resume" }), TestContext.Current.CancellationToken);
        response.ErrorCode.ShouldBe("account_not_quiescent"); h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
    }
    [Fact]
    public async Task AddObservedBuildingValidatesTargetAndAppendsOnlyOnce()
    {
        using var h = new TaskExecutionHarness(); h.Tasks.SetStatus(new(1), StatusEnums.Paused);
        using (var db = h.Factory.CreateDbContext())
        {
            db.Buildings.AddRange(Enumerable.Range(1, 38).Select(i => new Building { VillageId = 1, Location = i, Type = i <= 18 ? BuildingEnums.Woodcutter : i == 26 ? BuildingEnums.MainBuilding : BuildingEnums.Site, Level = i <= 18 || i == 26 ? 1 : 0 }));
            db.VillagesSetting.AddRange(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.Tribe, Value = (int)TribeEnums.Romans }, new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.ConfirmedCapitalStatus, Value = 2 });
            var storage = db.Storages.Single(x => x.VillageId == 1); storage.Warehouse = 1000000; storage.Granary = 1000000; db.SaveChanges();
        }
        var (service, _) = Control(h); var ct = TestContext.Current.CancellationToken;
        var queue = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        var revision = queue.GetProperty("Revision").GetString();
        var invalid = await service.ExecuteAsync("add_build_job", Args(new { accountId = 1, villageId = 1, slot = 1, targetLevel = 11, expectedRevision = revision, requestId = "bad-level" }), ct);
        invalid.ErrorCode.ShouldBe("invalid_level");
        var args = Args(new { accountId = 1, villageId = 1, slot = 1, targetLevel = 3, expectedRevision = revision, requestId = "add" });
        var added = await service.ExecuteAsync("add_build_job", args, ct); added.ErrorCode.ShouldBeNull();
        (await service.ExecuteAsync("add_build_job", args, ct)).ErrorCode.ShouldBeNull();
        var reread = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        reread.GetProperty("Total").GetInt32().ShouldBe(1);
        reread.GetProperty("Jobs")[0].GetProperty("Target").GetProperty("Level").GetInt32().ShouldBe(3);
        using (var db = h.Factory.CreateDbContext()) { db.Jobs.Add(new Job { VillageId = 1, Position = 1, Type = JobTypeEnums.ResourceBuild, Content = JsonSerializer.Serialize(new { Level = 5, Plan = 0 }) }); db.SaveChanges(); }
        var withGroup = Json((await service.ExecuteAsync("get_build_queue", Args(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        var redundant = await service.ExecuteAsync("add_build_job", Args(new { accountId = 1, villageId = 1, slot = 1, targetLevel = 5, expectedRevision = withGroup.GetProperty("Revision").GetString(), requestId = "group" }), ct);
        redundant.ErrorCode.ShouldBe("invalid_level");
    }
}
