using System.Globalization;
using System.Text.RegularExpressions;

namespace MainCore.Parsers;

public sealed record HeroResourceSnapshot(bool Readable, long[] Available, string Reason);

public static class HeroResourceTransferParser
{
    public static readonly string[] Names = ["lumber", "clay", "iron", "crop"];
    private static readonly HeroItemEnums[] Items = [HeroItemEnums.Wood, HeroItemEnums.Clay, HeroItemEnums.Iron, HeroItemEnums.Crop];

    public static async Task<HeroResourceSnapshot> WaitUntilReadableAsync(IPage page, CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(2);
        HeroResourceSnapshot snapshot;
        do
        {
            snapshot = await ReadAsync(page);
            if (snapshot.Readable) return snapshot;
            await Task.Delay(100, token);
        } while (DateTimeOffset.UtcNow < until);
        return snapshot;
    }

    public static async Task<HeroResourceSnapshot> ReadAsync(IPage page)
    {
        var dialogs = page.Locator("div.resourceTransferDialog:visible");
        if (await dialogs.CountAsync() != 1) return new(false, [], "dialog-missing-or-ambiguous");
        var available = new long[4];
        var dialog = dialogs.First;
        for (var i = 0; i < Names.Length; i++)
        {
            var rows = dialog.Locator($".resourceRow:visible:has(input[name='{Names[i]}'])");
            if (await rows.CountAsync() != 1) return new(false, [], $"{Names[i]}-row-missing-or-ambiguous");
            var row = rows.First;
            var field = row.Locator($"input[name='{Names[i]}']");
            var item = row.Locator($".heroItem:visible:has(.item.item{(int)Items[i]})");
            if (await field.CountAsync() != 1 || await item.CountAsync() != 1)
                return new(false, [], $"{Names[i]}-identity-unverified");
            var count = item.Locator(".count:visible");
            if (await count.CountAsync() != 1 || !TryAmount(await count.InnerTextAsync(), out available[i]))
                return new(false, [], $"{Names[i]}-amount-unreadable");
        }
        return new(true, available, "verified");
    }

    // Inventory/dialog counts are exact quantities, not rounded k/m abbreviations.
    public static bool TryAmount(string? text, out long value)
    {
        value = 0;
        var clean = new string((text ?? "").Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (!Regex.IsMatch(clean, @"^(?:[0-9]+|[0-9]{1,3}(?<sep>[ ,.\u00a0\u202f])[0-9]{3}(?:\k<sep>[0-9]{3})*)$")) return false;
        var digits = new string(clean.Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
