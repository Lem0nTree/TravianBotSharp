using MainCore.Services.Evasion;
using MainCore.Test.Parsers;

namespace MainCore.Test.Evasion;

public class ConfirmationAndMovementTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    [Fact]
    public async Task TroopConfirmationMustMatchEveryFieldAndProvideTravelTime()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var amounts = Enumerable.Range(1, 11).Select(i => (long)i).ToArray();
        await page.SetContentAsync(TroopPreviewFixture.Markup(amounts, x: 1, y: 2));
        var confirm = await TroopConfirmationAdapter.FindAsync(page, 1, 1, 2, amounts);
        confirm.ShouldNotBeNull();
        confirm.Duration.ShouldBe(TimeSpan.FromSeconds(120));
        (await TroopConfirmationAdapter.FindAsync(page, 1, 1, 3, amounts)).ShouldBeNull();
    }

    [Fact]
    public async Task MovementMustBeNewUniqueReinforcementWithExactCountsAndTarget()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var cells = string.Join("", Enumerable.Range(1, 10).Select(i => $"<td class='unit'>{i}</td>"));
        // Synthetic outbound class and heading; only the recall button/timer structure was observed.
        await page.SetContentAsync($"<table class='troop_details outSupply'><thead><tr><td class='role'><a href='/karte.php?d=92039'>Source</a></td><td class='troopHeadline'><a href='/karte.php?d=89634'>Reinforcement for Home</a></td></tr></thead><tbody class='units last'><tr>{cells}</tr></tbody><tbody><tr><td><div class='infos'><span class='in'><span class='timer' value='100'>0:01:40</span></span></div><div class='abort'><button onclick=\"window.location.href='/build.php?recall=42'\">x</button></div></td></tr></tbody></table>");
        var amounts = Enumerable.Range(1, 10).Select(i => (long)i).Append(0).ToArray();
        (await RallyMovementAdapter.SourceTileAsync(page, "Source")).ShouldBe(92039L);
        var movement = await RallyMovementAdapter.FindUniqueNewAsync(page, new HashSet<string>(), 89634, amounts);
        movement.ShouldNotBeNull();
        movement.Id.ShouldBe("42");
        (await RallyMovementAdapter.FindUniqueNewAsync(page, new HashSet<string> { "42" }, 89634, amounts)).ShouldBeNull();
        (await RallyMovementAdapter.FindUniqueNewAsync(page, new HashSet<string>(), 89635, amounts)).ShouldBeNull();
    }

    [Fact]
    public async Task SourceTileCanBeVerifiedFromHomeTroopsWithoutAnyOutgoingMovements()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("""
            <table class='troop_details'><thead><tr><td class='role'><a href='/karte.php?d=92039'>Source</a></td><td class='troopHeadline'><a href='/profile/355'>Own troops</a></td></tr></thead><tbody><tr id='unitRowAtTown'><td>58</td></tr></tbody></table>
            <table class='troop_details inAttack'><tr><td class='role'><a href='/karte.php?d=12345'>Attacker</a></td></tr></table>
            """);
        (await RallyMovementAdapter.SourceTileAsync(page, "Source")).ShouldBe(92039L);
        (await RallyMovementAdapter.SourceTileAsync(page, "Attacker")).ShouldBeNull();
    }
}
