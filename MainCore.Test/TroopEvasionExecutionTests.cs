using FluentResults;
using MainCore;
using MainCore.Commands.Navigate;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Models;
using MainCore.Services;
using MainCore.Services.Evasion;
using MainCore.Tasks;
using MainCore.Test.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace MainCore.Test.Evasion;

internal static class TroopPreviewFixture
{
    // Reconstructed from the inspected live English preview, without action tokens.
    public static string Markup(long[] amounts, int source = 1, int x = 10, int y = -23, string onConfirm = "") =>
        $"<h1>Rally Point Level 10</h1><form id='troopSendForm' method='post' action='/build.php?gid=16&amp;tt=2'>" +
        $"<input type='hidden' name='x' value='{x}'><input type='hidden' name='y' value='{y}'><input type='hidden' name='eventType' value='5'><input type='hidden' name='redeployHero' value=''><input type='hidden' name='troops[0][villageId]' value='{source}'>" +
        string.Concat(amounts.Select((n, i) => $"<input type='hidden' name='troops[0][t{i + 1}]' value='{n}'>")) +
        "<input type='hidden' name='troops[0][scoutTarget]' value=''><input type='hidden' name='troops[0][catapultTarget1]' value=''><input type='hidden' name='troops[0][catapultTarget2]' value=''>" +
        $"<table id='short_info'><tr><td>Target</td><td><a href='/karte.php?x={x}&amp;y={y}'>Two</a></td></tr></table>" +
        "<table class='troop_details'><thead><tr><td class='troopHeadline'>Reinforcement for Two</td></tr></thead><tbody class='units'><tr><td>Units</td></tr><tr>" +
        string.Concat(amounts.Select(n => $"<td class='unit'>&#8237;{n}&#8236;</td>")) +
        $"</tr></tbody></table><div id='in' class='in'>In 0:02:00 hours</div><span id='at' class='timer' counting='up' value='1793400000'>12:00:00</span><button id='confirmSendTroops' name='confirmSendTroops' type='button' value='Confirm' class='green rallyPointConfirm' onclick=\"{onConfirm}\">Confirm</button></form>";

    public static string Movement(long[] amounts) => "<table class='troop_details outSupply'><thead><tr><td class='role'><a href='/karte.php?d=92039'>One</a></td><td class='troopHeadline'><a href='/karte.php?d=89634'>Reinforcement for Two</a></td></tr></thead><tbody class='units last'><tr>" +
        string.Concat(amounts.Select(n => $"<td class='unit'>{n}</td>")) +
        "</tr></tbody><tbody><tr><td><div class='infos'><span class='in'><span class='timer' counting='down' value='120'>0:02:00</span></span></div><div class='abort' data-hide-after='90'><button type='button' class='icon' onclick=\"location.href='/build.php?gid=16&amp;tt=1&amp;recall=42'\">Cancel</button></div></td></tr></tbody></table>";
}

public class TroopEvasionExecutionTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    private static readonly long[] Amounts = [10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private const string Sidebar = "<div id='servertime'>12:00:00</div><div id='sidebarBoxVillageList'><div class='listEntry village active attack' data-did='1'></div></div>";
    private const string Home = "<table class='troop_details'><thead><tr><td class='role'><a href='/karte.php?d=92039'>One</a></td><td class='troopHeadline'><a href='/profile/1'>Own troops</a></td></tr></thead><tbody><tr id='unitRowAtTown'><td>10</td></tr></tbody></table>";

    [Fact]
    public async Task DelayedObservedPreviewNeedsNoConfirmHeadingAndReadsDurationNotArrival()
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        await page.SetContentAsync("<h1>Loading preview</h1>");
        await page.EvaluateAsync("html => setTimeout(() => document.body.innerHTML = html, 300)", TroopPreviewFixture.Markup(Amounts));
        var ready = await TroopConfirmationAdapter.WaitAsync(page, 1, 10, -23, Amounts, DateTimeOffset.UtcNow.AddSeconds(3), TestContext.Current.CancellationToken);
        ready.Reason.ShouldBe("matching-confirmation-ready"); ready.Confirmation.ShouldNotBeNull();
        ready.Confirmation.Duration.ShouldBe(TimeSpan.FromMinutes(2));
        (await page.Locator("#confirmSendTroops").GetAttributeAsync("type")).ShouldBe("button");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("event")]
    [InlineData("hidden")]
    [InlineData("visible")]
    [InlineData("target")]
    [InlineData("duration")]
    [InlineData("arrival-only")]
    [InlineData("extra-wave")]
    [InlineData("scout-target")]
    [InlineData("catapult-target")]
    [InlineData("missing-neutral")]
    [InlineData("redeploy")]
    [InlineData("disabled")]
    [InlineData("duplicate")]
    public async Task MismatchedOrAmbiguousObservedPreviewNeverOffersFinalClick(string mismatch)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        var html = TroopPreviewFixture.Markup(Amounts);
        html = mismatch switch
        {
            "source" => html.Replace("name='troops[0][villageId]' value='1'", "name='troops[0][villageId]' value='2'"),
            "event" => html.Replace("name='eventType' value='5'", "name='eventType' value='3'"),
            "hidden" => html.Replace("name='troops[0][t1]' value='10'", "name='troops[0][t1]' value='9'"),
            "visible" => html.Replace("&#8237;10&#8236;", "&#8237;9&#8236;"),
            "target" => html.Replace("karte.php?x=10", "karte.php?x=11"),
            "duration" => html.Replace("0:02:00 hours", "0:62:00 hours"),
            "arrival-only" => html.Replace("id='in'", "id='not-duration'"),
            "extra-wave" => html.Replace("</form>", "<input name='troops[1][t1]' value='1'></form>"),
            "scout-target" => html.Replace("name='troops[0][scoutTarget]' value=''", "name='troops[0][scoutTarget]' value='1'"),
            "catapult-target" => html.Replace("name='troops[0][catapultTarget2]' value=''", "name='troops[0][catapultTarget2]' value='0'"),
            "missing-neutral" => html.Replace("<input type='hidden' name='troops[0][catapultTarget1]' value=''>", ""),
            "redeploy" => html.Replace("name='redeployHero' value=''", "name='redeployHero' value='1'"),
            "disabled" => html.Replace("class='green rallyPointConfirm'", "disabled class='green rallyPointConfirm'"),
            "duplicate" => html + html,
            _ => html,
        };
        await page.SetContentAsync(html);
        (await TroopConfirmationAdapter.ReadAsync(page, 1, 10, -23, Amounts)).Confirmation.ShouldBeNull();
    }

    [Fact]
    public async Task DelayedOutboundMovementIsObservedWithoutAnotherSend()
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        await page.SetContentAsync(Home);
        await page.EvaluateAsync("html => setTimeout(() => document.body.insertAdjacentHTML('beforeend', html), 250)", TroopPreviewFixture.Movement(Amounts));
        var movement = await RallyMovementAdapter.WaitForUniqueNewAsync(page, new HashSet<string>(), 89634, Amounts, TimeSpan.FromSeconds(2));
        movement.ShouldNotBeNull(); movement.Id.ShouldBe("42");
    }

    [Fact]
    public async Task ObservedLiveReinforcesHeadingAndNinetySecondControlMatchExactMovement()
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        long[] oneClub = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var html = TroopPreviewFixture.Movement(oneClub).Replace("d=92039", "d=122112").Replace("d=89634", "d=92039")
            .Replace("Reinforcement for Two", "Player reinforces Home").Replace("value='120'>0:02:00", "value='9628'>2:40:28");
        await page.SetContentAsync(html);
        var movement = await RallyMovementAdapter.FindUniqueNewAsync(page, new HashSet<string>(), 92039, oneClub);
        movement.ShouldNotBeNull(); movement.Id.ShouldBe("42"); movement.Remaining.ShouldBe(TimeSpan.FromSeconds(9628));
        (await RallyRecallAdapter.FindAsync(page, "42")).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "normal")]
    [InlineData(false, "wrong-source")]
    [InlineData(false, "late")]
    public async Task ActualEvasionPreparesEarlyClicksFinalOnceAtTenSecondsAndSchedulesExactCancel(bool suppressMovement, string previewMode)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        var previewClicks = 0; var finalClicks = 0; DateTimeOffset previewAt = default, finalAt = default;
        await page.ExposeFunctionAsync("previewOpened", () => { previewClicks++; previewAt = DateTimeOffset.UtcNow; });
        await page.ExposeFunctionAsync("finalSent", () => { finalClicks++; finalAt = DateTimeOffset.UtcNow; });
        var troopInputs = string.Concat(Enumerable.Range(1, 10).Select(i => $"<td><input name='troop[t{i}]'> / <a>{Amounts[i - 1]}</a></td>"));
        var previewDelay = previewMode == "late" ? 5000 : 250;
        var sendForm = Sidebar + $"<form onsubmit=\"event.preventDefault();previewOpened();setTimeout(() => location.href='/preview',{previewDelay})\"><table id='troops'><tr>{troopInputs}</tr></table><input id='xCoordInput'><input id='yCoordInput'><input type='radio' name='eventType' value='5'><button id='ok' type='submit'>Send</button></form>";
        await page.RouteAsync("https://evade.test/**", async route =>
        {
            var url = new Uri(route.Request.Url);
            var body = url.AbsolutePath == "/preview" ? Sidebar + TroopPreviewFixture.Markup(Amounts, source: previewMode == "wrong-source" ? 2 : 1, onConfirm: "finalSent();this.disabled=true;location.href='/dorf1.php'") :
                url.Query.Contains("tt=2") ? sendForm : Sidebar + Home + (finalClicks > 0 && !suppressMovement ? TroopPreviewFixture.Movement(Amounts) : "");
            await route.FulfillAsync(new() { ContentType = "text/html", Body = body });
        });
        await page.GotoAsync("https://evade.test/dorf1.php");
        using var h = new TaskExecutionHarness(page);
        h.Browser.Navigate(Arg.Any<string>()).Returns(async call => { await page.GotoAsync(call.Arg<string>()); return Result.Ok(); });
        using (var db = h.Factory.CreateDbContext())
        {
            var source = db.Villages.Single(x => x.Id == 1); source.X = 9; source.Y = -29;
            var destination = db.Villages.Single(x => x.Id == 2); destination.X = 10; destination.Y = -23;
            db.VillagesSetting.AddRange(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.TroopEvasionEnable, Value = 1 },
                new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.ConfirmedCapitalStatus, Value = 2 });
            db.SaveChanges();
        }
        var impact = DateTimeOffset.UtcNow.AddSeconds(13);
        var key = new EvasionDispatch { AccountId = 1, VillageId = 1, DestinationId = 2, AttackId = "attack", Kind = "troop", ImpactUtc = impact.ToString("O"), Payload = "{}" };
        using (var db = h.Factory.CreateDbContext()) EvasionStore.TryPlan(db, key).ShouldBeTrue();
        var services = new ServiceCollection(); services.AddMainCore(); services.AddMainCoreHandlers();
        services.AddSingleton(h.Factory); services.AddSingleton(h.Browser); services.AddSingleton<Serilog.ILogger>(h.Logger); services.AddSingleton(h.Tasks);
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var implementation = new EvasionTroopTask(h.Browser, scope.ServiceProvider.GetRequiredService<SwitchVillageCommand.Handler>(), h.Factory, h.Tasks, h.Logger);
        var task = new EvasionTroopTask.Task(new(1), new(1), "attack", 2, impact);
        (await implementation.ExecuteAsync(task, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        previewClicks.ShouldBe(1);
        previewAt.ShouldBeLessThan(impact - EvasionRules.TroopLead);
        using var saved = h.Factory.CreateDbContext(); var dispatch = saved.EvasionDispatches.Single();
        var attempted = DateTimeOffset.Parse(dispatch.SentUtc!);
        if (previewMode != "normal")
        {
            finalClicks.ShouldBe(0); dispatch.State.ShouldBe("Submitting");
            h.Tasks.GetTaskList(new(1)).OfType<EvasionCancelTask.Task>().ShouldBeEmpty();
        }
        else
        {
            finalClicks.ShouldBe(1); finalAt.ShouldBeGreaterThanOrEqualTo(impact - EvasionRules.TroopLead);
            finalAt.ShouldBeLessThan(impact - EvasionRules.TroopLead + TimeSpan.FromSeconds(1));
            attempted.ShouldBeGreaterThan(previewAt); attempted.ShouldBeLessThanOrEqualTo(finalAt);
            if (suppressMovement)
            {
                dispatch.State.ShouldBe("Submitting"); h.Tasks.GetTaskList(new(1)).OfType<EvasionCancelTask.Task>().ShouldBeEmpty();
            }
            else
            {
                dispatch.State.ShouldBe("Sent"); dispatch.MovementId.ShouldBe("42");
                var cancel = h.Tasks.GetTaskList(new(1)).OfType<EvasionCancelTask.Task>().Single();
                cancel.MovementId.ShouldBe("42"); cancel.ExecuteAt.ShouldBeGreaterThanOrEqualTo(impact.AddSeconds(1).LocalDateTime);
                cancel.ExecuteAt.ShouldBeGreaterThanOrEqualTo(attempted.AddSeconds(10).LocalDateTime);
            }
        }
        (await implementation.ExecuteAsync(task, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        previewClicks.ShouldBe(1); finalClicks.ShouldBe(previewMode == "normal" ? 1 : 0); // durable intent prevents replay
    }
}
