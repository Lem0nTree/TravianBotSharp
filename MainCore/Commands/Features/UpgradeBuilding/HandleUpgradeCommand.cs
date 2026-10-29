using MainCore.Infrasturecture.Extensions;

namespace MainCore.Commands.Features.UpgradeBuilding
{
    [Handler]
    public sealed partial class HandleUpgradeCommand(
        IChromeBrowser browser,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger logger)
    {
        public sealed record Command(VillageId VillageId, NormalBuildPlan Plan) : IVillageCommand;

        private async ValueTask<Result> HandleAsync(Command command, CancellationToken token)
        {
            var (villageId, plan) = command;

            LogBuildingInfo(villageId, plan);

            Result result;
            if (CanUseSpecialUpgrade(villageId) && !UnskippableBuildings.Contains(plan.Type))
            {
                result = await SpecialUpgrade(plan.Type, token);
                if (result.IsFailed) return result;
            }
            else
            {
                result = await Upgrade(plan.Type, token);
                if (result.IsFailed) return result;
            }

            return Result.Ok();
        }

        private static readonly List<BuildingEnums> UnskippableBuildings =
        [
            BuildingEnums.Residence,
            BuildingEnums.Palace,
            BuildingEnums.CommandCenter,
        ];

        private void LogBuildingInfo(VillageId villageId, NormalBuildPlan plan)
        {
            using var context = contextFactory.CreateDbContext();

            var queueBuilding = context.QueueBuildings
                .Where(x => x.VillageId == villageId.Value)
                .FirstOrDefault(x => x.Location == plan.Location);

            if (queueBuilding is not null)
            {
                logger.Information("{Type} at location {Location} is in queue at level {Level}", queueBuilding.Type, queueBuilding.Location, queueBuilding.Level);
            }
            else
            {
                var building = context.Buildings
                   .Where(x => x.VillageId == villageId.Value)
                   .FirstOrDefault(x => x.Location == plan.Location);

                if (building is not null)
                {
                    logger.Information("{Type} at location {Location} is at level {Level}", building.Type, building.Location, building.Level);
                }
            }
        }

        private bool CanUseSpecialUpgrade(VillageId villageId)
        {
            using var context = contextFactory.CreateDbContext();
            var useSpecialUpgrade = context.BooleanByName(villageId, VillageSettingEnums.UseSpecialUpgrade);
            return useSpecialUpgrade;
        }

        private async Task<Result> SpecialUpgrade(BuildingEnums building, CancellationToken token)
        {
            var button = await UpgradeParser.GetSpecialButton(browser.CurrentPage, building);
            return await CommitOnceAsync(button, HandleAds, token);
        }

        private async Task<Result> HandleAds()
        {
            var page = browser.CurrentPage;
            var videoFeature = page.Locator("#videoFeature");

            var result = await browser.Wait(videoFeature);
            if (result.IsFailed) return result;

            var classess = await videoFeature.GetAttributeAsync("class") ?? "";
            if (classess.Contains("infoScreen"))
            {
                var checkBoxDontShowAgain = page.Locator("#videoFeature label.checkbox input[name=\"preference\"]");
                result = await browser.Click(checkBoxDontShowAgain);
                if (result.IsFailed) return result;

                var buttonWatchAds = page.Locator("#videoFeature div.buttonWrapper button");
                result = await browser.Click(buttonWatchAds);
                if (result.IsFailed) return result;
            }
            await Task.Delay(20_000);
            result = await browser.WaitPageChanged("dorf");
            if (result.IsFailed) return result;
            return Result.Ok();
        }

        private async Task<Result> Upgrade(BuildingEnums building, CancellationToken token)
        {
            var button = await UpgradeParser.GetNormalButton(browser.CurrentPage, building);
            return await CommitOnceAsync(button, () => browser.WaitPageChanged("dorf"), token);
        }

        public async Task<Result> CommitOnceAsync(ILocator button, Func<Task<Result>> observeCompletion, CancellationToken token = default)
        {
            if (await button.CountAsync() != 1 || !await button.IsVisibleAsync() || !await button.IsEnabledAsync() || await button.EvaluateAsync<bool>("n => n.classList.contains('disabled')"))
                return Retry.Error.WithError("The construction button is unavailable; no upgrade click was attempted.");
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await browser.Click(button);
                if (result.IsSuccess) result = await observeCompletion();
                if (result.IsSuccess) return result;
            }
            catch (Exception ex) { logger.Warning("Construction stage=outcome_uncertain errorType={ErrorType}", ex.GetType().Name); }
            return Result.Fail(new UncertainTaskOutcome("Construction may have started, but its outcome is uncertain. Inspect the game before retrying this village's build queue."));
        }
    }
}
