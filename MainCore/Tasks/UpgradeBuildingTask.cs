using MainCore.Commands.Features.UpgradeBuilding;
using MainCore.Commands.Update;
using MainCore.Tasks.Base;
using MainCore.Services.Evasion;

namespace MainCore.Tasks
{
    [Handler]
    public sealed partial class UpgradeBuildingTask(
        ILogger logger,
        IChromeBrowser browser,
        GetBuildPlanCommand.Handler getBuildPlanCommand,
        ToBuildPageCommand.Handler toBuildPageCommand,
        HandleResourceCommand.Handler handleResourceCommand,
        HandleUpgradeCommand.Handler handleUpgradeCommand,
        UpdateBuildingCommand.Handler updateBuildingCommand,
        TaskManager taskManager,
        IDbContextFactory<AppDbContext> contextFactory)
    {
        public sealed class Task(AccountId accountId, VillageId villageId) : VillageTask(accountId, villageId)
        {
            protected override string TaskName => "Upgrade building";
        }

        private async ValueTask<Result> HandleAsync(Task task, CancellationToken cancellationToken)
        {
            Result result;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested) return Cancel.Error;

                var (_, isFailed, plan, errors) = await getBuildPlanCommand.HandleAsync(new(task.AccountId, task.VillageId), cancellationToken);
                if (isFailed)
                {
                    var nextExecuteErrors = errors.OfType<NextExecuteError>().OrderBy(x => x.NextExecute).ToList();
                    if (nextExecuteErrors.Count > 0)
                    {
                        task.ExecuteAt = nextExecuteErrors.Min(x => x.NextExecute);
                    }

                    return Skip.Error.WithErrors(errors);
                }

                logger.Information("Build {Type} to level {Level} at location {Location}", plan.Type, plan.Level, plan.Location);

                result = await toBuildPageCommand.HandleAsync(new(task.VillageId, plan), cancellationToken);
                if (result.IsFailed) return result;

                result = await handleResourceCommand.HandleAsync(new(task.AccountId, task.VillageId, plan), cancellationToken);
                if (result.IsFailed)
                {
                    if (result.HasError<LackOfFreeCrop>())
                    {
                        if (ShouldYield(task)) { task.ExecuteAt = DateTime.Now; return Result.Ok(); }
                        continue;
                    }

                    if (result.HasError<StorageLimit>())
                    {
                        return Stop.Error.WithErrors(result.Errors);
                    }
                    if (result.HasError<MissingResource>())
                    {
                        var time = await UpgradeParser.GetTimeWhenEnoughResource(browser.CurrentPage, plan.Type);
                        task.ExecuteAt = DateTime.Now.Add(time);
                        logger.Information("Build stage=production_wait village={VillageId} slot={Slot} building={Building} nextAttempt={NextAttempt}; village or hero stock is insufficient", task.VillageId.Value, plan.Location, plan.Type, task.ExecuteAt);
                        return Skip.Error.WithErrors(result.Errors);
                    }

                    return result;
                }

                logger.Information("Build stage=upgrade_attempt village={VillageId} slot={Slot} building={Building} targetLevel={Level}", task.VillageId.Value, plan.Location, plan.Type, plan.Level);
                result = await handleUpgradeCommand.HandleAsync(new(task.VillageId, plan), cancellationToken);
                if (result.IsFailed) return result;

                try
                {
                    result = await updateBuildingCommand.HandleAsync(new(task.VillageId), cancellationToken);
                    if (result.IsFailed || !ConstructionObserved(task.VillageId, plan))
                        return Result.Fail(new UncertainTaskOutcome("Construction was clicked, but the target building/queue change was not observed. Inspect the game before retrying."));
                }
                catch (Exception ex)
                {
                    logger.Warning("Construction stage=observation_uncertain village={VillageId} slot={Slot} errorType={ErrorType}", task.VillageId.Value, plan.Location, ex.GetType().Name);
                    return Result.Fail(new UncertainTaskOutcome("Construction was clicked, but its result could not be read. Inspect the game before retrying."));
                }
                task.MadeProgress = true;
                logger.Information("Construction observed for {Type} at location {Location} toward level {Level}.", plan.Type, plan.Location, plan.Level);
                if (ShouldYield(task)) { task.ExecuteAt = DateTime.Now; return Result.Ok(); }
            }
        }

        private bool ConstructionObserved(VillageId villageId, NormalBuildPlan plan)
        {
            using var db = contextFactory.CreateDbContext();
            return HasConstructionEvidence(db, villageId, plan);
        }

        public static bool HasConstructionEvidence(AppDbContext db, VillageId villageId, NormalBuildPlan plan) =>
            db.Buildings.Any(x => x.VillageId == villageId.Value && x.Location == plan.Location && x.Type == plan.Type && x.Level >= plan.Level)
                || db.QueueBuildings.Any(x => x.VillageId == villageId.Value && x.Location == plan.Location && x.Type == plan.Type && x.Level >= plan.Level);

        private bool ShouldYield(Task task) => taskManager.GetTaskList(task.AccountId)
            .Any(x => x is IEvasionTask && x.ExecuteAt <= DateTime.Now.AddSeconds(25));
    }
}
