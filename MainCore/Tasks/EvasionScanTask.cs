using MainCore.Commands.Navigate;
using System.Text.Json;
using MainCore.Infrasturecture.Extensions;
using MainCore.Services.Evasion;
using MainCore.Tasks.Base;

namespace MainCore.Tasks;

// Read-only attack discovery. Scheduled independently of Auto Refresh.
[Handler]
public sealed partial class EvasionScanTask(IChromeBrowser browser, SwitchVillageCommand.Handler switchVillage,
    IDbContextFactory<AppDbContext> factory, TaskManager taskManager, AttackAlertWorker attackAlerts,
    EvasionSidebarCoordinator sidebar, ILogger logger)
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
        var trace = new EvasionTrace(logger, task.AccountId.Value, task.VillageId.Value, "-", "scan", 0, null, null);
        var originalTime = task.ExecuteAt;
        try { return await HandleCoreAsync(task, token, trace); }
        catch (Exception ex) { trace.Failure(ex); throw; }
        finally { if (task.ExecuteAt != originalTime) task.ExecuteAt = DateTime.Now.AddSeconds(10); }
    }

    private async ValueTask<Result> HandleCoreAsync(Task task, CancellationToken token, EvasionTrace trace)
    {
        trace.Retry("scan", "scan-started");
        using var db = factory.CreateDbContext();
        if (!task.CanStart(db)) { trace.Event("scan", "disabled-or-recovery-complete"); return Result.Ok(); }
        task.ExecuteAt = DateTime.Now.AddSeconds(10);
        var destination = db.ByName(task.VillageId, VillageSettingEnums.EvasionDestinationId);
        trace.SetDetails(destination, null, null);
        var owned = db.Villages.Where(x => x.AccountId == task.AccountId.Value).Select(x => x.Id).ToArray();
        var destinationValid = EvasionRules.IsOwnedDestination(task.VillageId.Value, destination, owned);
        if (!browser.IsInitialized) { trace.Retry("scan", "browser-unavailable"); return Result.Ok(); }
        if (!await LoginParser.IsIngamePage(browser.CurrentPage))
        {
            if (await LoginParser.IsLoginPage(browser.CurrentPage))
            {
                taskManager.AddOrUpdate(new LoginTask.Task(task.AccountId), first: true);
                trace.Retry("scan", "login-scheduled");
            }
            else trace.Retry("scan", "unrecognized-game-or-login-page");
            return Result.Ok();
        }
        token.ThrowIfCancellationRequested();
        var refreshStatus = await sidebar.EnsureFreshAsync(task.AccountId, async () => (await browser.Refresh()).IsSuccess);
        token.ThrowIfCancellationRequested();
        trace.Retry("sidebar", refreshStatus switch
        {
            SidebarRefreshStatus.Refreshed => "account-sidebar-refreshed",
            SidebarRefreshStatus.Reused => "recent-account-sidebar-reused",
            SidebarRefreshStatus.Failed => "account-sidebar-refresh-failed",
            _ => "waiting-after-failed-refresh"
        });
        if (refreshStatus is SidebarRefreshStatus.Failed or SidebarRefreshStatus.WaitingAfterFailure) return Result.Ok();
        if (refreshStatus == SidebarRefreshStatus.Refreshed)
            await AttackAlertObservation.CaptureAsync(browser.CurrentPage, task.AccountId, attackAlerts);
        var node = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await node.CountAsync() != 1) { trace.Retry("scan", "village-node-unavailable"); return Result.Ok(); }
        var underAttack = await node.EvaluateAsync<bool>("node => node.classList.contains('attack')");
        EvasionStore.EnsureSchema(db);
        var unresolved = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == task.AccountId.Value && x.VillageId == task.VillageId.Value && x.Kind == "troop" && x.State == "Submitting").ToArray();
        if (!EvasionSidebarCoordinator.ShouldInspect(underAttack, unresolved.Length > 0)) { trace.Retry("scan", "no-attack-or-unresolved-movement"); return Result.Ok(); }
        var wasActive = await VillagePanelParser.IsActive(node);
        trace.Retry("navigate", "switching-attacked-or-recovery-village");
        var switched = await switchVillage.HandleAsync(new(task.VillageId), token);
        if (switched.IsFailed) { trace.Retry("navigate", "switch-village-failed"); return switched; }
        if (await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) { trace.Retry("scan", "active-village-changed"); return Result.Ok(); }
        node = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await node.CountAsync() != 1) { trace.Retry("scan", "village-node-unavailable-after-switch"); return Result.Ok(); }
        underAttack = await node.EvaluateAsync<bool>("node => node.classList.contains('attack')");
        if (!EvasionSidebarCoordinator.ShouldInspect(underAttack, unresolved.Length > 0)) { trace.Retry("scan", "attack-cleared-before-rally"); return Result.Ok(); }
        var uri = new Uri(new Uri(browser.CurrentUrl), "/build.php?id=39&tt=1&gid=16");
        var overviewIsFresh = IsRallyOverview(browser.CurrentUrl) && (refreshStatus == SidebarRefreshStatus.Refreshed || !wasActive);
        if (!overviewIsFresh)
        {
            trace.Retry("navigate", "opening-rally");
            var result = await browser.Navigate(uri.ToString());
            if (result.IsFailed) { trace.Retry("navigate", "rally-navigation-failed"); return result; }
        }
        else trace.Retry("navigate", "fresh-rally-reused");
        if (await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) { trace.Retry("scan", "active-village-changed-after-navigation"); return Result.Ok(); }
        await ReconcileUncertainAsync(db, task, unresolved);
        if (!underAttack) return Result.Ok();
        if (!destinationValid) { logger.Warning("Evasion scan skipped account={AccountId} village={VillageId} stage=validate reason=destination-not-another-owned-village destination={DestinationId}", task.AccountId.Value, task.VillageId.Value, destination); return Result.Ok(); }
        trace.Retry("parse", "reading-incoming-movements");
        var report = RallyDiagnostics.RequireAttackWhenFlagged(await IncomingAttackAdapter.ReadAsync(browser.CurrentPage), underAttack);
        RallyDiagnostics.Log(logger, task.AccountId.Value, task.VillageId.Value, "scan", report);
        if (!report.Complete) return Result.Ok();
        foreach (var attack in report.Attacks)
        {
            foreach (var kind in new[] { "merchant", "troop" })
            {
                void Decision(string reason) => EvasionDecisionLog.Scheduling(logger, task.AccountId.Value, task.VillageId.Value, attack.Id, kind, destination, attack.ImpactAt, reason);
                if (attack.ImpactAt <= DateTimeOffset.UtcNow) { Decision("impact-expired"); continue; }
                var enabled = db.BooleanByName(task.VillageId, kind == "merchant" ? VillageSettingEnums.ResourceEvasionEnable : VillageSettingEnums.TroopEvasionEnable);
                if (!enabled) { Decision("disabled"); continue; }
                if (kind == "troop" && db.ByName(task.VillageId, VillageSettingEnums.ConfirmedCapitalStatus) != 2) { Decision("capital-or-unknown-excluded"); continue; }
                var created = EvasionStore.TryPlan(db, new EvasionDispatch { AccountId = task.AccountId.Value, VillageId = task.VillageId.Value,
                    DestinationId = destination, AttackId = attack.Id, Kind = kind, ImpactUtc = attack.ImpactAt.ToUniversalTime().ToString("O"), Payload = "{}" });
                if (kind == "merchant")
                {
                    var planned = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == task.AccountId.Value && x.VillageId == task.VillageId.Value && x.AttackId == attack.Id && x.Kind == kind);
                    if (planned is null || planned.State != "Planned" || planned.DestinationId != destination || !DateTimeOffset.TryParse(planned.ImpactUtc, out var impact)) { Decision("existing-record-not-planned-or-destination-changed"); continue; }
                    if (DateTimeOffset.UtcNow > impact.AddSeconds(-29)) { Decision("missed-deadline"); continue; }
                    var operation = new EvasionMerchantTask.Task(task.AccountId, task.VillageId, attack.Id, destination, impact)
                    { ExecuteAt = impact.AddSeconds(-40).LocalDateTime };
                    if (!taskManager.GetTaskList(task.AccountId).Any(x => x is EvasionMerchantTask.Task && x.Key == operation.Key))
                    { taskManager.Add(operation); Decision(created ? "new-enqueued" : "existing-enqueued"); }
                    else Decision("already-enqueued");
                }
                else
                {
                    var planned = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == task.AccountId.Value && x.VillageId == task.VillageId.Value && x.AttackId == attack.Id && x.Kind == kind);
                    if (planned is null || planned.State != "Planned" || planned.DestinationId != destination || !DateTimeOffset.TryParse(planned.ImpactUtc, out var impact)) { Decision("existing-record-not-planned-or-destination-changed"); continue; }
                    if (DateTimeOffset.UtcNow > impact.AddSeconds(-19)) { Decision("missed-deadline"); continue; }
                    var operation = new EvasionTroopTask.Task(task.AccountId, task.VillageId, attack.Id, destination, impact)
                    { ExecuteAt = impact.AddSeconds(-28).LocalDateTime };
                    if (!taskManager.GetTaskList(task.AccountId).Any(x => x is EvasionTroopTask.Task && x.Key == operation.Key))
                    { taskManager.Add(operation); Decision(created ? "new-enqueued" : "existing-enqueued"); }
                    else Decision("already-enqueued");
                }
            }
        }
        return Result.Ok();
    }

    private static bool IsRallyOverview(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.AbsolutePath.EndsWith("/build.php", StringComparison.OrdinalIgnoreCase)) return false;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2).ToArray();
        return query.Any(pair => pair[0] == "id" && pair[1] == "39") && query.Any(pair => pair[0] == "tt" && pair[1] == "1");
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
                logger.Information("Evasion account={AccountId} village={VillageId} attack={AttackId} kind=troop destination={DestinationId} stage=reconcile reason=exact-movement-observed movement={MovementId}", task.AccountId.Value, task.VillageId.Value, dispatch.AttackId, dispatch.DestinationId, movement.Id);
                var cancel = new EvasionCancelTask.Task(task.AccountId, task.VillageId, dispatch.AttackId, movement.Id)
                { ExecuteAt = impact.AddSeconds(1).LocalDateTime };
                if (!taskManager.GetTaskList(task.AccountId).Any(x => x is EvasionCancelTask.Task && x.Key == cancel.Key))
                {
                    taskManager.Add(cancel);
                    logger.Information("Evasion account={AccountId} village={VillageId} attack={AttackId} kind=troop destination={DestinationId} stage=cancel reason=recovered-movement-cancellation-scheduled movement={MovementId}", task.AccountId.Value, task.VillageId.Value, dispatch.AttackId, dispatch.DestinationId, movement.Id);
                }
            }
            catch (JsonException) { logger.Warning("Uncertain troop evasion has invalid saved data and requires manual inspection."); }
            catch (KeyNotFoundException) { logger.Warning("Uncertain troop evasion has incomplete saved data and requires manual inspection."); }
            catch (InvalidOperationException) { logger.Warning("Uncertain troop evasion has malformed saved data and requires manual inspection."); }
            catch (FormatException) { logger.Warning("Uncertain troop evasion has malformed saved data and requires manual inspection."); }
        }
    }
}
