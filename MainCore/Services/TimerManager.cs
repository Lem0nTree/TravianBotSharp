using System.Collections.Concurrent;

using MainCore.Infrasturecture.Extensions;
using MainCore.Services.Evasion;

using Microsoft.Extensions.DependencyInjection;

using Polly;
using Polly.Retry;

namespace MainCore.Services
{
    [RegisterSingleton<TimerManager>]
    public sealed class TimerManager
    {
        private readonly ConcurrentDictionary<AccountId, CancellationTokenSource> _loops = [];

        private volatile bool _isShutdown = false;

        private readonly TaskManager _taskManager;
        private readonly RxQueue _rxQueue;
        private readonly CustomServiceScopeFactory _serviceScopeFactory;
        private readonly IDbContextFactory<AppDbContext> _contextFactory;
        private readonly TaskFailureController _taskFailures;

        private static readonly ResiliencePropertyKey<ContextData> contextDataKey = new(nameof(ContextData));
        private readonly ResiliencePipeline<Result> _pipeline;

        public TimerManager(TaskManager taskManager, CustomServiceScopeFactory serviceScopeFactory, RxQueue rxQueue, IDbContextFactory<AppDbContext> contextFactory, TaskFailureController taskFailures)
        {
            _taskManager = taskManager;
            _serviceScopeFactory = serviceScopeFactory;
            _rxQueue = rxQueue;
            _contextFactory = contextFactory;
            _taskFailures = taskFailures;

            static async ValueTask OnRetry(OnRetryArguments<Result> args)
            {
                if (!args.Context.Properties.TryGetValue(contextDataKey, out var contextData)) return;

                var (taskName, browser) = contextData;
                var error = args.Outcome;
                if (error.Exception is not null)
                {
                    var exception = error.Exception;
                    browser.Logger.Error(exception, "{Message}", exception.Message);
                }
                if (error.Result is not null)
                {
                    var message = string.Join(Environment.NewLine, error.Result.Reasons.Select(e => e.Message));
                    if (!string.IsNullOrEmpty(message))
                    {
                        browser.Logger.Warning("Task {TaskName} failed", taskName, message);
                        browser.Logger.Warning("{Message}", message);
                    }
                }
                await browser.Refresh();
                browser.Logger.Warning("{TaskName} will retry after {RetryDelay} (#{AttemptNumber} times)", taskName, args.RetryDelay, args.AttemptNumber + 1);
            }

            var retryOptions = new RetryStrategyOptions<Result>()
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(30),
                UseJitter = true,
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder<Result>()
                   .Handle<Exception>()
                   .HandleResult(static x => x.HasError<Retry>()),
                OnRetry = OnRetry
            };

            _pipeline = new ResiliencePipelineBuilder<Result>()
                .AddRetry(retryOptions)
                .Build();
        }

        public async Task Execute(AccountId accountId)
        {
            var phase = "startup";
            try { await ExecuteCore(accountId, value => phase = value); }
            catch (Exception ex)
            {
                // Detailed exceptions stay in local logs; remote alerts contain only
                // the generic recovery instruction below.
                Serilog.Log.Warning(ex, "Account task execution stopped safely account={AccountId} phase={Phase} errorType={ErrorType}; state may not have been saved. Inspect the game before restarting or resuming", accountId.Value, phase, ex.GetType().Name);
                _taskManager.SetStatus(accountId, StatusEnums.Paused);
                if (_taskManager.GetTaskQueue(accountId).CancellationTokenSource?.IsCancellationRequested != true)
                    NotifyAccountFailure(accountId, stateMayBeUnsaved: true);
            }
            finally
            {
                var queue = _taskManager.GetTaskQueue(accountId);
                if (queue.IsExecuting)
                {
                    foreach (var running in _taskManager.GetTaskList(accountId).Where(x => x.Stage == StageEnums.Executing)) running.Stage = StageEnums.Waiting;
                    queue.IsExecuting = false;
                    queue.CancellationTokenSource?.Dispose();
                    queue.CancellationTokenSource = null;
                    _rxQueue.Enqueue(new TasksModified(accountId));
                }
            }
        }

        private async Task ExecuteCore(AccountId accountId, Action<string> phase)
        {
            var taskQueue = _taskManager.GetTaskQueue(accountId);

            var status = taskQueue.Status;
            if (status != StatusEnums.Online) return;
            // Initialization and notifications may enqueue more tasks concurrently.
            // One locked snapshot owns this entire selection pass.
            var tasks = _taskManager.GetTaskList(accountId);
            phase("persistent-task-state");
            _taskFailures.Apply(accountId, tasks);
            phase("task-selection");
            var task = EvasionScheduling.Select(tasks.Where(x => !x.IsQuarantined), DateTime.Now, TimeSpan.FromSeconds(20));
            if (task is null) return;
            phase("account-settings");
            bool evasionEnabled;
            using (var settingsDb = _contextFactory.CreateDbContext())
                evasionEnabled = settingsDb.VillagesSetting.Any(s => s.Value == 1 &&
                    (s.Setting == VillageSettingEnums.ResourceEvasionEnable || s.Setting == VillageSettingEnums.TroopEvasionEnable) &&
                    settingsDb.Villages.Any(v => v.Id == s.VillageId && v.AccountId == accountId.Value));

            taskQueue.IsExecuting = true;
            var cts = new CancellationTokenSource();
            taskQueue.CancellationTokenSource = cts;

            task.Stage = StageEnums.Executing;
            _rxQueue.Enqueue(new TasksModified(accountId));

            var cacheExecuteTime = task.ExecuteAt;

            phase("task-scope");
            using var scope = _serviceScopeFactory.CreateScope(accountId);

            ///===========================================================///
            phase("browser-service");
            var browser = scope.ServiceProvider.GetRequiredService<IChromeBrowser>();
            var logger = browser.Logger;

            // Gate stale queued NPC work before any account/village behavior can navigate.
            if (task is NpcTask.Task npc)
            {
                using var eligibility = _contextFactory.CreateDbContext();
                if (!npc.CanStart(eligibility))
                {
                    var recovered = await TaskDialogRecovery.CloseNpcAsync(browser);
                    _taskManager.Remove(accountId, task);
                    task.Stage = StageEnums.Waiting;
                    taskQueue.IsExecuting = false;
                    var wasCancelled = cts.IsCancellationRequested;
                    cts.Dispose();
                    taskQueue.CancellationTokenSource = null;
                    logger.Information("NPC stage=queued_task_skipped village={VillageId} reason=disabled-or-ineligible; paid exchange was not attempted", npc.VillageId.Value);
                    if (!recovered)
                    {
                        _taskManager.SetStatus(accountId, StatusEnums.Paused);
                        if (!wasCancelled) NotifyAccountFailure(accountId);
                    }
                    _rxQueue.Enqueue(new TasksModified(accountId));
                    return;
                }
            }
            task.MadeProgress = false;

            var contextData = new ContextData(task.Description, browser);

            ///===========================================================///
            var resilienceContext = ResilienceContextPool.Shared.Get(cts.Token);

            resilienceContext.Properties.Set(contextDataKey, contextData);

            Outcome<Result> poliResult;
            phase("task-handler");
            if (task is IEvasionTask || evasionEnabled || TaskFailureController.IsLocal(task))
            {
                // A timed mutation can have an ambiguous outcome; never apply the
                // generic retry pipeline to it.
                try { poliResult = Outcome.FromResult(await scope.Execute(task, cts.Token)); }
                catch (Exception ex) { poliResult = Outcome.FromException<Result>(ex); }
            }
            else
            {
                poliResult = await _pipeline.ExecuteOutcomeAsync(
                    async (ctx, state) => Outcome.FromResult(await scope.Execute(state, ctx.CancellationToken)),
                    resilienceContext,
                    task);
            }

            ResilienceContextPool.Shared.Return(resilienceContext);
            ///===========================================================///

            task.Stage = StageEnums.Waiting;
            _rxQueue.Enqueue(new TasksModified(accountId));

            taskQueue.IsExecuting = false;

            var userCancelled = cts.IsCancellationRequested;
            cts.Dispose();
            taskQueue.CancellationTokenSource = null;

            var local = TaskFailureController.IsLocal(task);
            phase("task-outcome");
            var uncertain = poliResult.Result?.HasError<UncertainTaskOutcome>() == true;
            var construction = poliResult.Result?.Errors.OfType<UncertainTaskOutcome>().Select(x => x.Construction).FirstOrDefault(x => x is not null);
            var fatal = poliResult.Exception?.GetType().Name == "TargetClosedException" || poliResult.Result?.HasError<BrowserRecoveryFailed>() == true
                || !browser.IsInitialized || browser.CurrentPage.IsClosed;
            var explicitCancel = userCancelled || poliResult.Result?.HasError<Cancel>() == true;
            // Persist an uncertain action even if the user also paused or the browser closed.
            if (local && uncertain)
            {
                _taskFailures.HoldUncertainInMemory(accountId, task, construction);
                ReportLocalFailure(uncertain: true);
                if (!await TaskDialogRecovery.CloseLocalAsync(browser)) fatal = true;
                if (explicitCancel || fatal) _taskManager.SetStatus(accountId, StatusEnums.Paused);
                if (fatal && !explicitCancel) NotifyAccountFailure(accountId);
                return;
            }
            if (explicitCancel || fatal)
            {
                logger.Warning("The account is pausing because the user cancelled or the shared browser could not be recovered.");
                _taskManager.SetStatus(accountId, StatusEnums.Paused);
                if (fatal && !explicitCancel) NotifyAccountFailure(accountId);
                return;
            }
            var benignSkip = poliResult.Result is { IsFailed: true } skipped && skipped.HasError<Skip>() && !skipped.HasError<Retry>() && !skipped.HasError<Stop>();
            var failed = poliResult.Exception is not null || poliResult.Result is { IsFailed: true } && !benignSkip;
            if (local && failed && !explicitCancel && !fatal)
            {
                if (!await TaskDialogRecovery.CloseLocalAsync(browser))
                {
                    logger.Warning("Task recovery failed; the shared browser is unusable and the account is pausing.");
                    _taskManager.SetStatus(accountId, StatusEnums.Paused);
                    NotifyAccountFailure(accountId);
                    return;
                }
                ReportLocalFailure(uncertain: false);
                return;
            }
            if (local && task.MadeProgress && poliResult.Result is { } completed && (completed.IsSuccess || benignSkip))
            {
                _taskFailures.Succeeded(accountId, task);
                _rxQueue.Enqueue(new TasksModified(accountId));
            }

            void ReportLocalFailure(bool uncertain)
            {
                var decision = _taskFailures.Fail(accountId, task, uncertain, construction);
                var detail = poliResult.Exception?.Message ?? string.Join("; ", poliResult.Result?.Errors.Select(x => x.Message) ?? []);
                detail = System.Text.RegularExpressions.Regex.Replace(detail, @"https?://\S+|<[^>]+>", "<redacted>");
                detail = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (detail.Length > 500) detail = detail[..500];
                logger.Warning("Task failure detail task={TaskName} exceptionType={ErrorType} reason={Reason}", task.Description, poliResult.Exception?.GetType().Name ?? "result", detail);
                logger.Warning("Task {TaskName} failure {Attempts}: {Summary} Other account tasks continue. Details remain in the task table and account log.", task.Description, decision.Attempts, decision.Summary);
                if (decision.Notify)
                {
                    var village = task is MainCore.Tasks.Base.VillageTask v ? (VillageId?)v.VillageId : null;
                    _rxQueue.Enqueue(new MainCore.Services.Telegram.TelegramAlert(accountId, village,
                        MainCore.Services.Telegram.TelegramEvents.Errors,
                        $"{task.Description}: {decision.Summary} Other tasks continue. Inspect the game and the task table. Pause the account and use Retry failed tasks after inspection.", $"task-failure-{decision.Episode}"));
                    logger.Warning("Persistent task failure requires attention. A Telegram Errors alert is queued when that channel is configured; this local warning remains visible regardless of delivery.");
                }
                _rxQueue.Enqueue(new TasksModified(accountId));
            }

            if (poliResult.Exception is not null)
            {
                var ex = poliResult.Exception;

                if (ex is OperationCanceledException)
                {
                    logger.Information("Pause button is pressed");
                }
                else
                {
                    var filename = await browser.Screenshot();
                    logger.Information("Screenshot saved as {FileName}", filename);
                    logger.Warning("There is something wrong. Bot is pausing. Last exception is");
                    logger.Error(ex, "{Message}", ex.Message);
                }

                _taskManager.SetStatus(accountId, StatusEnums.Paused);
            }

            if (poliResult.Result is not null)
            {
                var result = poliResult.Result;
                if (result.IsFailed)
                {
                    if (result.HasError<Retry>() && evasionEnabled && task is not IEvasionTask)
                    {
                        task.ExecuteAt = DateTime.Now.AddSeconds(30);
                        logger.Warning("Task {TaskName} postponed after a retryable error so attack scans can run.", task.Description);
                    }
                    else if (result.HasError<Stop>() || result.HasError<Retry>())
                    {
                        _rxQueue.Enqueue(new MainCore.Services.Telegram.TelegramAlert(accountId, null,
                            MainCore.Services.Telegram.TelegramEvents.Errors,
                            "A task failed and the bot is pausing. Open the account's bot log for details.", "error"));
                        var filename = await browser.Screenshot();
                        logger.Information(messageTemplate: "Screenshot saved as {FileName}", filename);
                        _taskManager.SetStatus(accountId, StatusEnums.Paused);
                    }
                    else if (result.HasError<Skip>())
                    {
                        if (task.ExecuteAt == cacheExecuteTime)
                        {
                            _taskManager.Remove(accountId, task);
                        }
                        else
                        {
                            logger.Information("Schedule next run at {Time}", task.ExecuteAt.ToString("yyyy-MM-dd HH:mm:ss"));
                        }
                    }
                    else if (result.HasError<Cancel>())
                    {
                        _taskManager.SetStatus(accountId, StatusEnums.Paused);
                    }
                }
                else
                {
                    if (task.ExecuteAt == cacheExecuteTime)
                    {
                        _taskManager.Remove(accountId, task);
                    }
                    else
                    {
                        logger.Information("Schedule next run at {Time}", task.ExecuteAt.ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                }
            }

            if (task is IEvasionTask || _taskManager.GetTaskList(accountId).Any(x => x is IEvasionTask && x.ExecuteAt <= DateTime.Now.AddSeconds(20))) return;
            using var context = _contextFactory.CreateDbContext();
            var delay = context.ByName(accountId, AccountSettingEnums.TaskDelayMin, AccountSettingEnums.TaskDelayMax);
            await Task.Delay(delay);
        }

        private void NotifyAccountFailure(AccountId accountId, bool stateMayBeUnsaved = false) =>
            _rxQueue.Enqueue(new MainCore.Services.Telegram.TelegramAlert(accountId, null,
                MainCore.Services.Telegram.TelegramEvents.Errors,
                "The account paused because browser or persistent task-state recovery failed. Inspect its task table and local log."
                    + (stateMayBeUnsaved ? " Task state may not have been saved. Inspect the game before restarting or resuming." : ""), "account-recovery-fatal"));

        public void Shutdown()
        {
            _isShutdown = true;

            foreach (var accountId in _loops.Keys)
            {
                if (_loops.TryRemove(accountId, out var cancellationTokenSource))
                {
                    cancellationTokenSource.Cancel();
                    cancellationTokenSource.Dispose();
                }
            }
        }

        public void Start(AccountId accountId)
        {
            if (_isShutdown) return;

            var cancellationTokenSource = new CancellationTokenSource();
            if (!_loops.TryAdd(accountId, cancellationTokenSource))
            {
                cancellationTokenSource.Dispose();
                return;
            }

            _ = Task.Run(async () =>
            {
                while (!_isShutdown && !cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Execute(accountId);
                        await Task.Delay(100, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException) when (_isShutdown || cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (_loops.TryRemove(accountId, out var ownedCancellationTokenSource))
                {
                    ownedCancellationTokenSource.Dispose();
                }
            }, cancellationTokenSource.Token);
        }

        public record ContextData(string TaskName, IChromeBrowser Browser);
    }
}
