using System.Collections.Concurrent;

using MainCore.Services.Scheduling;
using MainCore.Tasks.Base;

namespace MainCore.Services
{
    [RegisterSingleton<TaskManager>]
    public sealed class TaskManager(
        RxQueue rxQueue,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger logger)
    {
        private readonly ConcurrentDictionary<AccountId, AccountSchedulerActor> _actors = [];

        public BaseTask? GetCurrentTask(AccountId accountId)
        {
            var tasks = GetTaskList(accountId);
            return tasks.Find(x => x.Stage == StageEnums.Executing);
        }

        // Non-reentrant: hold across control validation and persistence. Gameplay
        // only holds this while selecting/starting; completion never acquires it.
        private readonly ConcurrentDictionary<AccountId, SemaphoreSlim> _controlGates = [];

        public async Task<IDisposable> AcquireControlAsync(AccountId accountId, CancellationToken cancellationToken = default)
        {
            var gate = _controlGates.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new ControlLease(gate);
        }

        private sealed class ControlLease(SemaphoreSlim gate) : IDisposable
        {
            private SemaphoreSlim? _gate = gate;
            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }

        // Caller holds its control lease. Wait for recovery/persistence, too.
        public async Task StopCurrentTask(AccountId accountId, CancellationToken cancellationToken = default)
        {
            var stopping = GetStatus(accountId) == StatusEnums.Stopping;
            if (!stopping) SetStatus(accountId, StatusEnums.Pausing);
            var cts = GetCancellationTokenSource(accountId);
            try { if (cts is not null) await cts.CancelAsync().WaitAsync(cancellationToken); }
            catch (ObjectDisposedException) { /* Execution already finished. */ }
            while (IsExecuting(accountId)) await Task.Delay(50, cancellationToken);
            SetStatus(accountId, stopping ? StatusEnums.Stopping : StatusEnums.Paused);
        }

        public void AddOrUpdate<T>(T task, bool first = false) where T : AccountTask
        {
            if (!ValidateTaskOwnership(task))
            {
                return;
            }

            var actor = GetActor(task.AccountId);
            actor.AddOrUpdate(task, first);
        }

        public void Add<T>(T task, bool first = false) where T : AccountTask
        {
            if (!ValidateTaskOwnership(task))
            {
                return;
            }

            var actor = GetActor(task.AccountId);
            actor.Add(task, first);
        }

        public T EnsureTask<T>(T task) where T : AccountTask
        {
            if (!ValidateTaskOwnership(task)) throw new InvalidOperationException("The task does not belong to this account.");
            return GetActor(task.AccountId).Ensure(task);
        }

        public bool IsExist<T>(AccountId accountId) where T : BaseTask
        {
            var tasks = GetTaskList(accountId)
                .OfType<T>();
            return tasks.Any(x => x.Key == $"{accountId}");
        }

        public bool IsExist<T>(AccountId accountId, VillageId villageId) where T : BaseTask
        {
            var tasks = GetTaskList(accountId)
                .OfType<T>();
            return tasks.Any(x => x.Key == $"{accountId}-{villageId}");
        }

        public void Remove(AccountId accountId, BaseTask task)
        {
            var actor = GetActor(accountId);
            actor.Remove(task);
        }

        public void Remove<T>(AccountId accountId) where T : AccountTask
        {
            var actor = GetActor(accountId);
            actor.Remove<T>();
        }

        public void Remove<T>(AccountId accountId, VillageId villageId) where T : VillageTask
        {
            var actor = GetActor(accountId);
            actor.Remove<T>(villageId);
        }

        public void Clear(AccountId accountId)
        {
            var actor = GetActor(accountId);
            actor.Clear();
        }

        public List<BaseTask> GetTaskList(AccountId accountId)
        {
            var actor = GetActor(accountId);
            return actor.GetTaskList();
        }

        public StatusEnums GetStatus(AccountId accountId)
        {
            var actor = GetActor(accountId);
            return actor.GetStatus();
        }

        public void SetStatus(AccountId accountId, StatusEnums status)
        {
            var actor = GetActor(accountId);
            actor.SetStatus(status);
        }

        private CancellationTokenSource? GetCancellationTokenSource(AccountId accountId)
        {
            var actor = GetActor(accountId);
            return actor.GetCancellationTokenSource();
        }

        public bool IsExecuting(AccountId accountId)
        {
            var actor = GetActor(accountId);
            return actor.IsExecuting();
        }

        public TaskQueue GetTaskQueue(AccountId accountId)
        {
            var actor = GetActor(accountId);
            return actor.GetQueue();
        }

        private AccountSchedulerActor GetActor(AccountId accountId)
        {
            return _actors.GetOrAdd(accountId, static (id, state) => new AccountSchedulerActor(id, state), rxQueue);
        }

        private bool ValidateTaskOwnership(AccountTask task)
        {
            if (task.AccountId == AccountId.Empty)
            {
                var reason = $"Rejected {task.GetType().Name}: AccountId is empty";
                logger.Warning("{Reason}", reason);
                return false;
            }

            if (task is VillageTask villageTask)
            {
                using var context = contextFactory.CreateDbContext();
                var villageBelongToAccount = context.Villages
                    .Any(x => x.Id == villageTask.VillageId.Value && x.AccountId == task.AccountId.Value);

                if (!villageBelongToAccount)
                {
                    var reason = $"Rejected {task.GetType().Name}: village {villageTask.VillageId.Value} does not belong to account {task.AccountId.Value}";
                    logger.Warning("{Reason}", reason);
                    return false;
                }
            }
            return true;
        }
    }

    public class TaskQueue
    {
        private volatile bool _isExecuting;
        public bool IsExecuting { get => _isExecuting; set => _isExecuting = value; }
        public StatusEnums Status { get; set; } = StatusEnums.Offline;
        public CancellationTokenSource? CancellationTokenSource { get; set; }
        internal List<BaseTask> Tasks { get; } = [];
    }
}
