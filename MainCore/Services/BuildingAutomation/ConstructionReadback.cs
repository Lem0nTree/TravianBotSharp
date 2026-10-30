namespace MainCore.Services.BuildingAutomation;

public static class ConstructionReadback
{
    // Read-only verification: a field must be read on dorf1, a building on dorf2.
    // A wrong redirect view must not leave the submitted slot stale in the DB.
    public static async Task<bool> ObserveAsync(IChromeBrowser browser, VillageId village, ConstructionStep step,
        Func<Task<Result>> update, Func<bool> exactQueueObserved, CancellationToken token)
    {
        var dorf = step.Slot <= 18 ? 1 : 2;
        if (!Uri.TryCreate(browser.CurrentUrl, UriKind.Absolute, out var current) || current.Scheme is not ("https" or "http")) return false;
        var address = new Uri(current, $"/dorf{dorf}.php").ToString();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var node = VillagePanelParser.GetVillageNode(browser.CurrentPage, village);
            if (await node.CountAsync() != 1 || !await VillagePanelParser.IsActive(node)) return false;
            try { await browser.CurrentPage.GotoAsync(address, new() { Timeout = 3000 }); }
            catch (TimeoutException) { if (attempt == 2) return false; continue; }
            node = VillagePanelParser.GetVillageNode(browser.CurrentPage, village);
            if (await node.CountAsync() != 1 || !await VillagePanelParser.IsActive(node)) return false;
            var layout = dorf == 1 ? await BuildingLayoutParser.GetFields(browser.CurrentPage) : await BuildingLayoutParser.GetInfrastructures(browser.CurrentPage);
            if (CompleteView(layout, dorf))
            {
                var target = layout.SingleOrDefault(x => x.Location == step.Slot && x.Type == step.Type);
                var queue = await BuildingLayoutParser.GetQueueBuilding(browser.CurrentPage);
                var matchingFreshQueue = queue.Any(x => BuildingNameParser.TryParse(x.Type, out var type) && type == step.Type && x.Level >= step.Level && x.CompleteTime > DateTime.Now);
                var result = await update();
                // The old updater may promote a cached level from an expired timer;
                // direct fresh layout evidence must qualify any database proof.
                if (result.IsSuccess && target is not null && (target.Level >= step.Level ||
                    target.IsUnderConstruction && matchingFreshQueue && exactQueueObserved())) return true;
            }
            if (attempt < 2) await Task.Delay(250, token);
        }
        return false;
    }

    private static bool CompleteView(IReadOnlyCollection<BuildingDto> layout, int dorf) =>
        layout.Select(x => x.Location).Distinct().Count() == layout.Count &&
        (dorf == 1 ? layout.Count == 18 && layout.All(x => x.Location is >= 1 and <= 18 && x.Type.IsResourceField() && x.Level >= 0)
            : layout.Count >= 20 && layout.All(x => x.Location is >= 19 and <= 40 && x.Type != BuildingEnums.Unknown));
}
