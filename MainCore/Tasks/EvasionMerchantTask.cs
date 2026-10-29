using System.Text.Json;
using MainCore.Commands.Navigate;
using MainCore.Infrasturecture.Extensions;
using MainCore.Services.Evasion;
using MainCore.Tasks.Base;

namespace MainCore.Tasks;

// A single merchant attempt; Submitting is persisted before any potentially committing click.
[Handler]
public sealed partial class EvasionMerchantTask(IChromeBrowser browser, SwitchVillageCommand.Handler switchVillage,
    IDbContextFactory<AppDbContext> factory, ILogger logger)
{
    public sealed class Task(AccountId accountId, VillageId villageId, string attackId, int destinationId, DateTimeOffset impact)
        : VillageTask(accountId, villageId), IEvasionDeadlineTask
    {
        public string AttackId { get; } = attackId;
        public int DestinationId { get; } = destinationId;
        public DateTimeOffset Impact { get; } = impact;
        public override string Key => $"{AccountId}-{VillageId}-{AttackId}-merchant";
        protected override string TaskName => "Evade resources";
    }

    private async ValueTask<Result> HandleAsync(Task task, CancellationToken token)
    {
        var trace = new EvasionTrace(logger, task.AccountId.Value, task.VillageId.Value, task.AttackId, "merchant", task.DestinationId, task.Impact, task.Impact.AddSeconds(-30));
        try { return await HandleCoreAsync(task, token, trace); }
        catch (Exception ex) { trace.Failure(ex); throw; }
    }

    private async ValueTask<Result> HandleCoreAsync(Task task, CancellationToken token, EvasionTrace trace)
    {
        Result Skip(string stage, string reason) { trace.Event(stage, reason); return Result.Ok(); }
        var due = task.Impact.AddSeconds(-30);
        trace.Event("start", "operation-started");
        var key = new EvasionDispatch { AccountId = task.AccountId.Value, VillageId = task.VillageId.Value, AttackId = task.AttackId, Kind = "merchant" };
        if (DateTimeOffset.UtcNow > due.AddSeconds(1)) return Skip("start", "missed-deadline");
        using var db = factory.CreateDbContext();
        EvasionStore.EnsureSchema(db);
        var planned = db.EvasionDispatches.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.VillageId == key.VillageId && x.AttackId == key.AttackId && x.Kind == key.Kind);
        if (planned is null || planned.State != "Planned" || planned.DestinationId != task.DestinationId ||
            !db.BooleanByName(task.VillageId, VillageSettingEnums.ResourceEvasionEnable) ||
            db.ByName(task.VillageId, VillageSettingEnums.EvasionDestinationId) != task.DestinationId) return Skip("validate", "record-setting-or-destination-changed");
        var destination = db.Villages.AsNoTracking().SingleOrDefault(x => x.AccountId == key.AccountId && x.Id == task.DestinationId && x.Id != key.VillageId);
        if (destination is null || !browser.IsInitialized || !await LoginParser.IsIngamePage(browser.CurrentPage)) return Skip("validate", "destination-or-session-unavailable");
        trace.Event("navigate", "switch-village");
        var result = await switchVillage.HandleAsync(new(task.VillageId), token);
        if (result.IsFailed) { trace.Event("navigate", "switch-village-failed"); return result; }
        var address = new Uri(new Uri(browser.CurrentUrl), $"/build.php?gid=17&t=5&x={destination.X}&y={destination.Y}");
        result = await browser.Navigate(address.ToString());
        if (result.IsFailed) { trace.Event("navigate", "marketplace-navigation-failed"); return result; }
        if (await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != task.VillageId) return Skip("navigate", "village-changed");
        trace.Event("prepare", "reading-marketplace");
        var prepared = await MarketplaceAdapter.PrepareAsync(browser.CurrentPage, destination.X, destination.Y);
        if (prepared is null) return Skip("prepare", "marketplace-layout-capacity-or-resources-unavailable");
        trace.Resources(prepared);
        var remaining = due.AddSeconds(-1) - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(remaining, token);
        prepared = await MarketplaceAdapter.PrepareAsync(browser.CurrentPage, destination.X, destination.Y);
        if (prepared is null) return Skip("prepare", "marketplace-recheck-failed");
        trace.Resources(prepared);
        if (!EvasionStore.SavePayload(db, key, JsonSerializer.Serialize(new { destination.X, destination.Y, prepared.Amounts, prepared.Capacity }))) return Skip("prepare", "journal-payload-not-saved");
        remaining = due - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(remaining, token);
        token.ThrowIfCancellationRequested();
        if (!EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1))) return Skip("submit", "missed-deadline");
        var node = VillagePanelParser.GetVillageNode(browser.CurrentPage, task.VillageId);
        if (await node.CountAsync() != 1 || !await VillagePanelParser.IsActive(node) || !await node.EvaluateAsync<bool>("n => n.classList.contains('attack')")) return Skip("submit", "attack-or-active-village-not-confirmed");
        using (var current = factory.CreateDbContext())
            if (!current.BooleanByName(task.VillageId, VillageSettingEnums.ResourceEvasionEnable) || current.ByName(task.VillageId, VillageSettingEnums.EvasionDestinationId) != task.DestinationId) return Skip("submit", "settings-changed");
        if (!await MarketplaceAdapter.MatchesAsync(browser.CurrentPage, destination.X, destination.Y, prepared.Amounts) ||
            !EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)) ||
            !EvasionStore.BeginSubmit(db, key)) return Skip("submit", "form-deadline-or-journal-recheck-failed");
        trace.Event("intent", "submission-intent-persisted");
        if (!EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1))) return Skip("submit", "deadline-missed-after-intent");
        var button = browser.CurrentPage.Locator("#marketplaceSendResources").GetByRole(AriaRole.Button, new() { Name = "Send resources", Exact = true });
        if (await button.CountAsync() != 1) return Skip("submit", "send-control-unavailable-after-intent");
        trace.Event("submit", "first-click-starting");
        await button.ClickAsync(new() { Timeout = 2000 });
        trace.Event("submit", "first-click-returned-outcome-unconfirmed");
        logger.Information("Resource evasion first click drift from target: {Seconds:F2} seconds", (DateTimeOffset.UtcNow - due).TotalSeconds);
        // The first click may send directly. Only an unmistakable, matching confirmation
        // page may be clicked again; otherwise leave the intent unresolved for review.
        var confirm = await ResourceConfirmationAdapter.FindAsync(browser.CurrentPage, destination.X, destination.Y, prepared.Amounts);
        if (confirm is not null && EvasionRules.IsSendWindow(DateTimeOffset.UtcNow, task.Impact, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3)) && !token.IsCancellationRequested)
        {
            trace.Event("confirmation", "confirmation-click-starting");
            await confirm.ClickAsync(new() { Timeout = 2000 });
            trace.Event("confirmation", "confirmation-click-returned-outcome-unconfirmed");
            logger.Information("Resource confirmation click drift from target: {Seconds:F2} seconds", (DateTimeOffset.UtcNow - due).TotalSeconds);
        }
        else trace.Event("confirmation", confirm is null ? "confirmation-unrecognized-or-direct-submit" : "confirmation-window-missed-or-cancelled");
        trace.Event("result", "resource-outcome-unconfirmed-no-automatic-repeat");
        return Result.Ok();
    }
}
