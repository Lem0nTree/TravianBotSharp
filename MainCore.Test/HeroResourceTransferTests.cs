using MainCore.Commands.Features.UseHeroItem;
using MainCore.Enums;
using MainCore.Errors;
using MainCore.Models;
using MainCore.Parsers;
using MainCore.Services;
using MainCore.Test.Parsers;
using Microsoft.Playwright;

namespace MainCore.Test;

public class HeroResourceTransferTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    private static readonly string[] Names = ["lumber", "clay", "iron", "crop"];
    private static readonly NormalBuildPlan Plan = new() { Type = BuildingEnums.IronMine, Location = 3, Level = 5 };
    private static string Rows(long[]? stock = null) => string.Join("", new[] { 2, 0, 3, 1 }.Select(i =>
        $"<div class='resourceRow'><div class='resourceRowBody'><div class='heroItem notClickable consumable inventory'><div class='item item{145 + i}'></div><span class='count'>{(stock ?? [73, 1136, 1657, 1657])[i]}</span></div><input name='{Names[i]}' value='0'></div></div>"));
    private static string Page(string rows, string confirmScript = "") => $$"""
        <div id='sidebarBoxVillageList'><div class='listEntry village active' data-did='1'></div></div>
        <div id='build' class='gid3'><div class='upgradeBuilding'><div id='contract'><div class='resourceWrapper'>
          <button class='resource transfer fillUp' onclick="document.querySelector('.resourceTransferDialog').style.display='block'">Fill</button>
        </div></div></div><button id='upgrade' onclick='window.upgrades=(window.upgrades||0)+1'>Upgrade</button></div>
        <div id='l1'>90</div><div id='l2'>180</div><div id='l3'>30</div><div id='l4'>40</div>
        <div class='heroItem'><div class='item item1'></div><span class='count'>99999</span></div>
        <div class='resourceTransferDialog' style='display:none'>{{rows}}
          <button class='dialogCancelButton' onclick="document.querySelector('.resourceTransferDialog').style.display='none'">Cancel</button>
          <div class='actionButton'><button id='confirm' onclick="window.transfers=(window.transfers||0)+1;{{confirmScript}}">Transfer selected</button></div>
        </div>
        """;
    private static async Task<IChromeBrowser> BrowserAsync(IPage page, string html)
    {
        await page.RouteAsync("https://hero.test/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = html }));
        await page.GotoAsync("https://hero.test/build.php?id=3&gid=3");
        var browser = Substitute.For<IChromeBrowser>();
        browser.CurrentPage.Returns(page);
        browser.CurrentUrl.Returns(_ => page.Url);
        browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async call => { await call.ArgAt<ILocator>(0).ClickAsync(); return FluentResults.Result.Ok(); });
        browser.Wait(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async call => { await call.ArgAt<ILocator>(0).WaitForAsync(); return FluentResults.Result.Ok(); });
        return browser;
    }
    private static UseHeroResourceCommand.Command Request(long wood = 10) => new(new(1), Plan, [wood, 20, 0, 0], [100, 200, 30, 40, 0]);

    [Fact]
    public async Task NamedRowsIgnoreInventoryOrderEquipmentAndHiddenCopies()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(Page(Rows()).Replace("style='display:none'", "") + "<div class='resourceTransferDialog' style='display:none'>" + Rows([999, 999, 999, 999]) + "</div>");
        var snapshot = await HeroResourceTransferParser.ReadAsync(page);
        snapshot.Readable.ShouldBeTrue();
        snapshot.Available.ShouldBe([73, 1136, 1657, 1657]);
    }

    [Theory]
    [InlineData("73", 73)]
    [InlineData("1,136", 1136)]
    [InlineData("1.136", 1136)]
    [InlineData("1\u202f136", 1136)]
    [InlineData("\u202d0\u202c", 0)]
    public void ParsesOnlyExactNonNegativeQuantities(string text, long expected)
    {
        HeroResourceTransferParser.TryAmount(text, out var value).ShouldBeTrue();
        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("2k")]
    [InlineData("-3")]
    [InlineData("")]
    public void RejectsRoundedMalformedOrMissingAmounts(string text) => HeroResourceTransferParser.TryAmount(text, out _).ShouldBeFalse();

    [Fact]
    public async Task MissingDuplicateOrWrongResourceIdentityIsUnreadableRatherThanShortage()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        foreach (var rows in new[] { Rows().Replace("name='lumber'", "name='unknown'"), Rows() + Rows(), Rows().Replace("item145", "item1"), Rows().Replace(">73</span>", ">?</span>") })
        {
            await page.SetContentAsync(Page(rows).Replace("style='display:none'", ""));
            (await HeroResourceTransferParser.ReadAsync(page)).Readable.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task GenuineShortageDoesNotConfirmOrRefreshAndLeavesProductionTimerPage()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var browser = await BrowserAsync(page, Page(Rows()) + "<div class='resourceTransferDialog' style='display:none'>" + Rows() + "</div>");
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(Request(754), TestContext.Current.CancellationToken);
        result.HasError<MissingResource>().ShouldBeTrue();
        result.HasError<Stop>().ShouldBeFalse();
        (await page.EvaluateAsync<int>("() => window.transfers || 0")).ShouldBe(0);
        (await page.Locator(".resourceTransferDialog:visible").CountAsync()).ShouldBe(0);
        await browser.DidNotReceive().Refresh();
        await browser.DidNotReceive().Navigate(Arg.Any<string>());
    }

    [Fact]
    public async Task FastTransientReloadThenStableFundedPageAllowsUpgradeAfterOneTransfer()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        const string completion = "window.selected=Array.from(document.querySelectorAll('.resourceRow input')).sort((a,b)=>['lumber','clay','iron','crop'].indexOf(a.name)-['lumber','clay','iron','crop'].indexOf(b.name)).map(n=>Number(n.value));history.replaceState(null,'','/build.php?id=3&reload=auto');history.replaceState(null,'','/build.php?id=3');document.querySelector('.resourceTransferDialog').remove();document.querySelector('#l1').textContent='100';document.querySelector('#l2').textContent='200';";
        var browser = await BrowserAsync(page, Page(Rows(), completion));
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        await page.Locator("#upgrade").ClickAsync();
        (await page.EvaluateAsync<int>("() => window.transfers")).ShouldBe(1);
        (await page.EvaluateAsync<int>("() => window.upgrades")).ShouldBe(1);
        (await page.EvaluateAsync<long[]>("() => window.selected")).ShouldBe([10, 20, 0, 0]);
        await browser.DidNotReceive().WaitPageChanged(Arg.Any<string>());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("readonly")]
    public async Task FullGranaryUnusedZeroFieldAllowsOneVerifiedTransferWithoutFillingIt(string attribute)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var rows = Rows([1928, 2817, 8291, 8689]).Replace("name='crop' value='0'", $"name='crop' value='0' {attribute}");
        const string completion = "window.selected=Array.from(document.querySelectorAll('.resourceRow input')).sort((a,b)=>['lumber','clay','iron','crop'].indexOf(a.name)-['lumber','clay','iron','crop'].indexOf(b.name)).map(n=>Number(n.value));document.querySelector('.resourceTransferDialog').remove();document.querySelector('#l1').textContent='856';document.querySelector('#l2').textContent='1732';";
        var html = Page(rows, completion).Replace("id='l4'>40", "id='l4'>7,800");
        var browser = await BrowserAsync(page, html);
        await page.EvaluateAsync("() => { window.filled=[]; document.querySelectorAll('.resourceRow input').forEach(n=>n.addEventListener('input',()=>window.filled.push(n.name))); }");
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var request = Request() with { Resource = [766, 1552, 0, 0], Required = [856, 1732, 30, 7800, 0] };
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(request, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        (await page.EvaluateAsync<string[]>("() => window.filled")).ShouldBe(["lumber", "clay", "iron"]);
        (await page.EvaluateAsync<long[]>("() => window.selected")).ShouldBe([766, 1552, 0, 0]);
        (await page.EvaluateAsync<int>("() => window.transfers")).ShouldBe(1);
    }

    [Theory]
    [InlineData("disabled", 5, "0")]
    [InlineData("disabled", 5, "5")]
    [InlineData("readonly", 5, "0")]
    [InlineData("readonly", 5, "5")]
    [InlineData("disabled", 0, "7")]
    [InlineData("readonly", 0, "7")]
    [InlineData("disabled", 0, "")]
    [InlineData("readonly", 0, "?")]
    public async Task NoneditableRequiredOrUnverifiedZeroFieldStopsBeforeConfirmation(string attribute, long needed, string value)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var rows = Rows().Replace("name='crop' value='0'", $"name='crop' value='{value}' {attribute}");
        var browser = await BrowserAsync(page, Page(rows));
        var sink = new CaptureSink();
        using var logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var request = Request() with { Resource = [10, 20, 0, needed] };
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(request, TestContext.Current.CancellationToken);
        result.HasError<Stop>().ShouldBeTrue();
        result.HasError<Retry>().ShouldBeFalse();
        (await page.EvaluateAsync<int>("() => window.transfers || 0")).ShouldBe(0);
        var failure = sink.Events.Single(e => e.MessageTemplate.Text.Contains("stage=input_unverified"));
        failure.Properties["Resource"].ToString().ShouldBe("\"crop\"");
        failure.Properties["Count"].ToString().ShouldBe("1");
        failure.Properties["Enabled"].ToString().ShouldBe(attribute == "disabled" ? "False" : "True");
        failure.Properties["ReadOnly"].ToString().ShouldBe(attribute == "readonly" ? "True" : "False");
        failure.Properties["Needed"].ToString().ShouldBe(needed.ToString());
    }

    private sealed class CaptureSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = [];
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    [Theory]
    [InlineData(9, 91, true)]
    [InlineData(9, 90, false)]
    [InlineData(11, 100, false)]
    public async Task BlurClampedSelectionIsAcceptedOnlyWhenLowerAndFullyFunded(long selectedWood, long villageWood, bool allowed)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var rows = Rows().Replace("name='lumber' value='0'", $"name='lumber' value='0' onblur=\"this.value='{selectedWood}';document.querySelector('#l1').textContent='{villageWood}';\"")
            .Replace("name='crop' value='0'", "name='crop' value='0' disabled");
        const string completion = "window.selectedWood=Number(document.querySelector('[name=lumber]').value);document.querySelector('.resourceTransferDialog').remove();document.querySelector('#l1').textContent='100';document.querySelector('#l2').textContent='200';";
        var browser = await BrowserAsync(page, Page(rows, completion));
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBe(allowed);
        (await page.EvaluateAsync<int>("() => window.transfers || 0")).ShouldBe(allowed ? 1 : 0);
        if (allowed) (await page.EvaluateAsync<long>("() => window.selectedWood")).ShouldBe(selectedWood);
        else result.HasError<Stop>().ShouldBeTrue();
    }

    [Fact]
    public async Task ProductionThatClearsAllDeficitsClosesDialogWithoutConfirming()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var rows = Rows().Replace("name='lumber' value='0'", "name='lumber' value='0' onblur=\"this.value='0';document.querySelector('#l1').textContent='100';\"")
            .Replace("name='clay' value='0'", "name='clay' value='0' onblur=\"this.value='0';document.querySelector('#l2').textContent='200';document.querySelector('#confirm').disabled=true;\"");
        var browser = await BrowserAsync(page, Page(rows));
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        (await page.EvaluateAsync<int>("() => window.transfers || 0")).ShouldBe(0);
        (await page.Locator(".resourceTransferDialog:visible").CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task LastEditedFieldIsBlurredBeforeFinalSelectionIsLoggedAndConfirmed()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var rows = Rows().Replace("name='crop' value='0'", "name='crop' value='0' onblur=\"this.value='9';document.querySelector('#l4').textContent='31';\"");
        const string completion = "document.querySelector('.resourceTransferDialog').remove();document.querySelector('#l1').textContent='100';document.querySelector('#l2').textContent='200';document.querySelector('#l4').textContent='40';";
        var browser = await BrowserAsync(page, Page(rows, completion).Replace("id='l4'>40", "id='l4'>30"));
        var sink = new CaptureSink();
        using var logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var request = Request() with { Resource = [10, 20, 0, 10] };
        (await new UseHeroResourceCommand(browser, logger).ExecuteAsync(request, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var confirmation = sink.Events.Single(e => e.MessageTemplate.Text.Contains("stage=confirmation_attempt"));
        confirmation.Properties["Selected"].ToString().ShouldBe("[10, 20, 0, 9]");
        (await page.EvaluateAsync<int>("() => window.transfers")).ShouldBe(1);
    }

    [Fact]
    public async Task RealFastRedirectDoesNotRequireObservingTheIntermediateUrl()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var browser = await BrowserAsync(page, Page(Rows(), "location.href='/build.php?id=3&reload=auto';"));
        var submissions = 0;
        await page.RouteAsync("https://hero.test/**", async route =>
        {
            if (route.Request.Url.Contains("reload=auto"))
            {
                submissions++;
                await route.FulfillAsync(new() { Status = 302, Headers = new Dictionary<string, string> { ["Location"] = "/build.php?id=3&done=1" } });
            }
            else await route.FulfillAsync(new() { ContentType = "text/html", Body = Page(Rows()).Replace("id='l1'>90", "id='l1'>100").Replace("id='l2'>180", "id='l2'>200") });
        });
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        submissions.ShouldBe(1);
        page.Url.ShouldContain("done=1");
        await browser.DidNotReceive().WaitPageChanged(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationFailureOrExceptionNeverReturnsRetryOrConfirmsTwice(bool throwAfterClick)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var browser = await BrowserAsync(page, Page(Rows()));
        browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async call =>
        {
            var button = call.ArgAt<ILocator>(0);
            await button.ClickAsync();
            if (await button.GetAttributeAsync("id") != "confirm") return FluentResults.Result.Ok();
            if (throwAfterClick) throw new PlaywrightException("Response lost after click");
            return Retry.Error;
        });
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        result.HasError<Stop>().ShouldBeTrue();
        result.HasError<Retry>().ShouldBeFalse();
        (await page.EvaluateAsync<int>("() => window.transfers")).ShouldBe(1);
    }

    [Fact]
    public async Task ZeroResourceRequestDoesNotOpenOrConfirmATransfer()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var browser = await BrowserAsync(page, Page(Rows()));
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var request = Request() with { Resource = [0, 0, 0, 0] };
        (await new UseHeroResourceCommand(browser, logger).ExecuteAsync(request, TestContext.Current.CancellationToken)).HasError<Stop>().ShouldBeTrue();
        await browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("document.querySelector('.active').setAttribute('data-did','2');document.querySelector('.resourceTransferDialog').remove();")]
    public async Task UncertainOrWrongVillageAfterConfirmationStopsWithoutAnotherTransfer(string completion)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var browser = await BrowserAsync(page, Page(Rows(), completion));
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var result = await new UseHeroResourceCommand(browser, logger).ExecuteAsync(Request(), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(200));
        result.HasError<Stop>().ShouldBeTrue();
        result.HasError<Retry>().ShouldBeFalse();
        (await page.EvaluateAsync<int>("() => window.transfers")).ShouldBe(1);
        (await page.EvaluateAsync<int>("() => window.upgrades || 0")).ShouldBe(0);
    }
}
