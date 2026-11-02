using System.Net;

namespace MainCore.Parsers
{
    public static class TrainTroopParser
    {
        public sealed record Availability(bool Available, int Maximum, string Reason);

        private static ILocator Row(IPage page, TroopEnums troop) =>
            page.Locator($"div.troop:not(.empty):visible:has(img.unit.u{(int)troop})");

        public static ILocator GetInputBox(IPage page, TroopEnums troop) => Row(page, troop).Locator("div.cta input.text");

        public static async Task<Availability> ReadAvailability(IPage page, TroopEnums troop)
        {
            var row = Row(page, troop);
            if (await row.CountAsync() != 1) return new(false, 0, "Selected troop is unavailable or its training row is ambiguous; check research and building requirements.");
            var input = GetInputBox(page, troop);
            var maximum = row.Locator("div.cta a");
            if (await input.CountAsync() != 1 || await maximum.CountAsync() != 1)
                return new(false, 0, "Selected troop has no unambiguous training controls; check research and building requirements.");
            if (!await input.IsVisibleAsync() || !await input.IsEnabledAsync())
                return new(false, 0, "Selected troop training input is disabled or hidden.");
            // Count first: an absent/unresearched unit must never wait for the
            // default locator timeout. Bound a DOM-change race as well.
            var text = WebUtility.HtmlDecode(await maximum.InnerTextAsync(new() { Timeout = 2000 }));
            if (!HeroResourceTransferParser.TryAmount(text, out var amount) || amount > int.MaxValue)
                return new(false, 0, "Selected troop's available training amount could not be read safely.");
            var submit = GetTrainButton(page);
            if (await submit.CountAsync() != 1 || !await submit.IsVisibleAsync() || !await submit.IsEnabledAsync())
                return new(false, 0, "Training button is unavailable or disabled.");
            return new(true, (int)amount, "available");
        }

        public static async Task<int> GetMaxAmount(IPage page, TroopEnums troop) =>
            (await ReadAvailability(page, troop)).Maximum;

        public static ILocator GetTrainButton(IPage page) => page.Locator("#s1");
    }
}
