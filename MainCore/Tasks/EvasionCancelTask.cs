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
        using var db = factory.CreateDbContext();
        EvasionStore.EnsureSchema(db);
        var key = new EvasionDispatch { AccountId = task.AccountId.Value, VillageId = task.VillageId.Value, AttackId = task.AttackId, Kind = "troop" };
        var saved = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId && x.AttackId == key.AttackId && x.Kind == key.Kind);
        if (saved is null || saved.State != "Sent" || saved.MovementId != task.MovementId || !DateTimeOffset.TryParse(saved.SentUtc, out var sent) || !DateTimeOffset.TryParse(saved.ArrivalUtc, out var arrival)) return Result.Ok();
        if (!browser.IsInitialized || !await LoginParser.IsIngamePage(browser.CurrentPage)) return RetryRead(task, sent, arrival);
        var result = await switchVillage.HandleAsync(new(task.VillageId), token);
        if (result.IsFailed) return RetryRead(task, sent, arrival);
        var rally = new Uri(new Uri(browser.CurrentUrl), "/build.php?id=39&tt=1&gid=16");
        result = await browser.Navigate(rally.ToString());
        if (result.IsFailed || await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) return RetryRead(task, sent, arrival);
        var now = DateTimeOffset.UtcNow;
        var live = await IncomingAttackAdapter.ReadAsync(browser.CurrentPage, now);
        if (live is null) { logger.Warning("Troop return deferred: current attack timings are unreadable."); return RetryRead(task, sent, arrival); }
        var villageNode = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await villageNode.CountAsync() != 1 ||
            await villageNode.EvaluateAsync<bool>("n => n.classList.contains('attack')") && live.Count == 0)
        { logger.Warning("Troop return deferred: an attack is indicated but no timed hostile movement was recognized."); return RetryRead(task, sent, arrival); }
        var impacts = db.EvasionDispatches.AsNoTracking().Where(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId)
            .Select(x => x.ImpactUtc).ToArray().Select(x => DateTimeOffset.TryParse(x, out var hit) ? hit : DateTimeOffset.MinValue)
            .Where(x => x != DateTimeOffset.MinValue).Concat(live.Select(x => x.ImpactAt));
        var safeAt = EvasionRules.SafeCancelAt(sent, arrival, impacts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        if (safeAt is null || now >= sent.AddSeconds(87) || now >= arrival)
        { logger.Warning("Troop return could not be safely cancelled within its travel and 90-second limits; inspect the movement manually."); return Result.Ok(); }
        if (now < safeAt)
        {
            task.ExecuteAt = safeAt.Value.LocalDateTime;
            return Result.Ok();
        }
        var button = await RallyRecallAdapter.FindAsync(browser.CurrentPage, task.MovementId);
        if (button is null) { logger.Warning("Exact evasion movement cannot yet be identified for cancellation."); return RetryRead(task, sent, arrival); }
        token.ThrowIfCancellationRequested();
        // Navigation and DOM inspection take time; enforce the limits again at the click.
        now = DateTimeOffset.UtcNow;
        if (now < safeAt || now >= sent.AddSeconds(87) || now >= arrival)
        { logger.Warning("Troop return missed its safe cancellation window; inspect the movement manually."); return Result.Ok(); }
        if (!EvasionStore.BeginCancel(db, key, task.MovementId)) return Result.Ok();
        await button.ClickAsync(new() { Timeout = 2000 });
        result = await browser.Navigate(rally.ToString());
        if (result.IsSuccess && await RallyRecallAdapter.FindAsync(browser.CurrentPage, task.MovementId) is null)
            EvasionStore.RecordCancelled(db, key, task.MovementId);
        else logger.Warning("Troop cancellation outcome is uncertain; it will not be repeated automatically.");
        return Result.Ok();
    }

    private static Result RetryRead(Task task, DateTimeOffset sent, DateTimeOffset arrival)
    {
        var next = DateTimeOffset.UtcNow.AddSeconds(2);
        if (next < sent.AddSeconds(87) && next < arrival) task.ExecuteAt = next.LocalDateTime;
        return Result.Ok();
    }
}
