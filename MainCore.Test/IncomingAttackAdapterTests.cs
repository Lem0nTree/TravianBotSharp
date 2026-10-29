using MainCore.Services.Evasion;
using MainCore.Test.Parsers;

namespace MainCore.Test.Evasion;

public class IncomingAttackAdapterTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    [Fact]
    public async Task ReadsObservedAttackIdentityAndTimerExcludingReturns()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        // Synthetic fixture based on accessibility details observed in the paused browser.
        await page.SetContentAsync("""
            <h2>Incoming troops (4)</h2>
            <table class='troop_details inAttack'>
            <tr><td><a title='mark attack'><img id='markSymbol_1705985'></a>Principle attacks ChatGPT</td></tr>
            <tr><th>Arrival</th><td>in 0:00:28 hrs. at 11:36:57</td></tr></table>
            <table class='troop_details inReturn'><tr><td>Return from Other</td></tr></table>
            <table class='troop_details outRaid'><tr><td>Me raids Other</td></tr></table>
            """);
        var now = DateTimeOffset.Parse("2026-01-01T11:36:29Z");
        var attacks = await IncomingAttackAdapter.ReadAsync(page, now);
        attacks.ShouldNotBeNull();
        attacks.Count.ShouldBe(1);
        attacks[0].Id.ShouldBe("1705985");
        attacks[0].ImpactAt.ShouldBe(now.AddSeconds(28));
    }

    [Fact]
    public async Task ParsesSeparateHostileWavesAndRejectsAnUnmarkedHostileTable()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        const string rows = """
            <h2>Incoming troops (4)</h2>
            <table class='troop_details inAttack'><tr><td><a title='mark attack'><img id='markSymbol_101'></a>A attacks B</td><td>in 0:00:28 hrs.</td></tr></table>
            <table class='troop_details inRaid'><tr><td><a title='mark attack'><img id='markSymbol_102'></a>C raids B</td><td>in 0:00:35 hrs.</td></tr></table>
            """;
        await page.SetContentAsync(rows);
        var attacks = await IncomingAttackAdapter.ReadAsync(page, DateTimeOffset.UtcNow);
        attacks.ShouldNotBeNull();
        attacks.Select(x => x.Id).ShouldBe(["101", "102"]);
        await page.SetContentAsync(rows + "<table class='troop_details inAttack'><tr><td>Unknown attacks B</td><td>in 0:00:40 hrs.</td></tr></table>");
        (await IncomingAttackAdapter.ReadAsync(page, DateTimeOffset.UtcNow)).ShouldBeNull();
    }

    [Fact]
    public async Task RefusesIncompleteMarkedAttack()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<table><tr><td>Incoming troops <a title='mark attack'><img id='markSymbol_123'></a> attacks</td></tr></table>");
        (await IncomingAttackAdapter.ReadAsync(page, DateTimeOffset.UtcNow)).ShouldBeNull();
    }
}
