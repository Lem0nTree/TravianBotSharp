using MainCore.Services.Evasion;
using MainCore.Test.Parsers;

namespace MainCore.Test.Evasion;

public class TroopSendAdapterTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    [Fact]
    public async Task FillsObservedColumnOrderByTroopNumberAndSelectsReinforcement()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        // Synthetic markup based on the observed game form, not a captured page.
        var order = new[] { 1, 4, 7, 9, 2, 5, 8, 10, 3, 6 };
        var cells = string.Join("", order.Select(i => $"<td><input type='text' inputmode='numeric' name='troop[t{i}]'> / <a>\u202d{i * 10}\u202c</a></td>"));
        await page.SetContentAsync($"<table id='troops'><tr>{cells}</tr></table><input id='xCoordInput'><input id='yCoordInput'><input type='radio' name='eventType' value='5'><button id='ok' type='submit'>Send</button>");
        var prepared = await TroopSendAdapter.PrepareAsync(page, 12, -5);
        prepared.ShouldNotBeNull();
        prepared.Amounts.ShouldBe(new long[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 0 });
        (await TroopSendAdapter.MatchesAsync(page, 12, -5, prepared.Amounts)).ShouldBeTrue();
        (await TroopSendAdapter.MatchesAsync(page, 12, -4, prepared.Amounts)).ShouldBeFalse();
        (await page.Locator("input[name='troop[t4]']").InputValueAsync()).ShouldBe("40");
        (await page.Locator("input[name=eventType]").IsCheckedAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task MissingUnitOrAmbiguousCountFailsClosed()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<table id='troops'><tr><td><input name='troop[t1]'> / <a>10</a></td></tr></table>");
        (await TroopSendAdapter.PrepareAsync(page, 1, 2)).ShouldBeNull();
    }
}
