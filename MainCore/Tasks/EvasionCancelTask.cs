using MainCore.Commands.Navigate;
using MainCore.Services.Evasion;
using MainCore.Tasks.Base;

namespace MainCore.Tasks;

[Handler]
public sealed partial class EvasionCancelTask(IChromeBrowser browser, SwitchVillageCommand.Handler switchVillage,
    IDbContextFactory<AppDbContext> factory, ILogger logger)
{
    public sealed class Task(AccountId accountId, VillageId villageId, string attackId, string movementId)
        : VillageTask(accountId, villageId), IEvasionDeadlineTask
    {
        public string AttackId { get; } = attackId;
        public string MovementId { get; } = movementId;
        public override string Key => $"{AccountId}-{VillageId}-{AttackId}-cancel-{MovementId}";
        protected override string TaskName => "Return evaded troops";
    }

    private async ValueTask<Result> HandleAsync(Task task, CancellationToken token)
    {
        var trace = new EvasionTrace(logger, task.AccountId.Value, task.VillageId.Value, task.AttackId, "troop-cancel", 0, null, null);
        try { return await HandleCoreAsync(task, token, trace); }
        catch (Exception ex) { trace.Failure(ex); throw; }
    }

    private async ValueTask<Result> HandleCoreAsync(Task task, CancellationToken token, EvasionTrace trace)
    {
        Result Skip(string stage, string reason) { trace.Event(stage, reason); return Result.Ok(); }
        Result Retry(string reason, DateTimeOffset sentAt, DateTimeOffset arrivalAt)
        {
            if (DateTimeOffset.UtcNow.AddSeconds(2) >= sentAt.AddSeconds(87) || DateTimeOffset.UtcNow.AddSeconds(2) >= arrivalAt)
                return Skip("cancel-read", "retry-window-expired-manual-inspection-required");
            trace.Retry("cancel-read", reason);
            return RetryRead(task, sentAt, arrivalAt);
        }
        trace.Retry("start", "cancellation-check-started");
        using var db = factory.CreateDbContext();
        EvasionStore.EnsureSchema(db);
        var key = new EvasionDispatch { AccountId = task.AccountId.Value, VillageId = task.VillageId.Value, AttackId = task.AttackId, Kind = "troop" };
        var saved = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId && x.AttackId == key.AttackId && x.Kind == key.Kind);
        if (saved is null || saved.State != "Sent" || saved.MovementId != task.MovementId || !DateTimeOffset.TryParse(saved.SentUtc, out var sent) || !DateTimeOffset.TryParse(saved.ArrivalUtc, out var arrival)) return Skip("validate", "saved-movement-or-times-invalid");
        var impact = DateTimeOffset.TryParse(saved.ImpactUtc, out var hitTime) ? (DateTimeOffset?)hitTime : null;
        trace.SetDetails(saved.DestinationId, impact, impact?.AddSeconds(1));
        if (!browser.IsInitialized || !await LoginParser.IsIngamePage(browser.CurrentPage)) return Retry("session-unavailable", sent, arrival);
        var result = await switchVillage.HandleAsync(new(task.VillageId), token);
        if (result.IsFailed) return Retry("switch-village-failed", sent, arrival);
        var rally = new Uri(new Uri(browser.CurrentUrl), "/build.php?id=39&tt=1&gid=16");
        result = await browser.Navigate(rally.ToString());
        if (result.IsFailed || await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) return Retry("rally-navigation-or-village-check-failed", sent, arrival);
        var now = DateTimeOffset.UtcNow;
        var villageNode = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await villageNode.CountAsync() != 1) return Retry("village-node-unavailable", sent, arrival);
        var flagged = await villageNode.EvaluateAsync<bool>("n => n.classList.contains('attack')");
        var report = RallyDiagnostics.RequireAttackWhenFlagged(await IncomingAttackAdapter.ReadAsync(browser.CurrentPage), flagged);
        RallyDiagnostics.Log(logger, task.AccountId.Value, task.VillageId.Value, "cancel", report);
        if (!report.Complete) return Retry("incoming-report-incomplete", sent, arrival);
        var impacts = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId)
            .Select(x => x.ImpactUtc).ToArray().Select(x => DateTimeOffset.TryParse(x, out var hit) ? hit : DateTimeOffset.MinValue)
            .Where(x => x != DateTimeOffset.MinValue).Concat(report.Attacks.Select(x => x.ImpactAt));
        var safeAt = EvasionRules.SafeCancelAt(sent, arrival, impacts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        if (safeAt is null || now >= sent.AddSeconds(87) || now >= arrival)
        { return Skip("cancel", "unsafe-cancellation-window-manual-inspection-required"); }
        if (now < safeAt)
        {
            task.ExecuteAt = safeAt.Value.LocalDateTime;
            return Skip("cancel", "deferred-until-safe-after-waves");
        }
        var button = await RallyRecallAdapter.FindAsync(browser.CurrentPage, task.MovementId);
        if (button is null) return Retry("exact-cancel-control-unavailable", sent, arrival);
        token.ThrowIfCancellationRequested();
        // Navigation and DOM inspection take time; enforce the limits again at the click.
        now = DateTimeOffset.UtcNow;
        if (now < safeAt || now >= sent.AddSeconds(87) || now >= arrival)
        { return Skip("cancel", "window-missed-manual-inspection-required"); }
        if (!EvasionStore.BeginCancel(db, key, task.MovementId)) return Skip("cancel", "cancellation-intent-not-saved");
        trace.Event("intent", $"cancellation-intent-persisted-movement-{task.MovementId}");
        trace.Event("cancel", "cancel-click-starting");
        await button.ClickAsync(new() { Timeout = 2000 });
        trace.Event("cancel", "cancel-click-returned-outcome-unconfirmed");
        result = await browser.Navigate(rally.ToString());
        if (result.IsSuccess && await RallyRecallAdapter.FindAsync(browser.CurrentPage, task.MovementId) is null)
        {
            if (EvasionStore.RecordCancelled(db, key, task.MovementId)) trace.Event("cancel", "exact-cancel-control-no-longer-observed");
            else trace.Event("cancel", "journal-update-failed-outcome-uncertain");
        }
        else trace.Event("cancel", "outcome-uncertain-no-automatic-repeat");
        return Result.Ok();
    }

    private static Result RetryRead(Task task, DateTimeOffset sent, DateTimeOffset arrival)
    {
        var next = DateTimeOffset.UtcNow.AddSeconds(2);
        if (next < sent.AddSeconds(87) && next < arrival) task.ExecuteAt = next.LocalDateTime;
        return Result.Ok();
    }
}
