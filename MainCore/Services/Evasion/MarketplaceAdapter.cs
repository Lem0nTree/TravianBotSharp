using System.Globalization;
using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

// Contract based on the observed English marketplace screen; unrecognized layouts fail closed.
public static partial class MarketplaceAdapter
{
    public sealed record Prepared(long[] Amounts, long Capacity);

    public static async Task<bool> MatchesAsync(IPage page, int x, int y, IReadOnlyList<long> amounts)
    {
        if (amounts.Count != 4) return false;
        var root = page.Locator("#marketplaceSendResources");
        if (await root.CountAsync() != 1) return false;
        var xField = root.GetByLabel("X", new() { Exact = true });
        var yField = root.GetByLabel("Y", new() { Exact = true });
        if (await xField.CountAsync() != 1 || await yField.CountAsync() != 1 ||
            await xField.InputValueAsync() != x.ToString(CultureInfo.InvariantCulture) || await yField.InputValueAsync() != y.ToString(CultureInfo.InvariantCulture)) return false;
        var once = root.Locator("label.radio:has(.deliveries_1x) input[name='deliveries'][type='radio']");
        if (await once.CountAsync() != 1 || !await once.IsCheckedAsync()) return false;
        var names = new[] { "lumber", "clay", "iron", "crop" };
        for (var i = 0; i < names.Length; i++)
        {
            var field = root.Locator($"input[name='{names[i]}']");
            if (await field.CountAsync() != 1 || !long.TryParse(await field.InputValueAsync(), out var value) || value != amounts[i]) return false;
        }
        var send = root.GetByRole(AriaRole.Button, new() { Name = "Send resources", Exact = true });
        return await send.CountAsync() == 1 && await send.IsEnabledAsync() && !await send.EvaluateAsync<bool>("node => node.classList.contains('disabled')");
    }

    public static async Task<Prepared?> PrepareAsync(IPage page, int x, int y)
    {
        var root = page.Locator("#marketplaceSendResources");
        if (await root.CountAsync() != 1) return null;
        var text = new string((await root.InnerTextAsync()).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
        var merchants = MerchantsRegex().Match(text);
        var capacity = CapacityRegex().Match(text);
        if (!merchants.Success || !capacity.Success ||
            !long.TryParse(merchants.Groups[1].Value, out var available) ||
            !TryNumber(capacity.Groups[1].Value, out var each) || available < 0 || each <= 0) return null;
        long total;
        try { total = checked(available * each); } catch (OverflowException) { return null; }
        var xField = root.GetByLabel("X", new() { Exact = true });
        var yField = root.GetByLabel("Y", new() { Exact = true });
        if (await xField.CountAsync() != 1 || await yField.CountAsync() != 1) return null;
        var resourceInputs = new List<ILocator>();
        var stocks = new long[4];
        foreach (var name in new[] { "lumber", "clay", "iron", "crop" })
        {
            var input = root.Locator($"input[name='{name}']");
            if (await input.CountAsync() != 1 || !await input.IsVisibleAsync() || !await input.IsEnabledAsync()) return null;
            var stock = input.Locator("xpath=ancestor::*[contains(concat(' ', normalize-space(@class), ' '), ' stock ')][1]").Locator(".denominator");
            if (await stock.CountAsync() != 1 || !TryNumber(await stock.InnerTextAsync(), out stocks[resourceInputs.Count])) return null;
            resourceInputs.Add(input);
        }
        var send = root.GetByRole(AriaRole.Button, new() { Name = "Send resources", Exact = true });
        var once = root.Locator("label.radio:has(.deliveries_1x) input[name='deliveries'][type='radio']");
        if (await send.CountAsync() != 1 || await once.CountAsync() != 1) return null;
        var amounts = EvasionRules.Allocate(stocks, total);
        if (amounts.All(a => a == 0)) return null;
        await xField.FillAsync(x.ToString(CultureInfo.InvariantCulture));
        await yField.FillAsync(y.ToString(CultureInfo.InvariantCulture));
        await once.CheckAsync();
        for (var i = 0; i < 4; i++) await resourceInputs[i].FillAsync(amounts[i].ToString(CultureInfo.InvariantCulture));
        if (!await send.IsEnabledAsync() || await send.EvaluateAsync<bool>("node => node.classList.contains('disabled')")) return null;
        return new(amounts, total);
    }

    private static bool TryNumber(string text, out long number)
    {
        var trimmed = new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => !char.IsDigit(c) && c is not (' ' or '\u00a0' or '\u202f' or ',' or '.'))) { number = 0; return false; }
        return long.TryParse(new string(trimmed.Where(char.IsDigit).ToArray()), out number);
    }

    [GeneratedRegex(@"Merchants:\s*(\d+)\s*/\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex MerchantsRegex();
    [GeneratedRegex(@"Capacity per merchant:\s*([0-9][0-9 ,\.\u00a0\u202f]*)", RegexOptions.IgnoreCase)]
    private static partial Regex CapacityRegex();
}
