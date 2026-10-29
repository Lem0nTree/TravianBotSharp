using MainCore.Tasks.Base;

namespace MainCore.Services;

public sealed record TaskFailureDecision(bool Quarantined, int Attempts, DateTimeOffset NextAttempt, bool Notify, string Episode, string Summary);

[RegisterSingleton<TaskFailureController>]
public sealed class TaskFailureController(IDbContextFactory<AppDbContext> factory, Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly HashSet<int> _loaded = [];
    private readonly Dictionary<(int Account, int Village, string Type), TaskFailureState> _states = [];
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public static bool IsLocal(BaseTask task) => task is UpgradeBuildingTask.Task or AutoBuildTask.Task or NpcTask.Task;
    private static (int Account, int Village, string Type) Key(AccountId account, BaseTask task) =>
        (account.Value, ((VillageTask)task).VillageId.Value, task.GetType().FullName!);

    public void Apply(AccountId account, IEnumerable<BaseTask> tasks)
    {
        lock (_gate)
        {
            Load(account);
            foreach (var task in tasks.Where(IsLocal)) Apply(task, _states.GetValueOrDefault(Key(account, task)));
        }
    }

    public TaskFailureDecision Fail(AccountId account, BaseTask task, bool uncertain)
    {
        lock (_gate)
        {
            Load(account);
            var key = Key(account, task);
            using var db = factory.CreateDbContext();
            var state = db.TaskFailureStates.SingleOrDefault(x => x.AccountId == key.Account && x.VillageId == key.Village && x.TaskType == key.Type);
            if (state is null)
            {
                state = new() { AccountId = key.Account, VillageId = key.Village, TaskType = key.Type, Episode = Guid.NewGuid().ToString("N") };
                db.TaskFailureStates.Add(state);
            }
            state.Attempts++;
            state.Quarantined |= uncertain;
            var next = Now.AddMinutes(state.Attempts == 1 ? 2 : state.Attempts == 2 ? 5 : 15);
            state.NextAttemptUtc = next.ToString("O");
            state.Summary = state.Quarantined ? "Needs attention: action outcome uncertain; inspect the game before retrying."
                : $"Deferred after {state.Attempts} failure(s); next retry {next.LocalDateTime:HH:mm:ss}.";
            var notify = !state.Alerted && (state.Quarantined || state.Attempts >= 3);
            state.Alerted |= notify;
            // Keep an in-memory hold even when durable storage fails; the scheduler
            // then pauses the account rather than replaying an uncertain action.
            if (state.Quarantined) { _states[key] = state; Apply(task, state); }
            db.SaveChanges();
            _states[key] = state;
            Apply(task, state);
            return new(state.Quarantined, state.Attempts, next, notify, state.Episode, state.Summary);
        }
    }

    public void HoldUncertainInMemory(AccountId account, BaseTask task)
    {
        lock (_gate)
        {
            var key = Key(account, task);
            if (!_states.TryGetValue(key, out var state))
                _states[key] = state = new() { AccountId = key.Account, VillageId = key.Village, TaskType = key.Type, Episode = Guid.NewGuid().ToString("N") };
            state.Quarantined = true;
            state.Summary = "Needs attention: action outcome uncertain; inspect the game before retrying.";
            Apply(task, state);
        }
    }

    // A successful execution is progress; Skip/no-op and failed cleanup do not reset a failure episode.
    public void Succeeded(AccountId account, BaseTask task)
    {
        lock (_gate)
        {
            Load(account);
            var key = Key(account, task);
            if (!_states.ContainsKey(key)) return;
            using var db = factory.CreateDbContext();
            db.TaskFailureStates.Where(x => x.AccountId == key.Account && x.VillageId == key.Village && x.TaskType == key.Type).ExecuteDelete();
            _states.Remove(key);
            Apply(task, null);
        }
    }

    public void ResetAfterInspection(AccountId account, IEnumerable<BaseTask> tasks)
    {
        lock (_gate)
        {
            Load(account);
            using var db = factory.CreateDbContext();
            db.TaskFailureStates.Where(x => x.AccountId == account.Value).ExecuteDelete();
            foreach (var key in _states.Keys.Where(x => x.Account == account.Value).ToArray()) _states.Remove(key);
            foreach (var task in tasks.Where(IsLocal))
            {
                if (!string.IsNullOrEmpty(task.FailureStatus)) task.ExecuteAt = DateTime.Now;
                Apply(task, null);
            }
        }
    }

    public int Count(AccountId account)
    {
        lock (_gate) { Load(account); return _states.Keys.Count(x => x.Account == account.Value); }
    }

    private void Apply(BaseTask task, TaskFailureState? state)
    {
        task.IsQuarantined = state?.Quarantined ?? false;
        task.FailureStatus = state?.Summary ?? "";
        if (state is not null && DateTimeOffset.TryParse(state.NextAttemptUtc, out var next) && next > Now && task.ExecuteAt < next.LocalDateTime)
            task.ExecuteAt = next.LocalDateTime;
    }

    private void Load(AccountId account)
    {
        if (_loaded.Contains(account.Value)) return;
        using var db = factory.CreateDbContext();
        // Additive upgrade: never recreate/drop the user's existing jobs or history.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "TaskFailureStates" (
              "AccountId" INTEGER NOT NULL, "VillageId" INTEGER NOT NULL, "TaskType" TEXT NOT NULL,
              "Attempts" INTEGER NOT NULL, "Quarantined" INTEGER NOT NULL, "NextAttemptUtc" TEXT NOT NULL,
              "Summary" TEXT NOT NULL, "Episode" TEXT NOT NULL, "Alerted" INTEGER NOT NULL,
              PRIMARY KEY ("AccountId", "VillageId", "TaskType"));
            """);
        foreach (var state in db.TaskFailureStates.AsNoTracking().Where(x => x.AccountId == account.Value))
            _states[(state.AccountId, state.VillageId, state.TaskType)] = state;
        _loaded.Add(account.Value);
    }
}
