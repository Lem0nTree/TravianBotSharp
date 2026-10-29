using FluentResults;
using MainCore;
using MainCore.Entities;
using MainCore.Errors;
using MainCore.Infrasturecture.Persistence;
using MainCore.Services;
using MainCore.Tasks;
using MainCore.Tasks.Base;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MainCore.Test;

public class StartupSchedulingTests
{
    [Fact]
    public void QueueSnapshotRemainsEnumerableWhenInitializationAddsRemovesAndClearsTasks()
    {
        using var h = new TaskExecutionHarness();
        var first = h.QueueBuild(1); var second = h.QueueBuild(2);
        var snapshot = h.Tasks.GetTaskList(new(1));
        using var iterator = snapshot.GetEnumerator();
        iterator.MoveNext().ShouldBeTrue();
        h.Tasks.Add(new TrainTroopTask.Task(new(1), new(1)));
        h.Tasks.Remove(new(1), second);
        Should.NotThrow(() => iterator.MoveNext().ShouldBeTrue());
        iterator.Current.ShouldBeSameAs(second);
        h.Tasks.Clear(new(1));
        Should.NotThrow(() => iterator.MoveNext().ShouldBeFalse());
        snapshot.Count.ShouldBe(2);
        h.Tasks.GetTaskList(new(1)).ShouldBeEmpty();
        snapshot[0].ShouldBeSameAs(first); // Shallow snapshot retains actual task state.
        first.ExecuteAt = DateTime.Now.AddHours(1);
        snapshot[0].ExecuteAt.ShouldBe(first.ExecuteAt);
    }

    [Fact]
    public async Task InitializationEnqueueDuringStateEnumerationDoesNotPauseAccount()
    {
        using var h = new TaskExecutionHarness();
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Retry.Error));
        h.QueueBuild(1);
        var build = h.QueueBuild(2);
        using (var db = h.Factory.CreateDbContext())
        {
            db.TaskFailureStates.Add(new TaskFailureState { AccountId = 1, VillageId = 1, TaskType = typeof(UpgradeBuildingTask.Task).FullName!, Attempts = 1, NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"), Summary = "pending retry", Episode = "old-episode" });
            db.SaveChanges();
        }
        // Reproduce the pre-fix contract (a raw mutable list) through the real
        // state application code, without modifying production files to run it.
        var unsafeList = h.Tasks.GetTaskList(new(1));
        var unsafePolicy = new TaskFailureController(h.Factory, () =>
        {
            unsafeList.Add(new TrainTroopTask.Task(new(1), new(2)));
            return DateTimeOffset.UtcNow;
        });
        Should.Throw<InvalidOperationException>(() => unsafePolicy.Apply(new(1), unsafeList)).Message.ShouldContain("Collection was modified");
        var queued = false;
        var failures = new TaskFailureController(h.Factory, () =>
        {
            // This callback runs INSIDE Apply's foreach when it evaluates the first
            // persisted hold. Mutating the old live List here deterministically
            // invalidates its enumerator before the second task can be visited.
            if (!queued)
            {
                queued = true;
                h.Tasks.Add(new TrainTroopTask.Task(new(1), new(2)) { ExecuteAt = DateTime.Now.AddMinutes(-10) });
            }
            return DateTimeOffset.UtcNow;
        });
        var timer = h.NewTimer(h.Tasks, failures);
        await timer.Execute(new(1));
        h.Tasks.GetStatus(new(1)).ShouldBe(MainCore.Enums.StatusEnums.Online);
        await h.Build.Received(1).HandleAsync(build, Arg.Any<CancellationToken>());
        await h.Train.DidNotReceive().HandleAsync(Arg.Any<TrainTroopTask.Task>(), Arg.Any<CancellationToken>());
        await timer.Execute(new(1));
        await h.Train.Received(1).HandleAsync(Arg.Any<TrainTroopTask.Task>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LegacyFileDatabaseUsesProductionPoolingAndAddsStateWithoutEnsureCreatedOrDroppingHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbs-legacy-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = AppMixins.DatabaseConnectionString(directory);
        try
        {
            using (var legacy = new SqliteConnection(connectionString))
            {
                legacy.Open();
                using var create = legacy.CreateCommand();
                create.CommandText = "CREATE TABLE Jobs (Id INTEGER PRIMARY KEY, Content TEXT NOT NULL); INSERT INTO Jobs VALUES (1, 'preserve-existing-job');";
                create.ExecuteNonQuery();
            }
            var services = new ServiceCollection();
            services.AddPooledDbContextFactory<AppDbContext>(o => o.UseSqlite(connectionString, sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
            using (var provider = services.BuildServiceProvider())
            {
                var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                var controller = new TaskFailureController(factory);
                var task = new UpgradeBuildingTask.Task(new(1), new(1));
                controller.Apply(new(1), [task]);
                task.IsQuarantined.ShouldBeFalse();
                controller.Fail(new(1), task, uncertain: true);
                new TaskFailureController(factory).Apply(new(1), [new UpgradeBuildingTask.Task(new(1), new(1))]);
                using var db = factory.CreateDbContext();
                db.TaskFailureStates.Single().Quarantined.ShouldBeTrue();
                using var check = db.Database.GetDbConnection().CreateCommand();
                db.Database.OpenConnection();
                check.CommandText = "SELECT Content FROM Jobs WHERE Id=1";
                check.ExecuteScalar().ShouldBe("preserve-existing-job");
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }
}
