using System.Collections.Concurrent;
using FluentResults;
using Immediate.Handlers.Shared;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Errors;
using MainCore.Infrasturecture.Persistence;
using MainCore.Notifications;
using MainCore.Services;
using MainCore.Services.Telegram;
using MainCore.Tasks;
using MainCore.Tasks.Base;
using MainCore.UI.Models.Output;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace MainCore.Test;

internal sealed class TaskExecutionHarness : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    public IDbContextFactory<AppDbContext> Factory { get; } = Substitute.For<IDbContextFactory<AppDbContext>>();
    public IChromeBrowser Browser { get; }
    public IPage Page { get; }
    public IHandler<UpgradeBuildingTask.Task, Result> Build { get; } = Substitute.For<IHandler<UpgradeBuildingTask.Task, Result>>();
    public IHandler<TrainTroopTask.Task, Result> Train { get; } = Substitute.For<IHandler<TrainTroopTask.Task, Result>>();
    public IHandler<NpcTask.Task, Result> Npc { get; } = Substitute.For<IHandler<NpcTask.Task, Result>>();
    public RxQueue Notifications { get; }
    public TaskManager Tasks { get; }
    public TaskFailureController Failures { get; }
    public TimerManager Timer { get; }
    public DateTimeOffset Clock { get; set; } = DateTimeOffset.UtcNow;
    public ConcurrentQueue<TelegramAlert> Alerts { get; } = new();
    private readonly IDisposable _subscription;
    public Serilog.Core.Logger Logger { get; } = new Serilog.LoggerConfiguration().CreateLogger();

    public TaskExecutionHarness(IPage? page = null)
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        Factory.CreateDbContext().Returns(_ => new AppDbContext(options));
        using (var db = Factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Accounts.Add(new Account { Id = 1, Username = "test", Server = "https://game.invalid" });
            db.AccountsInfo.Add(new AccountInfo { AccountId = 1, Gold = 10 });
            db.Villages.AddRange(new Village { Id = 1, AccountId = 1, Name = "One" }, new Village { Id = 2, AccountId = 1, Name = "Two" });
            db.AccountsSetting.AddRange(new AccountSetting { AccountId = 1, Setting = AccountSettingEnums.TaskDelayMin, Value = 0 }, new AccountSetting { AccountId = 1, Setting = AccountSettingEnums.TaskDelayMax, Value = 0 });
            db.VillagesSetting.AddRange(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.AutoNPCEnable, Value = 0 }, new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.AutoNPCGranaryPercent, Value = 10 });
            db.Storages.Add(new Storage { VillageId = 1, Granary = 100, Crop = 100, Warehouse = 100 });
            db.SaveChanges();
        }
        Page = page ?? Substitute.For<IPage>();
        if (page is null)
        {
            var empty = Substitute.For<ILocator>();
            empty.CountAsync().Returns(0);
            Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(empty);
        }
        Browser = Substitute.For<IChromeBrowser>();
        Browser.CurrentPage.Returns(Page);
        Browser.IsInitialized.Returns(true);
        Browser.CurrentUrl.Returns(_ => page is null ? "https://game.invalid/build.php?id=26" : Page.Url);
        Browser.AccountId.Returns(new AccountId(1));
        Browser.Logger.Returns(Logger);
        Notifications = new(Factory);
        _subscription = Notifications.GetObservable<TelegramAlert>().Subscribe(new Observer<TelegramAlert>(Alerts.Enqueue));
        Tasks = new(Notifications, Factory, Logger);
        Failures = new(Factory, () => Clock);
        var services = new ServiceCollection();
        services.AddScoped<DataService>();
        services.AddScoped(_ => Factory.CreateDbContext());
        services.AddSingleton(Browser);
        services.AddSingleton(Build);
        services.AddSingleton(Train);
        services.AddSingleton(Npc);
        _provider = services.BuildServiceProvider();
        Timer = NewTimer(Tasks, Failures);
        Tasks.SetStatus(new(1), StatusEnums.Online);
        Train.HandleAsync(Arg.Any<TrainTroopTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Result.Ok()));
    }
    public TimerManager NewTimer(TaskManager manager, TaskFailureController controller) =>
        new(manager, new CustomServiceScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>()), Notifications, Factory, controller);
    public UpgradeBuildingTask.Task QueueBuild(int village = 1)
    {
        var task = new UpgradeBuildingTask.Task(new(1), new(village)) { ExecuteAt = DateTime.Now.AddMinutes(-2) };
        Tasks.Add(task); return task;
    }
    public void AdvanceToRetry(BaseTask task)
    {
        Clock = Clock.AddMinutes(16);
        task.ExecuteAt = DateTime.Now.AddSeconds(-1);
    }
    public void Dispose() { _subscription.Dispose(); _provider.Dispose(); _connection.Dispose(); Logger.Dispose(); }
    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    { public void OnNext(T value) => next(value); public void OnCompleted() { } public void OnError(Exception error) { } }
}

public class TaskFailureExecutionTests
{
    [Theory]
    [InlineData("retry")]
    [InlineData("stop")]
    [InlineData("exception")]
    [InlineData("timeout")]
    public async Task FailedConstructionDefersOnlyItsDispatcherAndAnotherDueTaskActuallyRuns(string failure)
    {
        using var h = new TaskExecutionHarness();
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => failure switch
        {
            "exception" => ValueTask.FromException<Result>(new InvalidOperationException("resource panel unreadable")),
            "timeout" => ValueTask.FromException<Result>(new OperationCanceledException("network timeout")),
            "stop" => ValueTask.FromResult(Stop.Error.WithError("input unreadable")),
            _ => ValueTask.FromResult(Retry.Error)
        });
        var build = h.QueueBuild();
        h.Tasks.Add(new TrainTroopTask.Task(new(1), new(2)) { ExecuteAt = DateTime.Now.AddMinutes(-1) });
        await h.Timer.Execute(new(1));
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Online);
        build.IsQuarantined.ShouldBeFalse();
        build.ExecuteAt.ShouldBeGreaterThan(DateTime.Now.AddMinutes(1));
        new TaskItem(build).Task.ShouldContain("Deferred after 1 failure");
        await h.Timer.Execute(new(1));
        await h.Build.Received(1).HandleAsync(build, Arg.Any<CancellationToken>());
        await h.Train.Received(1).HandleAsync(Arg.Any<TrainTroopTask.Task>(), Arg.Any<CancellationToken>());
        h.Tasks.IsExecuting(new(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task UncertainOutcomeSurvivesReenqueueAndRestartWithoutReplayingAction()
    {
        using var h = new TaskExecutionHarness();
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Result.Fail(new UncertainTaskOutcome("response lost"))));
        var build = h.QueueBuild();
        await h.Timer.Execute(new(1));
        build.IsQuarantined.ShouldBeTrue();
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Online);
        h.Tasks.AddOrUpdate(new UpgradeBuildingTask.Task(new(1), new(1)) { ExecuteAt = DateTime.Now.AddMinutes(-1) });
        await h.Timer.Execute(new(1));
        var restartedTasks = new TaskManager(h.Notifications, h.Factory, h.Logger);
        var restarted = new UpgradeBuildingTask.Task(new(1), new(1));
        restartedTasks.Add(restarted);
        restartedTasks.SetStatus(new(1), StatusEnums.Online);
        await h.NewTimer(restartedTasks, new TaskFailureController(h.Factory)).Execute(new(1));
        restarted.IsQuarantined.ShouldBeTrue();
        new TaskItem(restarted).Task.ShouldContain("Needs attention");
        await h.Build.Received(1).HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistentFailuresAlertOnceAndRealProgressFollowedBySkipClearsEpisode()
    {
        using var h = new TaskExecutionHarness();
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Retry.Error));
        var build = h.QueueBuild();
        for (var i = 0; i < 4; i++) { h.AdvanceToRetry(build); await h.Timer.Execute(new(1)); }
        var until = DateTime.UtcNow.AddSeconds(2);
        while (h.Alerts.IsEmpty && DateTime.UtcNow < until) await Task.Delay(10, TestContext.Current.CancellationToken);
        h.Alerts.Count.ShouldBe(1);
        h.Alerts.Single().Kind.ShouldBe(TelegramEvents.Errors);
        h.Alerts.Single().VillageId.ShouldBe(new VillageId(1));
        new TaskItem(build).Task.ShouldContain("failure"); // Local fallback exists independently of Telegram configuration.
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(call =>
        { call.ArgAt<UpgradeBuildingTask.Task>(0).MadeProgress = true; return ValueTask.FromResult(Skip.Error); });
        h.AdvanceToRetry(build);
        await h.Timer.Execute(new(1));
        using var db = h.Factory.CreateDbContext();
        db.TaskFailureStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoOpSkipDoesNotResetFailureHistory()
    {
        using var h = new TaskExecutionHarness();
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Retry.Error));
        var build = h.QueueBuild(); await h.Timer.Execute(new(1));
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Skip.Error));
        h.AdvanceToRetry(build); await h.Timer.Execute(new(1));
        using var db = h.Factory.CreateDbContext();
        db.TaskFailureStates.Single().Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ExplicitUserCancellationStillPausesAndClearsExecutionFlags()
    {
        using var h = new TaskExecutionHarness();
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { h.Tasks.GetTaskQueue(new(1)).CancellationTokenSource!.Cancel(); return ValueTask.FromResult(Cancel.Error); });
        h.QueueBuild(); await h.Timer.Execute(new(1));
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
        h.Tasks.IsExecuting(new(1)).ShouldBeFalse();
        h.Tasks.GetTaskQueue(new(1)).CancellationTokenSource.ShouldBeNull();
        h.Alerts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClosedBrowserIsAccountFatalRatherThanAConstructionRetry()
    {
        using var h = new TaskExecutionHarness();
        h.Page.IsClosed.Returns(true);
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Retry.Error));
        h.QueueBuild(); await h.Timer.Execute(new(1));
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
        using var db = h.Factory.CreateDbContext(); db.TaskFailureStates.ShouldBeEmpty();
        var until = DateTime.UtcNow.AddSeconds(2);
        while (h.Alerts.IsEmpty && DateTime.UtcNow < until) await Task.Delay(10, TestContext.Current.CancellationToken);
        h.Alerts.Single().Kind.ShouldBe(TelegramEvents.Errors);
    }

    [Fact]
    public async Task UnrecoverableOverlayIsAccountFatalAndDoesNotBlindlyRetry()
    {
        using var h = new TaskExecutionHarness();
        var modal = Substitute.For<ILocator>(); modal.CountAsync().Returns(1);
        var empty = Substitute.For<ILocator>(); empty.CountAsync().Returns(0);
        modal.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(empty);
        h.Page.Locator(".dialogOverlay:visible:has(#npc)", Arg.Any<PageLocatorOptions?>()).Returns(modal);
        h.Browser.Navigate(Arg.Any<string>()).Returns(Retry.Error);
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Retry.Error));
        h.QueueBuild(); await h.Timer.Execute(new(1));
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
        await h.Browser.Received(1).Navigate("https://game.invalid/dorf1.php");
    }

    [Fact]
    public async Task PersistentStateFailurePausesSafelyInsteadOfLeavingOnlineWithADeadLoop()
    {
        using var h = new TaskExecutionHarness();
        var broken = Substitute.For<IDbContextFactory<AppDbContext>>();
        broken.CreateDbContext().Returns(_ => throw new IOException("task-state unavailable"));
        var build = h.QueueBuild();
        await h.NewTimer(h.Tasks, new TaskFailureController(broken)).Execute(new(1));
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
        h.Tasks.IsExecuting(new(1)).ShouldBeFalse();
        await h.Build.DidNotReceive().HandleAsync(build, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedPersistenceAfterAnUncertainActionPausesAndRetainsItsInMemoryHold()
    {
        using var h = new TaskExecutionHarness();
        var cannotSave = false;
        var flaky = Substitute.For<IDbContextFactory<AppDbContext>>();
        flaky.CreateDbContext().Returns(_ => cannotSave ? throw new IOException("state unavailable after action") : h.Factory.CreateDbContext());
        var failures = new TaskFailureController(flaky);
        var timer = h.NewTimer(h.Tasks, failures);
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { cannotSave = true; return ValueTask.FromResult(Result.Fail(new UncertainTaskOutcome("ambiguous action"))); });
        var build = h.QueueBuild(); await timer.Execute(new(1));
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
        build.IsQuarantined.ShouldBeTrue();
        cannotSave = false;
        h.Tasks.SetStatus(new(1), StatusEnums.Online);
        h.Tasks.AddOrUpdate(new UpgradeBuildingTask.Task(new(1), new(1)));
        await timer.Execute(new(1));
        await h.Build.Received(1).HandleAsync(build, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ExistingLowerConstructionIsNotEvidenceForAHigherRequestedLevel()
    {
        using var h = new TaskExecutionHarness();
        using var db = h.Factory.CreateDbContext();
        var building = new Building { VillageId = 1, Location = 1, Type = BuildingEnums.Woodcutter, Level = 5, IsUnderConstruction = true };
        var queue = new QueueBuilding { VillageId = 1, Location = 1, Type = BuildingEnums.Woodcutter, Level = 6 };
        db.Buildings.Add(building); db.QueueBuildings.Add(queue); db.SaveChanges();
        var plan = new MainCore.Models.NormalBuildPlan { Location = 1, Type = BuildingEnums.Woodcutter, Level = 7 };
        UpgradeBuildingTask.HasConstructionEvidence(db, new(1), plan).ShouldBeFalse();
        queue.Level = 7; db.SaveChanges();
        UpgradeBuildingTask.HasConstructionEvidence(db, new(1), plan).ShouldBeTrue();
        db.QueueBuildings.Remove(queue); building.Level = 7; db.SaveChanges();
        UpgradeBuildingTask.HasConstructionEvidence(db, new(1), plan).ShouldBeTrue();
        UpgradeBuildingTask.HasConstructionEvidence(db, new(2), plan).ShouldBeFalse();
    }

    [Fact]
    public async Task DisabledNpcNeverReachesItsHandlerOrNavigatesAndCanBeRequeuedSafely()
    {
        using var h = new TaskExecutionHarness();
        for (var i = 0; i < 2; i++)
        {
            h.Tasks.Add(new NpcTask.Task(new(1), new(1)));
            await h.Timer.Execute(new(1));
        }
        await h.Npc.DidNotReceive().HandleAsync(Arg.Any<NpcTask.Task>(), Arg.Any<CancellationToken>());
        await h.Browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
        await h.Browser.DidNotReceive().Navigate(Arg.Any<string>());
        h.Tasks.GetTaskList(new(1)).ShouldBeEmpty();
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Online);
    }
}
