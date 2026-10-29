using System.Globalization;
using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

public sealed record TroopConfirmation(ILocator Button, TimeSpan Duration);

// This strict synthetic confirmation contract must be replaced/verified with a
// captured game confirmation. Unrecognized confirmations are never submitted.
public static class TroopConfirmationAdapter
{
    public static async Task<TroopConfirmation?> FindAsync(IPage page, int x, int y, IReadOnlyList<long> amounts)
    {
        if (amounts.Count != 11 || await page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("confirm", RegexOptions.IgnoreCase) }).CountAsync() == 0) return null;
        var forms = page.Locator("form");
        TroopConfirmation? match = null;
        for (var i = 0; i < await forms.CountAsync(); i++)
        {
            var form = forms.Nth(i);
            if (!await EqualInput(form, "x", x) || !await EqualInput(form, "y", y) || !await EqualInput(form, "eventType", 5)) continue;
            var valid = true;
            for (var unit = 1; unit <= 11; unit++)
            {
                var input = form.Locator($"input[name='troop[t{unit}]']");
                if (await input.CountAsync() == 0 && amounts[unit - 1] == 0) continue;
                if (!await EqualInput(form, $"troop[t{unit}]", amounts[unit - 1])) { valid = false; break; }
            }
            if (!valid) continue;
            var timer = form.Locator(".duration .timer");
            if (await timer.CountAsync() != 1 || !long.TryParse(await timer.GetAttributeAsync("value"), out var seconds) || seconds <= 0 || seconds > 604800) continue;
            var button = form.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Confirm|Send)$", RegexOptions.IgnoreCase) });
            if (await button.CountAsync() != 1 || !await button.IsEnabledAsync()) continue;
            if (match is not null) return null;
            match = new(button, TimeSpan.FromSeconds(seconds));
        }
        return match;
    }

    private static async Task<bool> EqualInput(ILocator form, string name, long expected)
    {
        var input = form.Locator($"input[name='{name}']");
        return await input.CountAsync() == 1 && long.TryParse(await input.InputValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual) && actual == expected;
    }
}
