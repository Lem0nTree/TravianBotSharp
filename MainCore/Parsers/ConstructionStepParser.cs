using System.Globalization;
using System.Text.RegularExpressions;
using MainCore.Commands.Features.UseHeroItem;

namespace MainCore.Parsers;

public static class ConstructionStepParser
{
    public static async Task<ConstructionStep?> CaptureAsync(IPage page, AppDbContext db, AccountId account, VillageId village, NormalBuildPlan goal)
    {
        if (!db.Villages.Any(x => x.Id == village.Value && x.AccountId == account.Value) ||
            !await UseHeroResourceCommand.IsBuildingContextAsync(page, village, goal)) return null;
        var current = db.Buildings.AsNoTracking().SingleOrDefault(x => x.VillageId == village.Value && x.Location == goal.Location);
        if (current is null || current.Type != goal.Type && current.Type != BuildingEnums.Site) return null;
        var normal = await UpgradeParser.GetNormalButton(page, goal.Type);
        if (await normal.CountAsync() != 1) return null;
        var build = page.Locator("#build");
        var empty = await build.EvaluateAsync<bool>("n => n.classList.contains('gid0')");
        var queued = db.QueueBuildings.AsNoTracking().Where(x => x.VillageId == village.Value && x.Location == goal.Location && x.Type == goal.Type)
            .Select(x => (int?)x.Level).Max() ?? 0;
        int next;
        if (empty)
        {
            if (current.Type != BuildingEnums.Site || queued != 0 || !await normal.EvaluateAsync<bool>("n => n.classList.contains('new')")) return null;
            next = 1;
        }
        else
        {
            var classes = await build.GetAttributeAsync("class") ?? "";
            var level = Regex.Match(classes, @"(?:^|\s)level(\d+)(?:\s|$)");
            if (!level.Success || !int.TryParse(level.Groups[1].Value, out var displayed) || displayed != current.Level || current.Type != goal.Type) return null;
            var fromValue = NextLevel(await normal.GetAttributeAsync("value"));
            var fromText = NextLevel(await normal.InnerTextAsync());
            if (fromValue is not null && fromText is not null && fromValue != fromText) return null;
            var stated = fromValue ?? fromText;
            if (stated is null || stated != Math.Max(current.Level, queued) + 1) return null;
            next = stated.Value;
        }
        return next <= goal.Level ? new(goal.Location, goal.Type, next) : null;
    }

    private static int? NextLevel(string? value)
    {
        var clean = new string((value ?? "").Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        var match = Regex.Match(clean, @"^Upgrade\s+to\s+level\s+(\d+)$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var level) && level is > 0 and <= 100 ? level : null;
    }
}
