using System.Globalization;
using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

// Contract from the observed English rally-point send form. No submission occurs here.
public static partial class TroopSendAdapter
{
    public sealed record Prepared(long[] Amounts);

    public static async Task<bool> MatchesAsync(IPage page, int x, int y, IReadOnlyList<long> amounts)
    {
        if (amounts.Count != 11) return false;
        var xField = page.Locator("#xCoordInput");
        var yField = page.Locator("#yCoordInput");
        var reinforce = page.Locator("input[name='eventType'][value='5']");
        if (await xField.CountAsync() != 1 || await yField.CountAsync() != 1 || await reinforce.CountAsync() != 1 ||
            await xField.InputValueAsync() != x.ToString(CultureInfo.InvariantCulture) || await yField.InputValueAsync() != y.ToString(CultureInfo.InvariantCulture) || !await reinforce.IsCheckedAsync()) return false;
        for (var i = 1; i <= 11; i++)
        {
            var field = page.Locator($"input[name='troop[t{i}]']");
            if (await field.CountAsync() == 0 && i == 11 && amounts[i - 1] == 0) continue;
            if (await field.CountAsync() != 1) return false;
            if (!await field.IsEnabledAsync()) { if (amounts[i - 1] != 0) return false; continue; }
            if (!long.TryParse(await field.InputValueAsync(), out var value) || value != amounts[i - 1]) return false;
        }
        var redeploy = page.Locator("input[name='redeployHero']");
        if (await redeploy.CountAsync() > 1 || await redeploy.CountAsync() == 1 && await redeploy.IsCheckedAsync()) return false;
        return true;
    }

    public static async Task<Prepared?> PrepareAsync(IPage page, int x, int y)
    {
        var inputs = page.Locator("input[name^='troop[']");
        var count = await inputs.CountAsync();
        if (count is < 10 or > 11) return null;
        var fields = new Dictionary<int, ILocator>();
        var amounts = new long[11];
        for (var i = 0; i < count; i++)
        {
            var input = inputs.Nth(i);
            var match = NameRegex().Match(await input.GetAttributeAsync("name") ?? "");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index is < 1 or > 11 || !fields.TryAdd(index, input)) return null;
            var cell = input.Locator("xpath=ancestor::td[1]");
            if (await cell.CountAsync() != 1) return null;
            if (!await input.IsEnabledAsync()) { amounts[index - 1] = 0; continue; }
            var anchor = cell.Locator("a");
            if (await anchor.CountAsync() != 1 || !TryCount(await anchor.InnerTextAsync(), out amounts[index - 1])) return null;
        }
        if (Enumerable.Range(1, 10).Any(i => !fields.ContainsKey(i)) || amounts.All(x => x == 0)) return null;
        var xField = page.Locator("#xCoordInput");
        var yField = page.Locator("#yCoordInput");
        var reinforce = page.Locator("input[name='eventType'][value='5']");
        var send = page.Locator("button#ok[type=submit]");
        if (await xField.CountAsync() != 1 || await yField.CountAsync() != 1 || await reinforce.CountAsync() != 1 || await send.CountAsync() != 1) return null;
        var redeploy = page.Locator("input[name='redeployHero']");
        if (await redeploy.CountAsync() > 1) return null;
        if (await redeploy.CountAsync() == 1 && await redeploy.IsCheckedAsync()) await redeploy.UncheckAsync();
        await xField.FillAsync(x.ToString(CultureInfo.InvariantCulture));
        await yField.FillAsync(y.ToString(CultureInfo.InvariantCulture));
        await reinforce.CheckAsync();
        foreach (var (index, field) in fields.OrderBy(p => p.Key))
            if (await field.IsEnabledAsync()) await field.FillAsync(amounts[index - 1].ToString(CultureInfo.InvariantCulture));
        return await send.IsEnabledAsync() ? new(amounts) : null;
    }

    private static bool TryCount(string text, out long count)
    {
        var filtered = new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (filtered.Length == 0 || filtered.Any(c => !char.IsDigit(c) && c is not (' ' or '\u00a0' or '\u202f' or ',' or '.'))) { count = 0; return false; }
        return long.TryParse(new string(filtered.Where(char.IsDigit).ToArray()), out count);
    }

    [GeneratedRegex(@"^troop\[t(\d+)\]$")]
    private static partial Regex NameRegex();
}
