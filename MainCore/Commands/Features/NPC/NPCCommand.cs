namespace MainCore.Commands.Features.NPC
{
    using MainCore.Infrasturecture.Extensions;

    [Handler]
    public static partial class NPCCommand
    {
        public sealed record Command(IChromeBrowser Browser, VillageId VillageId, long[] Values);

        private static async ValueTask<Result> HandleAsync(Command command, IDbContextFactory<AppDbContext> factory, ILogger logger, CancellationToken token)
        {
            bool Enabled()
            {
                using var db = factory.CreateDbContext();
                return db.Villages.Any(v => v.Id == command.VillageId.Value && v.AccountId == command.Browser.AccountId.Value)
                    && db.BooleanByName(command.VillageId, VillageSettingEnums.AutoNPCEnable);
            }
            return await ExecuteAsync(command, Enabled, logger, token);
        }

        public static async Task<Result> ExecuteAsync(Command command, Func<bool> enabled, ILogger logger, CancellationToken token, TimeSpan? completionTimeout = null)
        {
            var (browser, villageId, values) = command;
            if (!enabled()) return Skip.Error.WithError("NPC was disabled before preparing the exchange.");
            if (values.Length != 4 || values.Any(x => x < 0)) return Stop.Error.WithError("NPC resource amounts are invalid.");
            Result result;

            result = await InputAmount(browser, values);
            if (result.IsFailed) return result;

            result = await browser.Click(NpcResourceParser.GetDistributeButton(browser.CurrentPage));
            if (result.IsFailed) return result;

            // Distribute is a preview. Only Redeem commits the paid exchange.
            if (!enabled() || await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) != villageId)
                return Skip.Error.WithError("NPC was disabled or the selected village changed before the exchange.");
            var redeem = NpcResourceParser.GetRedeemButton(browser.CurrentPage);
            if (await redeem.CountAsync() != 1 || !await redeem.IsEnabledAsync() || await redeem.EvaluateAsync<bool>("n => n.classList.contains('disabled')"))
                return Retry.Error.WithError("NPC exchange confirmation is not ready.");
            var goldBefore = await ReadGoldAsync(browser.CurrentPage);
            if (goldBefore is null || goldBefore < 3) return Retry.Error.WithError("NPC gold availability could not be verified; no paid exchange was attempted.");
            token.ThrowIfCancellationRequested();
            if (!enabled()) return Skip.Error.WithError("NPC was disabled before the paid confirmation.");
            try
            {
                logger.Information("NPC stage=confirmation_attempt village={VillageId}; paid exchange outcome pending", villageId.Value);
                result = await browser.Click(redeem);
                if (result.IsFailed) return Uncertain();
                var until = DateTimeOffset.UtcNow + (completionTimeout ?? TimeSpan.FromSeconds(5));
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (await browser.CurrentPage.Locator(".dialogOverlay:visible:has(#npc)").CountAsync() == 0 &&
                        await VillagePanelParser.GetCurrentVillageId(browser.CurrentPage) == villageId &&
                        await ReadGoldAsync(browser.CurrentPage) == goldBefore - 3) return Result.Ok();
                    await Task.Delay(100, token);
                } while (DateTimeOffset.UtcNow < until);
            }
            catch (Exception ex) { logger.Warning("NPC stage=confirmation_uncertain village={VillageId} errorType={ErrorType}", villageId.Value, ex.GetType().Name); }
            return Uncertain();

            static Result Uncertain() => Result.Fail(new UncertainTaskOutcome("NPC exchange outcome is uncertain. Inspect the game before retrying this task."));
        }

        private static async Task<long?> ReadGoldAsync(IPage page)
        {
            var gold = page.Locator("div.ajaxReplaceableGoldAmount");
            return await gold.CountAsync() == 1 && HeroResourceTransferParser.TryAmount(await gold.InnerTextAsync(), out var value) ? value : null;
        }

        private static async ValueTask<Result> InputAmount(IChromeBrowser browser, long[] values)
        {
            var inputs = NpcResourceParser.GetInputs(browser.CurrentPage);

            var inputCount = await inputs.CountAsync();

            if (inputCount != 4)
            {
                return Stop.Error.WithError($"Expected 4 input elements, but found {inputCount}.");
            }

            for (var i = 0; i < 4; i++)
            {
                var inputElement = inputs.Nth(i);
                var result = await browser.Input(inputElement, $"{values[i]}");
                if (result.IsFailed) return result;
            }

            return Result.Ok();
        }
    }
}
