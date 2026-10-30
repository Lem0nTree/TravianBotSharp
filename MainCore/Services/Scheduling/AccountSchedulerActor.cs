using MainCore.Tasks.Base;

namespace MainCore.Services.Scheduling
{
    internal sealed class AccountSchedulerActor(AccountId accountId, RxQueue rxQueue)
    {
        private readonly TaskQueue _queue = new();
        private readonly object _gate = new();

        public TaskQueue GetQueue() => _queue;

        public List<BaseTask> GetTaskList() { lock (_gate) return [.. _queue.Tasks]; }

        public CancellationTokenSource? GetCancellationTokenSource() => _queue.CancellationTokenSource;

        public bool IsExecuting() => _queue.IsExecuting;

        public StatusEnums GetStatus() => _queue.Status;

        public void Add<T>(T task, bool first) where T : BaseTask
        {
            lock (_gate) AddTask(task, first);
        }

        public T Ensure<T>(T task) where T : BaseTask
        {
            lock (_gate)
            {
                var existing = _queue.Tasks.OfType<T>().FirstOrDefault(x => x.Key == task.Key);
                if (existing is not null) return existing;
                AddTask(task, first: false);
                return task;
            }
        }

        public void AddOrUpdate<T>(T task, bool first) where T : BaseTask
        {
            lock (_gate)
            {
            var oldTask = _queue.Tasks
                .OfType<T>()
                .FirstOrDefault(x => x.Key == task.Key);

            if (oldTask is null)
            {
                AddTask(task, first);
                return;
            }

            if (first)
            {
                var firstTask = _queue.Tasks.OrderBy(x => x.ExecuteAt).FirstOrDefault();
                if (firstTask is not null)
                {
                    oldTask.ExecuteAt = firstTask.ExecuteAt.AddHours(-1);
                }
                else
                {
                    oldTask.ExecuteAt = task.ExecuteAt;
                }
            }
            else
            {
                oldTask.ExecuteAt = task.ExecuteAt;
            }
            }
        }

        public void Remove(BaseTask task)
        {
            lock (_gate) _queue.Tasks.Remove(task);
        }

        public void Remove<T>() where T : AccountTask
        {
            lock (_gate)
            {
            var task = _queue.Tasks.OfType<T>().FirstOrDefault(x => x.AccountId == accountId);
            if (task is null) return;

            _queue.Tasks.Remove(task);
            }
        }

        public void Remove<T>(VillageId villageId) where T : VillageTask
        {
            lock (_gate)
            {
            var task = _queue.Tasks.OfType<T>().FirstOrDefault(x => x.AccountId == accountId && x.VillageId == villageId);
            if (task is null) return;

            _queue.Tasks.Remove(task);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
            if (_queue.Tasks.Count == 0)
            {
                return;
            }

            _queue.Tasks.Clear();
            }
            rxQueue.Enqueue(new TasksModified(accountId));
        }

        public void SetStatus(StatusEnums status)
        {
            _queue.Status = status;
            rxQueue.Enqueue(new StatusModified(accountId, status));
        }

        private void AddTask(BaseTask task, bool first)
        {
            if (first)
            {
                var firstTask = _queue.Tasks.OrderBy(x => x.ExecuteAt).FirstOrDefault();
                if (firstTask is not null)
                {
                    task.ExecuteAt = firstTask.ExecuteAt.AddHours(-1);
                }
            }

            _queue.Tasks.Add(task);
            if (task is VillageTask villageTask)
            {
                rxQueue.Enqueue(new VillageTaskAdded(villageTask));
            }
        }
    }
}
