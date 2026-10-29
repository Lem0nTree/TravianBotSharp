using MainCore.Services.Evasion;
using MainCore.Test.Parsers;

namespace MainCore.Test.Evasion;

public class MarketplaceAdapterTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    // Synthetic fixture: mirrors the observed accessibility contract, not a saved game page.
    private const string Html = """
        <div id='marketplaceSendResources'>
          <div>Merchants: &#8237;2&#8236;/8</div><div>Capacity per merchant: &#8237;30&#8236;</div>
          <label>Village name<input type='text'></label>
          <label>X<input type='number'></label><label>Y<input type='number'></label>
          <div class='stock'><input type='number' name='lumber' value='0'><span class='denominator'>40</span></div>
          <div class='stock'><input type='number' name='clay' value='0'><span class='denominator'>50</span></div>
          <div class='stock'><input type='number' name='iron' value='0'><span class='denominator'>60</span></div>
          <div class='stock'><input type='number' name='crop' value='0'><span class='denominator'>70</span></div>
          <label class='radio'><input type='radio' name='deliveries'><i class='deliveries_1x'></i>1×</label>
          <button>Send resources</button>
        </div>
        """;

    [Fact]
    public async Task PreparesStrictPriorityFromDisplayedCapacityWithoutSending()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(Html);
        var prepared = await MarketplaceAdapter.PrepareAsync(page, 12, -5);
        prepared.ShouldNotBeNull();
        prepared.Capacity.ShouldBe(60);
        prepared.Amounts.ShouldBe([40, 20, 0, 0]);
        (await MarketplaceAdapter.MatchesAsync(page, 12, -5, prepared.Amounts)).ShouldBeTrue();
        (await MarketplaceAdapter.MatchesAsync(page, 12, -4, prepared.Amounts)).ShouldBeFalse();
        (await page.GetByLabel("X").InputValueAsync()).ShouldBe("12");
        (await page.GetByLabel("Y").InputValueAsync()).ShouldBe("-5");
        (await page.Locator("#marketplaceSendResources input[type=number]").Nth(2).InputValueAsync()).ShouldBe("40");
    }

    [Fact]
    public async Task RefusesUnknownCapacityOrUnexpectedInputCount()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(Html.Replace("Capacity per merchant: &#8237;30&#8236;", "Capacity unknown"));
        (await MarketplaceAdapter.PrepareAsync(page, 1, 2)).ShouldBeNull();
        await page.SetContentAsync(Html.Replace("name='crop'", "name='lumber'"));
        (await MarketplaceAdapter.PrepareAsync(page, 1, 2)).ShouldBeNull();
    }
}
