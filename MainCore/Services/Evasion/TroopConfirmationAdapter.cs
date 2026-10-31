using System.Globalization;
using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

public sealed record TroopConfirmation(ILocator Button, TimeSpan Duration);

// Contract observed on the English rally-point reinforcement preview. The first
// Send opens this form; only confirmSendTroops submits the actual movement.
public static class TroopConfirmationAdapter
{
    public sealed record Readiness(TroopConfirmation? Confirmation, string Reason);

    public static async Task<Readiness> WaitAsync(IPage page, int source, int x, int y, IReadOnlyList<long> amounts,
        DateTimeOffset deadline, CancellationToken token)
    {
        var last = "preview-not-ready";
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var read = await ReadAsync(page, source, x, y, amounts);
                if (read.Confirmation is not null) return read;
                last = read.Reason;
            }
            catch (PlaywrightException ex) when (TransientNavigation(ex)) { last = "preview-navigation-in-progress"; }
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), token);
        }
        return new(null, last + "-before-deadline");
    }

    public static async Task<TroopConfirmation?> FindAsync(IPage page, int source, int x, int y, IReadOnlyList<long> amounts) =>
        (await ReadAsync(page, source, x, y, amounts)).Confirmation;

    public static async Task<Readiness> ReadAsync(IPage page, int source, int x, int y, IReadOnlyList<long> amounts)
    {
        Readiness Invalid(string reason) => new(null, reason);
        if (source <= 0 || amounts.Count != 11 || amounts.Any(n => n < 0) || amounts.All(n => n == 0)) return Invalid("invalid-expected-troops");
        var form = page.Locator("form#troopSendForm");
        if (await form.CountAsync() != 1 || !await form.IsVisibleAsync()) return Invalid("preview-form-not-ready-or-ambiguous");
        if (!string.Equals(await form.GetAttributeAsync("method"), "post", StringComparison.OrdinalIgnoreCase)) return Invalid("preview-method-unrecognized");
        if (!await EqualInput(form, "x", x) || !await EqualInput(form, "y", y)) return Invalid("preview-coordinates-mismatch");
        if (!await EqualInput(form, "eventType", 5)) return Invalid("preview-not-reinforcement");
        if (!await EqualInput(form, "troops[0][villageId]", source)) return Invalid("preview-source-village-mismatch");
        // A second wave or an additional unrecognized troop parameter cannot be sent.
        var inputs = form.Locator("input[name^='troops[']");
        var names = await inputs.EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.name)");
        var neutralTargets = new[] { "troops[0][scoutTarget]", "troops[0][catapultTarget1]", "troops[0][catapultTarget2]" };
        var allowed = Enumerable.Range(1, 11).Select(i => $"troops[0][t{i}]").Append("troops[0][villageId]").Concat(neutralTargets).ToHashSet(StringComparer.Ordinal);
        if (names.Length != allowed.Count || !names.ToHashSet(StringComparer.Ordinal).SetEquals(allowed)) return Invalid("preview-extra-or-missing-troop-fields");
        foreach (var name in neutralTargets)
        {
            var input = form.Locator($"input[name='{name}']");
            if (await input.CountAsync() != 1 || !string.IsNullOrEmpty(await input.InputValueAsync())) return Invalid("preview-nonneutral-scout-or-catapult-target");
        }
        var redeploy = form.Locator("input[name='redeployHero']");
        if (await redeploy.CountAsync() > 1 || await redeploy.CountAsync() == 1 && !string.IsNullOrEmpty(await redeploy.InputValueAsync())) return Invalid("preview-hero-redeployment-unrecognized");
        var target = form.Locator("#short_info a[href*='karte.php']");
        if (await target.CountAsync() != 1 || !TargetCoordinates(await target.GetAttributeAsync("href"), x, y)) return Invalid("preview-target-link-mismatch");
        var table = form.Locator("table.troop_details");
        var heading = table.Locator(".troopHeadline");
        if (await table.CountAsync() != 1 || await heading.CountAsync() != 1 ||
            !Regex.IsMatch(Clean(await heading.InnerTextAsync()), @"^Reinforcement\s+for\s+\S", RegexOptions.IgnoreCase)) return Invalid("preview-reinforcement-heading-unrecognized");
        var units = table.Locator("tbody.units td.unit");
        if (await units.CountAsync() != 11) return Invalid("preview-visible-troop-count-unrecognized");
        for (var unit = 1; unit <= 11; unit++)
            if (!await EqualInput(form, $"troops[0][t{unit}]", amounts[unit - 1]) ||
                !TryCount(await units.Nth(unit - 1).InnerTextAsync(), out var visible) || visible != amounts[unit - 1]) return Invalid("preview-hidden-or-visible-troops-mismatch");
        var duration = form.Locator("#in.in");
        if (await duration.CountAsync() != 1 || !TryDuration(await duration.InnerTextAsync(), out var seconds)) return Invalid("preview-travel-duration-unrecognized");
        var button = form.Locator("button#confirmSendTroops[name='confirmSendTroops']");
        if (await button.CountAsync() != 1 || !await button.IsVisibleAsync() || !await button.IsEnabledAsync() ||
            await button.EvaluateAsync<bool>("n => n.classList.contains('disabled')") || !string.Equals(Clean(await button.InnerTextAsync()), "Confirm", StringComparison.OrdinalIgnoreCase)) return Invalid("preview-confirm-control-not-ready");
        return new(new(button, TimeSpan.FromSeconds(seconds)), "matching-confirmation-ready");
    }

    private static async Task<bool> EqualInput(ILocator form, string name, long expected)
    {
        var input = form.Locator($"input[name='{name}']");
        return await input.CountAsync() == 1 && long.TryParse(Clean(await input.InputValueAsync()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual) && actual == expected;
    }

    private static bool TargetCoordinates(string? href, int x, int y)
    {
        if (!Uri.TryCreate(new Uri("https://preview.invalid"), href, out var uri) || uri.AbsolutePath != "/karte.php") return false;
        var parameters = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToArray();
        bool Match(string key, int expected) => parameters.Count(p => p[0] == key) == 1 && int.TryParse(parameters.Single(p => p[0] == key)[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n == expected;
        return Match("x", x) && Match("y", y);
    }

    private static bool TryCount(string text, out long amount)
    {
        var clean = Clean(text);
        if (clean == "-") clean = "0";
        amount = 0;
        return clean.Length > 0 && clean.All(c => char.IsDigit(c) || c is ' ' or '\u00a0' or '\u202f' or ',' or '.') &&
            long.TryParse(new string(clean.Where(char.IsDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out amount);
    }

    private static bool TryDuration(string text, out long seconds)
    {
        seconds = 0;
        var match = Regex.Match(Clean(text), @"^In\s+(\d+):(\d{2}):(\d{2})\s+hours?\.?$", RegexOptions.IgnoreCase);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var hours) || hours > 168 ||
            !int.TryParse(match.Groups[2].Value, out var minutes) || minutes > 59 || !int.TryParse(match.Groups[3].Value, out var secs) || secs > 59) return false;
        seconds = hours * 3600 + minutes * 60 + secs;
        return seconds is > 0 and <= 604800;
    }

    private static string Clean(string text) => new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
    private static bool TransientNavigation(PlaywrightException ex) => ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("navigation", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase);
}
