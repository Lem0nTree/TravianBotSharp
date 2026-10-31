using System.Text.Json;
using MainCore.Commands.Navigate;
using MainCore.Infrasturecture.Extensions;
using MainCore.Services.Evasion;
using MainCore.Tasks.Base;

namespace MainCore.Tasks;

[Handler]
public sealed partial class EvasionTroopTask(IChromeBrowser browser, SwitchVillageCommand.Handler switchVillage,
    IDbContextFactory<AppDbContext> factory, TaskManager tasks, ILogger logger)
{
    public sealed class Task(AccountId accountId, VillageId villageId, string attackId, int destinationId, DateTimeOffset impact)
        : VillageTask(accountId, villageId), IEvasionDeadlineTask
    {
        public string AttackId { get; } = attackId;
        public int DestinationId { get; } = destinationId;
        public DateTimeOffset Impact { get; } = impact;
        public override string Key => $"{AccountId}-{VillageId}-{AttackId}-troop";
        protected override string TaskName => "Evade troops";
    }

    private async ValueTask<Result> HandleAsync(Task task, CancellationToken token)
        => await ExecuteAsync(task, token);

    public async ValueTask<Result> ExecuteAsync(Task task, CancellationToken token)
    {
        var trace = new EvasionTrace(logger, task.AccountId.Value, task.VillageId.Value, task.AttackId, "troop", task.DestinationId, task.Impact, task.Impact - EvasionRules.TroopLead);
        try { return await HandleCoreAsync(task, token, trace); }
        catch (Exception ex) { trace.Failure(ex); throw; }
    }

    private async ValueTask<Result> HandleCoreAsync(Task task, CancellationToken token, EvasionTrace trace)
    {
        Result Skip(string stage, string reason) { trace.Event(stage, reason); return Result.Ok(); }
        var due = task.Impact - EvasionRules.TroopLead;
        trace.Event("start", "operation-started");
        var key = new EvasionDispatch { AccountId = task.AccountId.Value, VillageId = task.VillageId.Value, AttackId = task.AttackId, Kind = "troop" };
        if (DateTimeOffset.UtcNow > due.AddSeconds(1)) return Skip("start", "missed-deadline");
        using var db = factory.CreateDbContext();
        EvasionStore.EnsureSchema(db);
        var planned = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId && x.AttackId == key.AttackId && x.Kind == key.Kind);
        if (planned is null || planned.State != "Planned" || planned.DestinationId != task.DestinationId ||
            !db.BooleanByName(task.VillageId, VillageSettingEnums.TroopEvasionEnable) ||
            db.ByName(task.VillageId, VillageSettingEnums.ConfirmedCapitalStatus) != 2 ||
            ResolveDestination(db, task) != task.DestinationId) return Skip("validate", "record-setting-capital-or-destination-changed");
        var unresolved = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId && x.Kind == "troop" &&
                (x.State == "Submitting" || x.State == "Sent" || x.State == "Cancelling"))
            .Select(x => x.SentUtc).ToArray();
        // An unresolved movement may still need its 90-second cancellation. After that
        // interval it remains in the journal for manual recovery but cannot block new waves forever.
        if (unresolved.Any(x => !DateTimeOffset.TryParse(x, out var sent) || DateTimeOffset.UtcNow < sent.AddSeconds(90))) return Skip("validate", "previous-movement-unresolved");
        var destination = db.Villages.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.Id == task.DestinationId && x.Id != key.VillageId);
        var source = db.Villages.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.Id == key.VillageId);
        if (destination is null || source is null || !browser.IsInitialized || !await LoginParser.IsIngamePage(browser.CurrentPage)) return Skip("validate", "ownership-or-session-unavailable");
        trace.Event("navigate", "switch-village");
        var result = await switchVillage.HandleAsync(new(task.VillageId), token);
        if (result.IsFailed) { trace.Event("navigate", "switch-village-failed"); return result; }
        var rally = new Uri(new Uri(browser.CurrentUrl), "/build.php?id=39&tt=1&gid=16");
        result = await browser.Navigate(rally.ToString());
        if (result.IsFailed || await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) { trace.Event("navigate", "rally-navigation-or-village-check-failed"); return result; }
        var sourceTile = await RallyMovementAdapter.SourceTileAsync(browser.CurrentPage, source.Name);
        var destinationTile = sourceTile is null ? null : MapTileIdentity.TargetTile(sourceTile.Value, source.X, source.Y, destination.X, destination.Y);
        if (destinationTile is null) return Skip("validate", "source-or-destination-map-tile-unverified");
        trace.Event("validate", "destination-map-tile-verified");
        var before = await RallyMovementAdapter.RecallIdsAsync(browser.CurrentPage);
        var sendPage = new Uri(rally, $"/build.php?id=39&gid=16&tt=2&x={destination.X}&y={destination.Y}");
        result = await browser.Navigate(sendPage.ToString());
        if (result.IsFailed || await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) { trace.Event("navigate", "send-form-navigation-or-village-check-failed"); return result; }
        trace.Event("prepare", "reading-troop-form");
        var prepared = await TroopSendAdapter.PrepareAsync(browser.CurrentPage, destination.X, destination.Y);
        if (prepared is null) return Skip("prepare", "troop-form-or-available-troops-unrecognized");
        logger.Information("Evasion troop preparation account={AccountId} village={VillageId} attack={AttackId} destination={DestinationId} amounts={@Amounts}", task.AccountId.Value, task.VillageId.Value, task.AttackId, task.DestinationId, prepared.Amounts);
        if (!EvasionStore.SavePayload(db, key, JsonSerializer.Serialize(new { destination.X, destination.Y, DestinationTile = destinationTile.Value, prepared.Amounts, Before = before }))) return Skip("prepare", "journal-payload-not-saved");
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= due) return Skip("preview", "not-enough-time-to-prepare-confirmation");
        var node = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await node.CountAsync() != 1 || !await VillagePanelParser.IsActive(node) || !await node.EvaluateAsync<bool>("n => n.classList.contains('attack')")) return Skip("submit", "attack-or-active-village-not-confirmed");
        using (var current = factory.CreateDbContext())
            if (!SettingsMatch(current, task)) return Skip("preview", "settings-or-capital-status-changed");
        if (!await TroopSendAdapter.MatchesAsync(browser.CurrentPage, destination.X, destination.Y, prepared.Amounts) ||
            DateTimeOffset.UtcNow >= due ||
            !EvasionStore.BeginSubmit(db, key)) return Skip("submit", "form-deadline-or-journal-recheck-failed");
        trace.Event("intent", "submission-intent-persisted");
        if (DateTimeOffset.UtcNow >= due) return Skip("preview", "deadline-missed-after-intent");
        trace.Event("preview", "first-click-starting");
        try
        {
            await browser.CurrentPage.Locator("button#ok[type=submit]").ClickAsync(new() { Timeout = 2000 });
            trace.Event("preview", "first-click-returned-awaiting-confirmation");
        }
        catch (PlaywrightException) { trace.Event("preview", "first-click-response-uncertain-reading-only-no-repeat"); }
        var readiness = await TroopConfirmationAdapter.WaitAsync(browser.CurrentPage, task.VillageId.Value, destination.X, destination.Y, prepared.Amounts, due, token);
        var confirmation = readiness.Confirmation;
        if (confirmation is null) return Skip("confirmation", readiness.Reason);
        trace.Event("confirmation", "matching-preview-ready-waiting-for-final-deadline");
        var impacts = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId)
            .Select(x => x.ImpactUtc).ToArray().Select(x => DateTimeOffset.TryParse(x, out var hit) ? hit : DateTimeOffset.MinValue).Where(x => x != DateTimeOffset.MinValue).Append(task.Impact);
        if (EvasionRules.SafeCancelAt(due, due + confirmation.Duration, impacts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)) is null)
            return Skip("confirmation", "unsafe-cancellation-window-before-send");
        var remaining = due - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(remaining, token);
        token.ThrowIfCancellationRequested();
        using (var current = factory.CreateDbContext())
            if (!SettingsMatch(current, task)) return Skip("confirmation", "settings-or-capital-status-changed");
        node = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await node.CountAsync() != 1 || !await VillagePanelParser.IsActive(node) || !await node.EvaluateAsync<bool>("n => n.classList.contains('attack')")) return Skip("confirmation", "attack-or-active-village-not-confirmed");
        var finalRead = await TroopConfirmationAdapter.ReadAsync(browser.CurrentPage, task.VillageId.Value, destination.X, destination.Y, prepared.Amounts);
        confirmation = finalRead.Confirmation;
        if (confirmation is null) return Skip("confirmation", finalRead.Reason);
        var sentAt = DateTimeOffset.UtcNow;
        var cancelAt = confirmation is null ? null : EvasionRules.SafeCancelAt(sentAt, sentAt + confirmation.Duration, impacts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        if (confirmation is null || cancelAt is null || !EvasionRules.IsSendWindow(sentAt, task.Impact, EvasionRules.TroopLead, TimeSpan.FromSeconds(1)) || token.IsCancellationRequested)
        { return Skip("confirmation", "unsafe-cancellation-window-or-deadline-missed"); }
        if (!EvasionStore.BeginFinalTroopSubmit(db, key, sentAt)) return Skip("confirmation", "final-attempt-time-not-saved");
        if (!EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, EvasionRules.TroopLead, TimeSpan.FromSeconds(1))) return Skip("confirmation", "deadline-missed-after-final-intent");
        trace.Event("confirmation", "confirmation-click-starting");
        try
        {
            await confirmation.Button.ClickAsync(new() { Timeout = 2000 });
            trace.Event("confirmation", "confirmation-click-returned-outcome-unconfirmed");
        }
        catch (PlaywrightException) { trace.Event("confirmation", "final-click-response-uncertain-reading-only-no-repeat"); }
        logger.Information("Troop final send click observed account={AccountId} village={VillageId} attack={AttackId} attemptedAt={AttemptedAt} observedAt={ObservedAt} driftSeconds={Drift}", task.AccountId.Value, task.VillageId.Value, task.AttackId, sentAt, DateTimeOffset.UtcNow, (sentAt - due).TotalSeconds);
        RallyMovementAdapter.MatchedMovement? movement = null;
        for (var attempt = 0; attempt < 3 && movement is null; attempt++)
        {
            try { await browser.CurrentPage.GotoAsync(rally.ToString(), new() { Timeout = 2000, WaitUntil = WaitUntilState.DOMContentLoaded }); }
            catch (PlaywrightException) { trace.Event("reconcile", "rally-read-navigation-delayed-inspecting-current-dom"); }
            try
            {
                if (await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) == task.VillageId)
                    movement = await RallyMovementAdapter.WaitForUniqueNewAsync(browser.CurrentPage, before, destinationTile.Value, prepared.Amounts, TimeSpan.FromSeconds(1));
            }
            catch (PlaywrightException) { trace.Event("reconcile", "movement-read-not-ready"); }
        }
        if (movement is null || !EvasionStore.RecordSent(db, key, movement.Id, sentAt, DateTimeOffset.UtcNow + movement.Remaining))
        { return Skip("reconcile", "movement-ambiguous-or-journal-write-failed-manual-inspection-required"); }
        trace.Event("reconcile", $"exact-movement-observed-{movement.Id}");
        var cancel = new EvasionCancelTask.Task(task.AccountId, task.VillageId, task.AttackId, movement.Id) { ExecuteAt = cancelAt.Value.LocalDateTime };
        tasks.Add(cancel);
        trace.Event("cancel", "exact-movement-cancellation-scheduled");
        return Result.Ok();
    }

    private static int ResolveDestination(AppDbContext db, Task task) => EvasionRules.TroopDestination(task.VillageId.Value,
        db.ByName(task.VillageId, VillageSettingEnums.EvasionDestinationId), db.Villages.Where(x => x.AccountId == task.AccountId.Value).Select(x => x.Id).ToArray());

    private static bool SettingsMatch(AppDbContext db, Task task) => db.BooleanByName(task.VillageId, VillageSettingEnums.TroopEvasionEnable) &&
        db.ByName(task.VillageId, VillageSettingEnums.ConfirmedCapitalStatus) == 2 && ResolveDestination(db, task) == task.DestinationId;
}
