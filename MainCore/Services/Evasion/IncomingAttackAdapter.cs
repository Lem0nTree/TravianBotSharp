using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace MainCore.Services.Evasion;

public sealed record IncomingAttack(string Id, DateTimeOffset ImpactAt);

// English rally-point contract observed in the paused game. IDs are the markSymbol
// suffix; the displayed relative timer is used instead of trusting local/server clocks.
public static partial class IncomingAttackAdapter
{
    public static async Task<IReadOnlyList<IncomingAttack>?> ReadAsync(IPage page, DateTimeOffset sampledAt)
    {
        var raw = await page.EvaluateAsync<JsonElement>("""
            () => Array.from(document.querySelectorAll('table.troop_details, table:has([id^="markSymbol_"])')).map(table => {
                const markers = table.querySelectorAll('[id^="markSymbol_"]');
                return { id: markers.length === 1 ? markers[0].id : '', markerCount: markers.length,
                    text: table.innerText || '', classes: table.className || '',
                    mark: !!table.querySelector('a[title="mark attack"]') };
            })
            """);
        var rows = JsonSerializer.Deserialize<RawAttack[]>(raw.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (rows is null) return null;
        var result = new List<IncomingAttack>();
        foreach (var row in rows)
        {
            var text = new string((row.Text ?? "").Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
            var classes = (row.Classes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (classes.Any(c => c.StartsWith("out", StringComparison.OrdinalIgnoreCase) || c.Equals("inReturn", StringComparison.OrdinalIgnoreCase)) ||
                text.Contains("Return from", StringComparison.OrdinalIgnoreCase)) continue;
            var hostile = row.MarkerCount > 0 || classes.Any(c => c.Equals("inAttack", StringComparison.OrdinalIgnoreCase) || c.Equals("inRaid", StringComparison.OrdinalIgnoreCase)) || AttackRegex().IsMatch(text);
            if (!hostile) continue;
            var id = IdRegex().Match(row.Id ?? "");
            var timer = ArrivalRegex().Match(text);
            if (!row.Mark || row.MarkerCount != 1 || !id.Success || !timer.Success) return null;
            if (!long.TryParse(timer.Groups[1].Value, out var hours) ||
                !int.TryParse(timer.Groups[2].Value, out var minutes) ||
                !int.TryParse(timer.Groups[3].Value, out var seconds) || minutes > 59 || seconds > 59 || hours > 10000) return null;
            var remaining = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
            result.Add(new(id.Groups[1].Value, sampledAt + remaining));
        }
        if (result.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Count) return null;
        return result;
    }

    private sealed record RawAttack(string? Id, string? Text, string? Classes, int MarkerCount, bool Mark);
    [GeneratedRegex(@"^markSymbol_(\d+)$")]
    private static partial Regex IdRegex();
    [GeneratedRegex(@"\b(?:attacks|raids)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AttackRegex();
    [GeneratedRegex(@"\bin\s+(\d+):(\d{2}):(\d{2})\s*hrs?\.?", RegexOptions.IgnoreCase)]
    private static partial Regex ArrivalRegex();
}
