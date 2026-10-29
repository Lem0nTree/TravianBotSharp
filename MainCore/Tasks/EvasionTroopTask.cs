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
    {
        var due = task.Impact.AddSeconds(-20);
        var key = new EvasionDispatch { AccountId = task.AccountId.Value, VillageId = task.VillageId.Value, AttackId = task.AttackId, Kind = "troop" };
        if (DateTimeOffset.UtcNow > due.AddSeconds(1)) return Result.Ok();
        using var db = factory.CreateDbContext();
        EvasionStore.EnsureSchema(db);
        var planned = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId && x.AttackId == key.AttackId && x.Kind == key.Kind);
        if (planned is null || planned.State != "Planned" || planned.DestinationId != task.DestinationId ||
            !db.BooleanByName(task.VillageId, VillageSettingEnums.TroopEvasionEnable) ||
            db.ByName(task.VillageId, VillageSettingEnums.ConfirmedCapitalStatus) != 2 ||
            db.ByName(task.VillageId, VillageSettingEnums.EvasionDestinationId) != task.DestinationId) return Result.Ok();
        var unresolved = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId && x.Kind == "troop" &&
                (x.State == "Submitting" || x.State == "Sent" || x.State == "Cancelling"))
            .Select(x => x.SentUtc).ToArray();
        // An unresolved movement may still need its 90-second cancellation. After that
        // interval it remains in the journal for manual recovery but cannot block new waves forever.
        if (unresolved.Any(x => !DateTimeOffset.TryParse(x, out var sent) || DateTimeOffset.UtcNow < sent.AddSeconds(90))) return Result.Ok();
        var destination = db.Villages.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.Id == task.DestinationId && x.Id != key.VillageId);
        var source = db.Villages.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.Id == key.VillageId);
        if (destination is null || source is null || !browser.IsInitialized || !await LoginParser.IsIngamePage(browser.CurrentPage)) return Result.Ok();
        var result = await switchVillage.HandleAsync(new(task.VillageId), token);
        if (result.IsFailed) return result;
        var rally = new Uri(new Uri(browser.CurrentUrl), "/build.php?id=39&tt=1&gid=16");
        result = await browser.Navigate(rally.ToString());
        if (result.IsFailed || await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) return result;
        var sourceTile = await RallyMovementAdapter.SourceTileAsync(browser.CurrentPage, source.Name);
        var destinationTile = sourceTile is null ? null : MapTileIdentity.TargetTile(sourceTile.Value, source.X, source.Y, destination.X, destination.Y);
        if (destinationTile is null) { logger.Warning("Troop evasion skipped: the source map tile could not be verified from an outgoing movement."); return Result.Ok(); }
        var before = await RallyMovementAdapter.RecallIdsAsync(browser.CurrentPage);
        var sendPage = new Uri(rally, $"/build.php?id=39&gid=16&tt=2&x={destination.X}&y={destination.Y}");
        result = await browser.Navigate(sendPage.ToString());
        if (result.IsFailed || await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) return result;
        var prepared = await TroopSendAdapter.PrepareAsync(browser.CurrentPage, destination.X, destination.Y);
        if (prepared is null) { logger.Warning("Troop evasion skipped: sending form is unrecognized."); return Result.Ok(); }
        var remaining = due.AddSeconds(-1) - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(remaining, token);
        prepared = await TroopSendAdapter.PrepareAsync(browser.CurrentPage, destination.X, destination.Y);
        if (prepared is null || !EvasionStore.SavePayload(db, key, JsonSerializer.Serialize(new { destination.X, destination.Y, DestinationTile = destinationTile.Value, prepared.Amounts, Before = before }))) return Result.Ok();
        remaining = due - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(remaining, token);
        token.ThrowIfCancellationRequested();
        if (!EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1))) return Result.Ok();
        var node = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await node.CountAsync() != 1 || !await VillagePanelParser.IsActive(node) || !await node.EvaluateAsync<bool>("n => n.classList.contains('attack')")) return Result.Ok();
        using (var current = factory.CreateDbContext())
            if (!current.BooleanByName(task.VillageId, VillageSettingEnums.TroopEvasionEnable) || current.ByName(task.VillageId, VillageSettingEnums.ConfirmedCapitalStatus) != 2 || current.ByName(task.VillageId, VillageSettingEnums.EvasionDestinationId) != task.DestinationId) return Result.Ok();
        if (!await TroopSendAdapter.MatchesAsync(browser.CurrentPage, destination.X, destination.Y, prepared.Amounts) ||
            !EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1)) ||
            !EvasionStore.BeginSubmit(db, key)) return Result.Ok();
        if (!EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1))) return Result.Ok();
        await browser.CurrentPage.Locator("button#ok[type=submit]").ClickAsync(new() { Timeout = 2000 });
        logger.Information("Troop evasion first click drift from target: {Seconds:F2} seconds", (DateTimeOffset.UtcNow - due).TotalSeconds);
        var confirmation = await TroopConfirmationAdapter.FindAsync(browser.CurrentPage, destination.X, destination.Y, prepared.Amounts);
        if (confirmation is null) { logger.Warning("Troop evasion outcome is uncertain or confirmation is unsupported; it will not be retried."); return Result.Ok(); }
        var sentAt = DateTimeOffset.UtcNow;
        var impacts = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId)
            .Select(x => x.ImpactUtc).ToArray().Select(x => DateTimeOffset.TryParse(x, out var hit) ? hit : DateTimeOffset.MinValue).Where(x => x != DateTimeOffset.MinValue).Append(task.Impact);
        var cancelAt = EvasionRules.SafeCancelAt(sentAt, sentAt + confirmation.Duration, impacts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        if (cancelAt is null || !EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3)) || token.IsCancellationRequested)
        { logger.Warning("Troop evasion confirmation was not submitted: no safe cancellation window."); return Result.Ok(); }
        await confirmation.Button.ClickAsync(new() { Timeout = 2000 });
        logger.Information("Troop confirmation click drift from target: {Seconds:F2} seconds", (DateTimeOffset.UtcNow - due).TotalSeconds);
        result = await browser.Navigate(rally.ToString());
        if (result.IsFailed) return Result.Ok();
        var movement = await RallyMovementAdapter.FindUniqueNewAsync(browser.CurrentPage, before, destinationTile.Value, prepared.Amounts);
        if (movement is null || !EvasionStore.RecordSent(db, key, movement.Id, sentAt, DateTimeOffset.UtcNow + movement.Remaining))
        { logger.Warning("Troop evasion movement is ambiguous; automatic cancellation is unavailable and manual inspection is required."); return Result.Ok(); }
        var cancel = new EvasionCancelTask.Task(task.AccountId, task.VillageId, task.AttackId, movement.Id) { ExecuteAt = cancelAt.Value.LocalDateTime };
        tasks.Add(cancel);
        return Result.Ok();
    }
}
