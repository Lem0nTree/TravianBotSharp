using MainCore.Infrasturecture.Extensions;

namespace MainCore.Services.BuildingAutomation;

[RegisterSingleton<AutoBuildQueuePriority>]
public sealed class AutoBuildQueuePriority(IDbContextFactory<AppDbContext> factory, TaskManager tasks, TaskFailureController failures)
{
    private readonly object _gate = new();
    private readonly Dictionary<(int Account, int Village), (string Reason, DateTimeOffset At)> _last = [];

    // Runs before account/village browser behaviors: existing jobs own the builder.
    public bool TryHandle(AutoBuildTask.Task task, ILogger logger)
    {
        using var db = factory.CreateDbContext();
        if (!db.BooleanByName(task.VillageId, VillageSettingEnums.AutoBuildEnable)) return true;
        var pending = db.Jobs.Count(x => x.VillageId == task.VillageId.Value);
        if (pending == 0) return false;
        var dispatcher = EnsureDispatcher(task.AccountId, task.VillageId);
        task.ExecuteAt = DateTime.Now.AddMinutes(1);
        Log(task, logger, dispatcher.IsQuarantined ? "normal-queue-held-inspection-required" : dispatcher.ExecuteAt > DateTime.Now ? "normal-queue-waiting" : "normal-queue-priority", pending, dispatcher.ExecuteAt);
        return true;
    }

    public UpgradeBuildingTask.Task EnsureDispatcher(AccountId account, VillageId village)
    {
        var dispatcher = tasks.EnsureTask(new UpgradeBuildingTask.Task(account, village));
        failures.Apply(account, [dispatcher]);
        return dispatcher;
    }

    public void Log(AutoBuildTask.Task task, ILogger logger, string reason, int pending = 0, DateTime? next = null)
    {
        var key = (task.AccountId.Value, task.VillageId.Value);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_last.TryGetValue(key, out var old) && old.Reason == reason && now - old.At < TimeSpan.FromMinutes(5)) return;
            _last[key] = (reason, now);
        }
        logger.Information("Auto build village={VillageId} reason={Reason} pendingJobs={PendingJobs} dispatcherAt={DispatcherAt}; existing normal jobs have priority", task.VillageId.Value, reason, pending, next);
    }
}
