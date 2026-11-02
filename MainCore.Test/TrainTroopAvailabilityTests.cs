using System.Reflection;
using FluentResults;
using MainCore.Commands.Features;
using MainCore.Enums;
using MainCore.Errors;
using MainCore.Parsers;
using MainCore.Services;
using MainCore.Test.Parsers;
using Microsoft.Playwright;

namespace MainCore.Test;

public sealed class TrainTroopAvailabilityTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    private static string Card(int troop = 12, string amount = "7", string input = "") =>
        $"<div class='troop'><img class='unit u{troop}'><div class='cta'><input class='text' {input}><a>{amount}</a></div></div>";

    private static ValueTask<Result> Execute(IChromeBrowser browser, int amount, bool partial) =>
        (ValueTask<Result>)typeof(TrainTroopCommand).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new TrainTroopCommand(new Serilog.LoggerConfiguration().CreateLogger(), browser), [new TrainTroopCommand.Command(TroopEnums.Spearman, amount, partial)])!;

    [Theory]
    [InlineData("absent")]
    [InlineData("disabled")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    public async Task UnavailableSelectedUnitSkipsQuicklyWithoutAnySubmission(string kind)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync((kind switch {
            "absent" => Card(11), // Actual Claude screenshot: only Clubswinger is researched.
            "disabled" => Card(input: "disabled"),
            "duplicate" => Card() + Card(),
            _ => Card(amount: "Research first")
        }) + "<button id='s1'>Train</button>");
        var browser = Substitute.For<IChromeBrowser>(); browser.CurrentPage.Returns(page);
        var result = await Execute(browser, 10, false).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        result.HasError<Skip>().ShouldBeTrue();
        await browser.DidNotReceive().Input(Arg.Any<ILocator>(), Arg.Any<string>(), Arg.Any<string?>());
        await browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
    }

    [Theory]
    [InlineData(false, 10, 0)]
    [InlineData(true, 10, 7)]
    [InlineData(false, 3, 3)]
    public async Task AvailableTrainingHonorsResourcePolicyAndNeverSubmitsMoreThanAvailable(bool partial, int requested, int submitted)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(Card() + "<button id='s1' onclick='window.submits=(window.submits||0)+1'>Train</button>");
        var browser = Substitute.For<IChromeBrowser>(); browser.CurrentPage.Returns(page);
        browser.Input(Arg.Any<ILocator>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(async call => { await call.Arg<ILocator>().FillAsync(call.ArgAt<string>(1)); return Result.Ok(); });
        browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async call => { await call.Arg<ILocator>().ClickAsync(); return Result.Ok(); });
        var result = await Execute(browser, requested, partial);
        if (submitted == 0)
        {
            result.HasError<MissingResource>().ShouldBeTrue();
            await browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
        }
        else
        {
            result.IsSuccess.ShouldBeTrue();
            (await page.Locator("input").InputValueAsync()).ShouldBe(submitted.ToString());
            (await page.EvaluateAsync<int>("window.submits")).ShouldBe(1);
        }
    }
}
