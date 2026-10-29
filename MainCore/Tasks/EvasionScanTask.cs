using MainCore.Commands.Navigate;
using System.Text.Json;
using MainCore.Infrasturecture.Extensions;
using MainCore.Services.Evasion;
using MainCore.Tasks.Base;

namespace MainCore.Tasks;

// Read-only attack discovery. Scheduled independently of Auto Refresh.
[Handler]
public sealed partial class EvasionScanTask(IChromeBrowser browser, SwitchVillageCommand.Handler switchVillage,
    IDbContextFactory<AppDbContext> factory, TaskManager taskManager, AttackAlertWorker attackAlerts, ILogger logger)
{
    public sealed class Task(AccountId accountId, VillageId villageId) : VillageTask(accountId, villageId), IEvasionTask
    {
        protected override string TaskName => "Scan incoming attacks";
        public override bool CanStart(AppDbContext context)
        {
            if (context.BooleanByName(VillageId, VillageSettingEnums.ResourceEvasionEnable) || context.BooleanByName(VillageId, VillageSettingEnums.TroopEvasionEnable)) return true;
            EvasionStore.EnsureSchema(context);
            return context.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == AccountId.Value && x.VillageId == VillageId.Value && x.Kind == "troop" && x.State == "Submitting")
                .Select(x => x.SentUtc).AsEnumerable().Any(x => DateTimeOffset.TryParse(x, out var sent) && DateTimeOffset.UtcNow < sent.AddSeconds(87));
        }
    }

    private async ValueTask<Result> HandleAsync(Task task, CancellationToken token)
    {
        using var db = factory.CreateDbContext();
        if (!task.CanStart(db)) return Result.Ok();
        task.ExecuteAt = DateTime.Now.AddSeconds(10);
        var destination = db.ByName(task.VillageId, VillageSettingEnums.EvasionDestinationId);
        var owned = db.Villages.Where(x => x.AccountId == task.AccountId.Value).Select(x => x.Id).ToArray();
        var destinationValid = EvasionRules.IsOwnedDestination(task.VillageId.Value, destination, owned);
        if (!browser.IsInitialized) return Result.Ok();
        if (!await LoginParser.IsIngamePage(browser.CurrentPage))
        {
            if (await LoginParser.IsLoginPage(browser.CurrentPage))
                taskManager.AddOrUpdate(new LoginTask.Task(task.AccountId), first: true);
            else logger.Warning("Attack scan skipped because the browser is not on a recognized game or login page.");
            return Result.Ok();
        }
        var switched = await switchVillage.HandleAsync(new(task.VillageId), token);
        if (switched.IsFailed) return switched;
        var refreshed = await browser.Refresh();
        if (refreshed.IsFailed) return refreshed;
        await AttackAlertObservation.CaptureAsync(browser.CurrentPage, task.AccountId, attackAlerts);
        if (await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) return Result.Ok();
        var node = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await node.CountAsync() != 1) return Result.Ok();
        var underAttack = await node.EvaluateAsync<bool>("node => node.classList.contains('attack')");
        EvasionStore.EnsureSchema(db);
        var unresolved = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == task.AccountId.Value && x.VillageId == task.VillageId.Value && x.Kind == "troop" && x.State == "Submitting").ToArray();
        if (!underAttack && unresolved.Length == 0) return Result.Ok();
        var uri = new Uri(new Uri(browser.CurrentUrl), "/build.php?id=39&tt=1&gid=16");
        var result = await browser.Navigate(uri.ToString());
        if (result.IsFailed) return result;
        if (await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) return Result.Ok();
        await ReconcileUncertainAsync(db, task, unresolved);
        if (!underAttack) return Result.Ok();
        if (!destinationValid) { logger.Warning("Evasion scan skipped: destination must be a different owned village."); return Result.Ok(); }
        var started = DateTimeOffset.UtcNow;
        var attacks = await IncomingAttackAdapter.ReadAsync(browser.CurrentPage, started);
        if (attacks is null || attacks.Count == 0)
        {
            logger.Warning("Evasion scan could not identify timed hostile movements; no dispatch was scheduled.");
            return Result.Ok();
        }
        foreach (var attack in attacks)
        {
            if (attack.ImpactAt <= DateTimeOffset.UtcNow) continue;
            foreach (var kind in new[] { "merchant", "troop" })
            {
                var enabled = kind == "merchant" ? db.BooleanByName(task.VillageId, VillageSettingEnums.ResourceEvasionEnable)
                    : db.BooleanByName(task.VillageId, VillageSettingEnums.TroopEvasionEnable) &&
                      db.ByName(task.VillageId, VillageSettingEnums.ConfirmedCapitalStatus) == 2;
                if (!enabled) continue;
                EvasionStore.TryPlan(db, new EvasionDispatch { AccountId = task.AccountId.Value, VillageId = task.VillageId.Value,
                    DestinationId = destination, AttackId = attack.Id, Kind = kind, ImpactUtc = attack.ImpactAt.ToUniversalTime().ToString("O"), Payload = "{}" });
                if (kind == "merchant")
                {
                    var planned = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == task.AccountId.Value && x.VillageId == task.VillageId.Value && x.AttackId == attack.Id && x.Kind == kind);
                    if (planned is null || planned.State != "Planned" || planned.DestinationId != destination || !DateTimeOffset.TryParse(planned.ImpactUtc, out var impact) || DateTimeOffset.UtcNow > impact.AddSeconds(-29)) continue;
                    var operation = new EvasionMerchantTask.Task(task.AccountId, task.VillageId, attack.Id, destination, impact)
                    { ExecuteAt = impact.AddSeconds(-40).LocalDateTime };
                    if (!taskManager.GetTaskList(task.AccountId).Any(x => x is EvasionMerchantTask.Task && x.Key == operation.Key))
                        taskManager.Add(operation);
                }
                else
                {
                    var planned = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == task.AccountId.Value && x.VillageId == task.VillageId.Value && x.AttackId == attack.Id && x.Kind == kind);
                    if (planned is null || planned.State != "Planned" || planned.DestinationId != destination || !DateTimeOffset.TryParse(planned.ImpactUtc, out var impact) || DateTimeOffset.UtcNow > impact.AddSeconds(-19)) continue;
                    var operation = new EvasionTroopTask.Task(task.AccountId, task.VillageId, attack.Id, destination, impact)
                    { ExecuteAt = impact.AddSeconds(-28).LocalDateTime };
                    if (!taskManager.GetTaskList(task.AccountId).Any(x => x is EvasionTroopTask.Task && x.Key == operation.Key))
                        taskManager.Add(operation);
                }
            }
        }
        return Result.Ok();
    }

    private async System.Threading.Tasks.Task ReconcileUncertainAsync(AppDbContext db, Task task, IEnumerable<EvasionDispatch> unresolved)
    {
        foreach (var dispatch in unresolved)
        {
            if (!DateTimeOffset.TryParse(dispatch.SentUtc, out var sent) || DateTimeOffset.UtcNow >= sent.AddSeconds(87) ||
                !DateTimeOffset.TryParse(dispatch.ImpactUtc, out var impact)) continue;
            var target = db.Villages.AsNoTracking().SingleOrDefault(x => x.Id == dispatch.DestinationId && x.AccountId == dispatch.AccountId);
            if (target is null) continue;
            try
            {
                using var payload = JsonDocument.Parse(dispatch.Payload);
                var amounts = payload.RootElement.GetProperty("Amounts").Deserialize<long[]>();
                var before = payload.RootElement.GetProperty("Before").Deserialize<string[]>();
                var tile = payload.RootElement.GetProperty("DestinationTile").GetInt64();
                if (amounts is null || before is null || tile <= 0) continue;
                var movement = await RallyMovementAdapter.FindUniqueNewAsync(browser.CurrentPage, before.ToHashSet(StringComparer.Ordinal), tile, amounts);
                if (movement is null || !EvasionStore.RecordSent(db, dispatch, movement.Id, sent, DateTimeOffset.UtcNow + movement.Remaining)) continue;
                var cancel = new EvasionCancelTask.Task(task.AccountId, task.VillageId, dispatch.AttackId, movement.Id)
                { ExecuteAt = impact.AddSeconds(1).LocalDateTime };
                if (!taskManager.GetTaskList(task.AccountId).Any(x => x is EvasionCancelTask.Task && x.Key == cancel.Key)) taskManager.Add(cancel);
            }
            catch (JsonException) { logger.Warning("Uncertain troop evasion has invalid saved data and requires manual inspection."); }
            catch (KeyNotFoundException) { logger.Warning("Uncertain troop evasion has incomplete saved data and requires manual inspection."); }
            catch (InvalidOperationException) { logger.Warning("Uncertain troop evasion has malformed saved data and requires manual inspection."); }
            catch (FormatException) { logger.Warning("Uncertain troop evasion has malformed saved data and requires manual inspection."); }
        }
    }
}
