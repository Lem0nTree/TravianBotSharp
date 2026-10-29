namespace MainCore.Commands.Features.UseHeroItem;

[Handler]
public sealed partial class UseHeroResourceCommand(IChromeBrowser browser, ILogger logger)
{
    public sealed record Command(VillageId VillageId, NormalBuildPlan Plan, long[] Resource, long[] Required) : IVillageCommand;

    private async ValueTask<Result> HandleAsync(Command command, CancellationToken token) => await ExecuteAsync(command, token);

    public async Task<Result> ExecuteAsync(Command command, CancellationToken token, TimeSpan? completionTimeout = null)
    {
        var (villageId, plan, resource, required) = command;
        if (resource.Length != 4 || required.Length < 4 || resource.Any(x => x < 0) || !resource.Any(x => x > 0) || required.Any(x => x < 0))
            return Stop.Error.WithError("Hero resource request is invalid.");
        if (!await IsBuildingContextAsync(browser.CurrentPage, villageId, plan))
            return Stop.Error.WithError("Hero resources were not used: village or building context changed.");
        var originalUrl = browser.CurrentUrl;
        var fillUpButton = await UpgradeParser.GetFillUpButton(browser.CurrentPage, plan.Type);
        var result = await browser.Click(fillUpButton);
        if (result.IsFailed) return result;
        result = await browser.Wait(browser.CurrentPage.Locator("div.resourceTransferDialog:visible"));
        if (result.IsFailed) return result;

        var inventory = await HeroResourceTransferParser.WaitUntilReadableAsync(browser.CurrentPage, token);
        if (!inventory.Readable)
        {
            logger.Warning("Hero resources stage=inventory_unreadable village={VillageId} slot={Slot} reason={Reason}; no transfer submitted", villageId.Value, plan.Location, inventory.Reason);
            return Stop.Error.WithError("Hero inventory amounts could not be verified. No transfer was submitted.");
        }
        logger.Information("Hero resources stage=inventory_verified village={VillageId} slot={Slot} required={@Required} available={@Available}", villageId.Value, plan.Location, resource, inventory.Available);
        var errors = new List<Error>();
        for (var i = 0; i < 4; i++)
            if (inventory.Available[i] < resource[i]) errors.Add(MissingResource.Error(HeroResourceTransferParser.Names[i], inventory.Available[i], resource[i]));
        if (errors.Count > 0)
        {
            logger.Information("Hero resources stage=inventory_insufficient village={VillageId} slot={Slot}; no transfer submitted, waiting for village production", villageId.Value, plan.Location);
            var cancel = browser.CurrentPage.Locator("div.resourceTransferDialog:visible .dialogCancelButton");
            // Closing a shortage dialog is read-only; leave the build timer in place.
            result = await cancel.CountAsync() == 1 ? await browser.Click(cancel) : await browser.Navigate(originalUrl);
            if (result.IsFailed || !await IsBuildingContextAsync(browser.CurrentPage, villageId, plan))
                return Stop.Error.WithError("Hero inventory is insufficient and the building page could not be restored.");
            return Result.Fail(errors);
        }

        for (var i = 0; i < 4; i++)
        {
            var field = browser.CurrentPage.Locator($"div.resourceTransferDialog:visible .resourceRow input[name='{HeroResourceTransferParser.Names[i]}']");
            if (await field.CountAsync() != 1 || !await field.IsEnabledAsync())
                return Stop.Error.WithError("Hero transfer inputs could not be verified. No transfer was submitted.");
            await field.FillAsync(resource[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        for (var i = 0; i < 4; i++)
        {
            var field = browser.CurrentPage.Locator($"div.resourceTransferDialog:visible .resourceRow input[name='{HeroResourceTransferParser.Names[i]}']");
            if (!HeroResourceTransferParser.TryAmount(await field.InputValueAsync(), out var selected) || selected != resource[i])
                return Stop.Error.WithError("Hero transfer selection changed. No transfer was submitted.");
        }
        if (!await IsBuildingContextAsync(browser.CurrentPage, villageId, plan))
            return Stop.Error.WithError("Hero transfer was not submitted because the village or building changed.");
        var confirm = browser.CurrentPage.Locator("div.resourceTransferDialog:visible .actionButton button");
        if (await confirm.CountAsync() != 1 || !await confirm.IsEnabledAsync() || await confirm.EvaluateAsync<bool>("n => n.classList.contains('disabled')"))
            return Stop.Error.WithError("Hero transfer confirmation is unavailable. No transfer was submitted.");
        token.ThrowIfCancellationRequested();
        // A timeout or lost response after this point must never replay the transfer.
        try
        {
            logger.Information("Hero resources stage=confirmation_attempt village={VillageId} slot={Slot} selected={@Selected}; outcome pending", villageId.Value, plan.Location, resource);
            result = await browser.Click(confirm);
            if (result.IsFailed) return Uncertain();
            var until = DateTimeOffset.UtcNow + (completionTimeout ?? TimeSpan.FromSeconds(10));
            do
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (await IsBuildingContextAsync(browser.CurrentPage, villageId, plan) &&
                        await browser.CurrentPage.Locator("div.resourceTransferDialog:visible").CountAsync() == 0 &&
                        !new Uri(browser.CurrentUrl).Query.Contains("reload=auto", StringComparison.OrdinalIgnoreCase))
                    {
                        var stocks = await ReadStocksAsync(browser.CurrentPage);
                        if (stocks is not null && Enumerable.Range(0, 4).All(i => stocks[i] >= required[i]))
                        {
                            logger.Information("Hero resources stage=stocks_verified village={VillageId} slot={Slot} stocks={@Stocks}", villageId.Value, plan.Location, stocks);
                            return Result.Ok();
                        }
                    }
                }
                catch (PlaywrightException ex) when (ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase)) { /* Retry reads during a redirect, never the transfer. */ }
                await Task.Delay(100, token);
            } while (DateTimeOffset.UtcNow < until);
        }
        catch (Exception ex) { logger.Warning("Hero resources stage=transfer_uncertain errorType={ErrorType}", ex.GetType().Name); }
        return Uncertain();

        Result Uncertain()
        {
            logger.Warning("Hero resources stage=transfer_uncertain village={VillageId} slot={Slot}; inspect the game, transfer will not be retried automatically", villageId.Value, plan.Location);
            return Stop.Error.WithError("Hero transfer outcome is uncertain. Inspect the game before resuming.");
        }
    }

    public static async Task<bool> IsBuildingContextAsync(IPage page, VillageId villageId, NormalBuildPlan plan) =>
        await page.EvaluateAsync<bool>("""
            p => {
              const url = new URL(location.href);
              const active = document.querySelectorAll('#sidebarBoxVillageList .listEntry.village.active');
              const build = document.querySelector('#build');
              return url.pathname.endsWith('/build.php') && url.searchParams.get('id') === String(p.slot)
                && active.length === 1 && active[0].getAttribute('data-did') === String(p.village)
                && !!build && (build.classList.contains('gid' + p.type)
                  || build.classList.contains('gid0') && !!build.querySelector('#contract_building' + p.type));
            }
            """, new { village = villageId.Value, slot = plan.Location, type = (int)plan.Type });

    private static async Task<long[]?> ReadStocksAsync(IPage page)
    {
        var raw = await page.EvaluateAsync<string[]>("() => [1,2,3,4].map(i => document.querySelector('#l' + i)?.textContent || '')");
        if (raw.Length != 4) return null;
        var values = new long[4];
        for (var i = 0; i < 4; i++) if (!HeroResourceTransferParser.TryAmount(raw[i], out values[i])) return null;
        return values;
    }
}
