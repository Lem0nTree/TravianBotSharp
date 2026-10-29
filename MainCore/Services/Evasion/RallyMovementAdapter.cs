using System.Globalization;
using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

public static class RallyMovementAdapter
{
    public sealed record MatchedMovement(string Id, TimeSpan Remaining);
    public static async Task<long?> SourceTileAsync(IPage page, string sourceName)
    {
        var ids = new HashSet<long>();
        var tables = page.Locator("table.troop_details");
        for (var i = 0; i < await tables.CountAsync(); i++)
        {
            var table = tables.Nth(i);
            var classes = (await table.GetAttributeAsync("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var outgoing = classes.Any(c => c.StartsWith("out", StringComparison.OrdinalIgnoreCase));
            var home = await table.Locator("#unitRowAtTown").CountAsync() == 1;
            if (home)
            {
                var heading = table.Locator("td.troopHeadline a[href*='/profile/']");
                if (await heading.CountAsync() != 1) home = false;
                else
                {
                    var title = new string((await heading.InnerTextAsync()).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
                    home = string.Equals(title, "Own troops", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (!outgoing && !home) continue;
            var anchor = table.Locator("td.role a[href*='karte.php']");
            if (await anchor.CountAsync() != 1) continue;
            var text = new string((await anchor.InnerTextAsync()).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
            if (!string.Equals(text, sourceName, StringComparison.Ordinal)) continue;
            var id = TileId(await anchor.GetAttributeAsync("href") ?? "");
            if (id is not null) ids.Add(id.Value);
        }
        return ids.Count == 1 ? ids.Single() : null;
    }
    public static async Task<HashSet<string>> RecallIdsAsync(IPage page)
    {
        var buttons = page.Locator("table.troop_details div.abort button[onclick]");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < await buttons.CountAsync(); i++)
        {
            var id = RallyRecallAdapter.ReadId(await buttons.Nth(i).GetAttributeAsync("onclick") ?? "");
            if (id is not null) ids.Add(id);
        }
        return ids;
    }

    public static async Task<MatchedMovement?> FindUniqueNewAsync(IPage page, IReadOnlySet<string> before, long destinationTile, IReadOnlyList<long> amounts)
    {
        if (destinationTile <= 0 || amounts.Count != 11) return null;
        var tables = page.Locator("table.troop_details.outReinforcement, table.troop_details.outSupply");
        MatchedMovement? match = null;
        for (var i = 0; i < await tables.CountAsync(); i++)
        {
            var table = tables.Nth(i);
            var header = table.Locator("td.troopHeadline");
            var target = table.Locator("td.troopHeadline a[href*='karte.php']");
            var buttons = table.Locator("div.abort button[onclick]");
            var units = table.Locator("tbody.units.last td.unit");
            var timer = table.Locator(".infos .in span.timer");
            if (await header.CountAsync() != 1 || await target.CountAsync() != 1 || TileId(await target.GetAttributeAsync("href") ?? "") != destinationTile || await buttons.CountAsync() != 1 || await units.CountAsync() is < 10 or > 11 || await timer.CountAsync() != 1 ||
                !TryRemaining(await timer.InnerTextAsync(), out var seconds)) continue;
            var text = new string((await header.InnerTextAsync()).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
            if (!Regex.IsMatch(text, @"\breinforc", RegexOptions.IgnoreCase)) continue;
            var id = RallyRecallAdapter.ReadId(await buttons.First.GetAttributeAsync("onclick") ?? "");
            if (id is null || before.Contains(id)) continue;
            var parsed = new long[11];
            var valid = true;
            for (var n = 0; n < await units.CountAsync(); n++)
            {
                var raw = new string((await units.Nth(n).InnerTextAsync()).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
                if (raw is "" or "-") raw = "0";
                var digits = new string(raw.Where(char.IsDigit).ToArray());
                if (raw.Any(c => !char.IsDigit(c) && c is not (' ' or '\u00a0' or '\u202f' or ',' or '.')) || !long.TryParse(digits, out parsed[n])) { valid = false; break; }
            }
            if (!valid || !parsed.SequenceEqual(amounts)) continue;
            if (match is not null) return null;
            match = new(id, TimeSpan.FromSeconds(seconds));
        }
        return match;
    }

    private static long? TileId(string href)
    {
        var match = Regex.Match(href, @"(?:\?|&)d=(\d+)(?:&|$)");
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : null;
    }

    private static bool TryRemaining(string text, out long seconds)
    {
        seconds = 0;
        var clean = new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        var match = Regex.Match(clean, @"^(\d+):(\d{2}):(\d{2})$");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var hours) || hours > 168 ||
            !int.TryParse(match.Groups[2].Value, out var minutes) || minutes > 59 ||
            !int.TryParse(match.Groups[3].Value, out var secs) || secs > 59) return false;
        seconds = hours * 3600 + minutes * 60 + secs;
        return seconds > 0;
    }
}
