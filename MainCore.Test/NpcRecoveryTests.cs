using FluentResults;
using MainCore.Commands.Features.NPC;
using MainCore.Commands.Features.UpgradeBuilding;
using MainCore.Entities;
using MainCore.Errors;
using MainCore.Infrasturecture.Persistence;
using MainCore.Services;
using MainCore.Tasks;
using MainCore.Test.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace MainCore.Test;

public class NpcRecoveryTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    private const string Html = """
        <div id='servertime'>12:00:00</div>
        <div id='sidebarBoxVillageList'><div class='listEntry village active' data-did='1'></div></div>
        <div class='ajaxReplaceableGoldAmount'>10</div>
        <div class='dialogOverlay enabled dialogVisible'><div class='dialogWrapper dialogV1'>
          <div class='dialogCancelButton smallIconButton cancel' onclick='window.cancelled=(window.cancelled||0)+1;document.querySelector(".dialogOverlay").remove()'>x</div>
          <div class='exchangeResources'><table id='npc'><tbody><tr>
          <td class='sel'><input value='0'></td><td class='sel'><input value='0'></td><td class='sel'><input value='0'></td><td class='sel'><input value='0'></td>
          </tr></tbody></table><p id='submitText'><button class='gold' id='preview' onclick='window.previews=(window.previews||0)+1'>Distribute</button></p>
          <button id='npc_market_button' onclick='window.paid=(window.paid||0)+1'>Exchange 3 gold</button>
          <button class='dialogButtonOk' onclick='window.wrongCleanup=true'>Never cleanup with this</button>
          </div>
        </div></div>
        """;

    private static IChromeBrowser Browser(IPage page)
    {
        var browser = Substitute.For<IChromeBrowser>();
        browser.CurrentPage.Returns(page);
        browser.CurrentUrl.Returns("https://game.invalid/build.php?id=17");
        browser.IsInitialized.Returns(true);
        browser.Input(Arg.Any<ILocator>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(async call => { await call.ArgAt<ILocator>(0).FillAsync(call.ArgAt<string>(1)); return Result.Ok(); });
        browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async call => { await call.ArgAt<ILocator>(0).ClickAsync(); return Result.Ok(); });
        return browser;
    }

    [Fact]
    public async Task DisableAfterOpeningNpcPreventsPaidMutationAndCancelClosesTheActualOverlay()
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync(); await page.SetContentAsync(Html);
        var browser = Browser(page); var enabled = true;
        browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async call =>
        { var button = call.ArgAt<ILocator>(0); await button.ClickAsync(); if (await button.GetAttributeAsync("id") == "preview") enabled = false; return Result.Ok(); });
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await NPCCommand.ExecuteAsync(new(browser, new(1), [1, 2, 3, 4]), () => enabled, logger, TestContext.Current.CancellationToken);
        result.HasError<Skip>().ShouldBeTrue();
        (await page.EvaluateAsync<int>("() => window.paid || 0")).ShouldBe(0);
        (await TaskDialogRecovery.CloseNpcAsync(browser)).ShouldBeTrue();
        (await page.EvaluateAsync<int>("() => window.cancelled")).ShouldBe(1);
        (await page.EvaluateAsync<bool>("() => !!window.wrongCleanup")).ShouldBeFalse();
        await browser.DidNotReceive().Navigate(Arg.Any<string>());
    }

    [Fact]
    public async Task DisabledQueuedNpcClosesOrphanOverlayBeforeBehaviorsAndCannotReopenIt()
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync(); await page.SetContentAsync(Html);
        using var h = new TaskExecutionHarness(page);
        h.Tasks.Add(new NpcTask.Task(new(1), new(1)));
        await h.Timer.Execute(new(1));
        h.Tasks.Add(new NpcTask.Task(new(1), new(1)));
        await h.Timer.Execute(new(1));
        (await page.EvaluateAsync<int>("() => window.cancelled")).ShouldBe(1);
        (await page.EvaluateAsync<int>("() => window.paid || 0")).ShouldBe(0);
        (await page.Locator(".dialogOverlay:visible").CountAsync()).ShouldBe(0);
        await h.Npc.DidNotReceive().HandleAsync(Arg.Any<NpcTask.Task>(), Arg.Any<CancellationToken>());
        h.Tasks.GetStatus(new(1)).ShouldBe(MainCore.Enums.StatusEnums.Online);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedNpcConfirmationCannotBeRetriedAndCleanupNeverChargesAgain(bool throws)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync(); await page.SetContentAsync(Html);
        var browser = Browser(page);
        browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async call =>
        {
            var button = call.ArgAt<ILocator>(0); await button.ClickAsync();
            if (await button.GetAttributeAsync("id") != "npc_market_button") return Result.Ok();
            if (throws) throw new PlaywrightException("ambiguous response");
            return Retry.Error;
        });
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await NPCCommand.ExecuteAsync(new(browser, new(1), [1, 2, 3, 4]), () => true, logger, TestContext.Current.CancellationToken);
        result.HasError<UncertainTaskOutcome>().ShouldBeTrue(); result.HasError<Retry>().ShouldBeFalse();
        (await TaskDialogRecovery.CloseNpcAsync(browser)).ShouldBeTrue();
        (await page.EvaluateAsync<int>("() => window.paid")).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingNpcAloneDoesNotProvePaymentButVerifiedGoldChangeDoes(bool goldChanged)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync(); await page.SetContentAsync(Html);
        var script = "window.paid=(window.paid||0)+1;document.querySelector('.dialogOverlay').remove();" + (goldChanged ? "document.querySelector('.ajaxReplaceableGoldAmount').textContent='7';" : "");
        await page.Locator("#npc_market_button").EvaluateAsync("(n, script) => n.setAttribute('onclick', script)", script);
        var browser = Browser(page);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await NPCCommand.ExecuteAsync(new(browser, new(1), [1, 2, 3, 4]), () => true, logger, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(200));
        result.IsSuccess.ShouldBe(goldChanged);
        result.HasError<UncertainTaskOutcome>().ShouldBe(!goldChanged);
        (await page.EvaluateAsync<int>("() => window.paid")).ShouldBe(1);
    }

    [Theory]
    [InlineData("<button id='upgrade'>Build</button>")]
    [InlineData("<button id='upgrade' disabled>Build</button>")]
    [InlineData("<div>No button</div>")]
    public async Task UpgradePreflightDefersMissingOrDisabledButtonButHoldsAnAmbiguousClick(string html)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync(); await page.SetContentAsync(html);
        var browser = Browser(page);
        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var command = new HandleUpgradeCommand(browser, factory, logger);
        browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(Retry.Error);
        var result = await command.CommitOnceAsync(page.Locator("#upgrade"), () => Task.FromResult(Result.Ok()), TestContext.Current.CancellationToken);
        var enabled = html == "<button id='upgrade'>Build</button>";
        result.HasError<UncertainTaskOutcome>().ShouldBe(enabled);
        result.HasError<Retry>().ShouldBe(!enabled);
        if (enabled) await browser.Received(1).Click(Arg.Any<ILocator>(), Arg.Any<string?>());
        else await browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
    }
}
