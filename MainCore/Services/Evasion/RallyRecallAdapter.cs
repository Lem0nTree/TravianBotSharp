using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

public static partial class RallyRecallAdapter
{
    // Returns a control only for the exact recorded outgoing reinforcement. Attacks,
    // raids, returns, multiple matches and already-expired controls are rejected.
    public static async Task<ILocator?> FindAsync(IPage page, string movementId)
    {
        if (!DigitsRegex().IsMatch(movementId)) return null;
        var tables = page.Locator("table.troop_details.outReinforcement, table.troop_details.outSupply");
        ILocator? match = null;
        for (var i = 0; i < await tables.CountAsync(); i++)
        {
            var buttons = tables.Nth(i).Locator("div.abort button[onclick]");
            if (await buttons.CountAsync() != 1) continue;
            var button = buttons.First;
            var onclick = await button.GetAttributeAsync("onclick") ?? "";
            if (ReadId(onclick) != movementId || !await button.IsVisibleAsync() || !await button.IsEnabledAsync()) continue;
            if (match is not null) return null;
            match = button;
        }
        return match;
    }

    public static string? ReadId(string onclick)
    {
        var recall = RecallRegex().Match(onclick);
        return recall.Success ? recall.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsRegex();
    [GeneratedRegex(@"[?&]recall=(\d+)(?=[&'\""\s;]|$)")]
    private static partial Regex RecallRegex();
}
