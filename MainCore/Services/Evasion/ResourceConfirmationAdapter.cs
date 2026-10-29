using System.Globalization;
using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

// Only a page explicitly presenting confirmation and the entire submitted payload
// can yield a final control. The contract is synthetic pending a captured confirmation.
public static class ResourceConfirmationAdapter
{
    public static async Task<ILocator?> FindAsync(IPage page, int x, int y, IReadOnlyList<long> amounts)
    {
        if (amounts.Count != 4) return null;
        var heading = page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("confirm", RegexOptions.IgnoreCase) });
        if (await heading.CountAsync() == 0) return null;
        var forms = page.Locator("form");
        ILocator? match = null;
        for (var n = 0; n < await forms.CountAsync(); n++)
        {
            var form = forms.Nth(n);
            var values = new (string name, long expected)[] { ("x", x), ("y", y), ("lumber", amounts[0]), ("clay", amounts[1]), ("iron", amounts[2]), ("crop", amounts[3]) };
            var valid = true;
            foreach (var (name, expected) in values)
            {
                var input = form.Locator($"input[name='{name}']");
                if (await input.CountAsync() != 1 || !long.TryParse(await input.InputValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != expected)
                { valid = false; break; }
            }
            if (!valid) continue;
            var button = form.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Confirm|Send resources)$", RegexOptions.IgnoreCase) });
            if (await button.CountAsync() != 1 || !await button.IsEnabledAsync()) continue;
            if (match is not null) return null;
            match = button;
        }
        return match;
    }
}
