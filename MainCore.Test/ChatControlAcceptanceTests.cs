using System.IO.Pipes;
using System.Text.Json;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Infrasturecture.Persistence;
using MainCore.Services;
using MainCore.Services.ChatControl;
using MainCore.Services.Telegram;
using Microsoft.EntityFrameworkCore;
using TravianBotSharp.ControlProtocol;

namespace MainCore.Test;

public sealed class ChatControlAcceptanceTests
{
    [Fact]
    public async Task FailureAfterCommitReportsUnknownOutcomeAndCannotReplayTheWrite()
    {
        using var h = new TaskExecutionHarness();
        using (var db = h.Factory.CreateDbContext())
        {
            db.Jobs.Add(new Job { VillageId = 1, Position = 0, Type = JobTypeEnums.NormalBuild, Content = "{}" });
            db.SaveChanges();
        }
        // Task insertion validates ownership after the queue transaction committed.
        // Simulate that separate scheduler database access failing at that point.
        var failingFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        failingFactory.CreateDbContext().Returns(_ => throw new InvalidOperationException("simulated scheduler failure"));
        var tasks = new TaskManager(h.Notifications, failingFactory, h.Logger);
        tasks.SetStatus(new(1), StatusEnums.Paused);
        using var observations = new ObservationTracker(h.Notifications);
        var service = new ApplicationControlService(h.Factory, tasks, new(h.Factory, tasks, h.Notifications), observations);
        var ct = TestContext.Current.CancellationToken;
        var queue = JsonSerializer.SerializeToElement((await service.ExecuteAsync("get_build_queue", JsonSerializer.SerializeToElement(new { accountId = 1, villageId = 1 }), ct)).Result).GetProperty("Data");
        var request = JsonSerializer.SerializeToElement(new
        {
            accountId = 1, villageId = 1,
            jobId = queue.GetProperty("Jobs")[0].GetProperty("Id").GetInt32(),
            expectedRevision = queue.GetProperty("Revision").GetString(), requestId = "post-commit-failure"
        });
        var first = await service.ExecuteAsync("remove_build_job", request, ct);
        first.ErrorCode.ShouldBe("mutation_outcome_unknown");
        var replay = await service.ExecuteAsync("remove_build_job", request, ct);
        replay.ShouldBe(first);
        using var verify = h.Factory.CreateDbContext();
        verify.Jobs.ShouldBeEmpty();
        failingFactory.Received(1).CreateDbContext();
    }

    [Fact]
    public async Task ObservationsDistinguishFieldsCenterResourcesAndOtherVillages()
    {
        using var h = new TaskExecutionHarness();
        using var tracker = new ObservationTracker(h.Notifications);
        tracker.Get(1, 1).Resources.ShouldBeNull();
        h.Notifications.Enqueue(new TelegramVillageScan(new(1), new(1), Buildings: [new(1, BuildingEnums.Woodcutter, 1)]));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (tracker.Get(1, 1).Fields is null) await Task.Delay(10, timeout.Token);
        tracker.Get(1, 1).Center.ShouldBeNull();
        tracker.Get(1, 1).Resources.ShouldBeNull();
        tracker.Get(1, 2).Fields.ShouldBeNull();
        h.Notifications.Enqueue(new TelegramVillageScan(new(1), new(1), UnderAttack: true, Storage: new(1, 2, 3, 4, 100, 100)));
        while (tracker.Get(1, 1).Resources is null) await Task.Delay(10, timeout.Token);
        tracker.Get(1, 1).Attack.ShouldNotBeNull();
        tracker.Get(1, 1).Center.ShouldBeNull();
    }
}

[Collection("MCP bridge")]
public sealed class ControlProtocolAcceptanceTests
{
    [Fact]
    public async Task UnsupportedVersionDoesNotStopBridgeAndIdleClientCannotBlockShutdown()
    {
        using var h = new TaskExecutionHarness();
        using var observations = new ObservationTracker(h.Notifications);
        using var bridge = new ControlBridge(new(h.Factory, h.Tasks, new(h.Factory, h.Tasks, h.Notifications), observations));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await bridge.StartAsync(timeout.Token);
        try
        {
            await using (var pipe = NewPipe())
            {
                await pipe.ConnectAsync(timeout.Token);
                await PipeProtocol.WriteAsync(pipe, new ControlRequest(999, "list_accounts", JsonSerializer.SerializeToElement(new { })), timeout.Token);
                (await PipeProtocol.ReadAsync<ControlResponse>(pipe, timeout.Token)).ErrorCode.ShouldBe("protocol_version");
            }
            await using (var pipe = NewPipe())
            {
                await pipe.ConnectAsync(timeout.Token);
                await PipeProtocol.WriteAsync(pipe, new ControlRequest(PipeProtocol.Version, "list_accounts", JsonSerializer.SerializeToElement(new { })), timeout.Token);
                (await PipeProtocol.ReadAsync<ControlResponse>(pipe, timeout.Token)).ErrorCode.ShouldBeNull();
            }
            await using var idle = NewPipe();
            await idle.ConnectAsync(timeout.Token);
            await bridge.StopAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { await bridge.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken); }
    }

    private static NamedPipeClientStream NewPipe() => new(".", PipeProtocol.Name(AppContext.BaseDirectory), PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
