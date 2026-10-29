using System.Text.Json;
using MainCore.Commands.Navigate;
using MainCore.Commands.Update;
using MainCore.Infrasturecture.Extensions;
using MainCore.Services.BuildingAutomation;
using MainCore.Tasks.Base;

namespace MainCore.Tasks;

[Handler]
public sealed partial class AutoBuildTask(
    IChromeBrowser browser, ToDorfCommand.Handler navigate, UpdateBuildingCommand.Handler update,
    IDbContextFactory<AppDbContext> factory, TaskManager tasks, ILogger logger)
{
    public sealed class Task(AccountId accountId, VillageId villageId) : VillageTask(accountId, villageId)
    {
        protected override string TaskName => "Auto build";
        public override bool CanStart(AppDbContext context) => context.BooleanByName(VillageId, VillageSettingEnums.AutoBuildEnable);
    }

    private async ValueTask<Result> HandleAsync(Task task, CancellationToken token)
    {
        task.ExecuteAt = DateTime.Now.AddMinutes(1);
        using (var initial = factory.CreateDbContext())
            if (!task.CanStart(initial)) return Result.Ok();
        // A complete, freshly read layout is required; parsers returning empty/incomplete data fail closed.
        foreach (var dorf in new[] { 1, 2 })
        {
            var result = await navigate.HandleAsync(new(dorf), token);
            if (result.IsFailed) return result;
            result = await browser.Refresh();
            if (result.IsFailed) return result;
            if (await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId)
            {
                logger.Warning("Auto build skipped: the selected village changed during refresh.");
                return Result.Ok();
            }
            var layout = dorf == 1 ? await BuildingLayoutParser.GetFields(browser.CurrentPage) : await BuildingLayoutParser.GetInfrastructures(browser.CurrentPage);
            if (dorf == 1 && (layout.Count != 18 || layout.Any(x => x.Location is < 1 or > 18 || !x.Type.IsResourceField() || x.Level < 0)) ||
                dorf == 2 && (layout.Count < 20 || layout.Any(x => x.Location is < 19 or > 40 || x.Type == BuildingEnums.Unknown)))
            {
                logger.Warning("Auto build skipped: village layout could not be read completely.");
                return Result.Ok();
            }
            result = await update.HandleAsync(new(task.VillageId), token);
            if (result.IsFailed) return result;
        }
        token.ThrowIfCancellationRequested();
        using var db = factory.CreateDbContext();
        NormalBuildPlan? plan;
        try { plan = AutoBuildQueue.TryQueue(db, task.AccountId, task.VillageId); }
        catch (InvalidOperationException ex) { logger.Warning("Auto build skipped: {Reason}", ex.Message); return Result.Ok(); }
        if (plan is null) return Result.Ok();
        tasks.AddOrUpdate(new UpgradeBuildingTask.Task(task.AccountId, task.VillageId));
        task.MadeProgress = true;
        logger.Information("Auto build queued {Building} level {Level} at slot {Slot}", plan.Type, plan.Level, plan.Location);
        return Result.Ok();
    }
}

public static class AutoBuildQueue
{
    public static NormalBuildPlan? TryQueue(AppDbContext db, AccountId accountId, VillageId villageId)
    {
        using var transaction = db.Database.BeginTransaction();
        if (!db.BooleanByName(villageId, VillageSettingEnums.AutoBuildEnable) || db.Jobs.Any(x => x.VillageId == villageId.Value) ||
            db.QueueBuildings.Any(x => x.VillageId == villageId.Value) ||
            db.Buildings.Any(x => x.VillageId == villageId.Value && x.IsUnderConstruction))
            return null;
        var capital = db.ByName(villageId, VillageSettingEnums.ConfirmedCapitalStatus) switch { 1 => (bool?)true, 2 => false, _ => null };
        var snapshot = BuildingSnapshotReader.Read(db, accountId, villageId);
        var plan = AutoBuildSelector.Select(snapshot, capital);
        if (plan is null) return null;
        db.Jobs.Add(new Job { VillageId = villageId.Value, Position = 0, Type = JobTypeEnums.NormalBuild, Content = JsonSerializer.Serialize(plan) });
        db.SaveChanges();
        transaction.Commit();
        return plan;
    }
}

public static class AutoBuildSelector
{
    public static NormalBuildPlan? Select(BuildingSnapshot snapshot, bool? isCapital)
    {
        if (snapshot.PendingCount != 0 || snapshot.ConstructionCount != 0 || snapshot.Buildings.Any(x => x.UnderConstruction)) return null;
        foreach (var b in snapshot.Buildings.Where(x => x.Type != BuildingEnums.Site && (x.Level > 0 || x.Type.IsResourceField() && x.Level == 0))
                     .OrderBy(x => x.Level).ThenBy(x => x.Slot))
        {
            if (BuildingRules.CanUpgrade(snapshot, b, isCapital))
                return new NormalBuildPlan { Location = b.Slot, Type = b.Type, Level = b.Level + 1 };
        }
        return null;
    }
}
