using MainCore.Services.Evasion;
using MainCore.Test.Parsers;

namespace MainCore.Test.Evasion;

public class RallyRecallAdapterTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    [Fact]
    public async Task MatchesExactRecallOnlyInOutgoingReinforcement()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        // Synthetic HTML; the button and recall attribute follow the observed DOM.
        await page.SetContentAsync("""
            <table class='troop_details outRaid'><tr><td><div class='abort'><button onclick="window.location.href='/build.php?gid=16&amp;tt=1&amp;recall=111';return false">x</button></div></td></tr></table>
            <table class='troop_details outReinforcement'><tr><td><div class='abort'><button onclick="window.location.href='/build.php?gid=16&amp;tt=1&amp;recall=222';return false">x</button></div></td></tr></table>
            """);
        (await RallyRecallAdapter.FindAsync(page, "111")).ShouldBeNull();
        (await RallyRecallAdapter.FindAsync(page, "222")).ShouldNotBeNull();
        (await RallyRecallAdapter.FindAsync(page, "22")).ShouldBeNull();
    }
}
