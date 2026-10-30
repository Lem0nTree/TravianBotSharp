using System.Text.Json;
using FluentResults;
using MainCore;
using MainCore.Behaviors;
using MainCore.Commands.Features.UpgradeBuilding;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Errors;
using MainCore.Models;
using MainCore.Parsers;
using MainCore.Services;
using MainCore.Services.BuildingAutomation;
using MainCore.Tasks;
using MainCore.Test.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace MainCore.Test;

public class BuildQueueProgressTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    private const string Sidebar = "<div id='sidebarBoxVillageList'><div class='listEntry village active' data-did='1'></div></div><div id='servertime'>12:00:00</div><div id='questmasterButton' class='vid_2'></div>";
    private const string Stocks = "<div id='l1'>80000</div><div id='l2'>80000</div><div id='l3'>80000</div><div id='l4'>80000</div><div id='stockBarFreeCrop'>1000</div><div id='stockBar'><div class='warehouse'><div class='capacity'><div class='value'>80000</div></div></div><div class='granary'><div class='capacity'><div class='value'>80000</div></div></div></div>";
    private static string BuildPage(int slot, BuildingEnums type, int level, int next, bool disabled = false, bool empty = false) => Sidebar + Stocks +
        $"<div id='build' class='gid{(empty ? 0 : (int)type)} level{level}'><div id='contract_building{(int)type}'><div class='upgradeBuilding'><div id='contract'><div class='resourceWrapper'>{string.Concat(Enumerable.Repeat("<div class='resource'>1</div>", 5))}</div></div></div><div class='upgradeButtonsContainer'><div class='section1'><button class='{(empty ? "new" : "build")} green' value='{(empty ? "Construct building" : $"Upgrade to level {next}")}' {(disabled ? "disabled" : "")} onclick=\"location.href='/dorf2.php?clicked={next}'\">{(empty ? "Construct building" : $"Upgrade to level {next}")}</button></div><div class='section2'><button class='videoFeatureButton'>Video upgrade</button></div></div></div></div>";
    private static string Queue(BuildingEnums type, int level) => level == 0 ? "" : $"<ul class='buildingList'><li><div class='name'>{type}<span class='lvl'>Level {level}</span></div><div class='buildDuration'><span class='timer' value='180'>0:03:00</span></div></li></ul>";
    private static string Center(int current, int queued = 0) => Sidebar + Stocks + "<div id='villageContent' class='village2'>" +
        string.Concat(Enumerable.Range(19, 22).Select(slot => $"<div class='buildingSlot a{slot}' data-aid='{slot}' data-gid='{(slot == 27 ? 25 : slot == 26 ? 15 : 0)}'><a data-level='{(slot == 27 ? current : slot == 26 ? 5 : 0)}' class='{(slot == 27 && queued > current ? "underConstruction" : "")}'></a><svg><path onclick=\"location.href='/build.php?id={slot}&amp;gid=25'\"></path></svg></div>")) + "</div>" + Queue(BuildingEnums.Residence, queued);
    private static string Fields(int targetLevel, bool constructing, int queue = 0) => Sidebar + Stocks + "<div id='resourceFieldContainer'>" +
        string.Concat(Enumerable.Range(1, 18).Select(slot => $"<a class='level buildingSlot{slot} gid{(slot == 18 ? 2 : 1)} level{(slot == 18 ? targetLevel : 1)} {(slot == 18 && constructing ? "underConstruction" : "")}'></a>")) + "</div>" + Queue(BuildingEnums.ClayPit, queue);

    private static void Seed(TaskExecutionHarness h, int slot = 27, BuildingEnums type = BuildingEnums.Residence, int level = 1, int queued = 0, int goal = 5)
    {
        using var db = h.Factory.CreateDbContext();
        db.Buildings.Add(new Building { VillageId = 1, Location = slot, Type = type, Level = level });
        db.Jobs.Add(new Job { VillageId = 1, Type = JobTypeEnums.NormalBuild, Content = JsonSerializer.Serialize(new NormalBuildPlan { Location = slot, Type = type == BuildingEnums.Site ? BuildingEnums.Residence : type, Level = goal }) });
        db.VillagesSetting.Add(new VillageSetting { VillageId = 1, Setting = VillageSettingEnums.AutoBuildEnable, Value = 1 });
        if (queued > 0) db.QueueBuildings.Add(new QueueBuilding { VillageId = 1, Location = slot, Type = type, Level = queued, CompleteTime = DateTime.Now.AddMinutes(3) });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 3)]
    [InlineData(2, 0, 3)]
    public async Task CapturesTheUiSubmittedStepRatherThanTheUltimateQueueGoal(int current, int queued, int next)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        await page.RouteAsync("https://build.test/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = BuildPage(27, BuildingEnums.Residence, current, next) }));
        await page.GotoAsync("https://build.test/build.php?id=27&gid=25");
        using var h = new TaskExecutionHarness(page); Seed(h, level: current, queued: queued);
        using var db = h.Factory.CreateDbContext();
        var goal = JsonSerializer.Deserialize<NormalBuildPlan>(db.Jobs.Single().Content)!;
        var step = await ConstructionStepParser.CaptureAsync(page, db, new(1), new(1), goal);
        step.ShouldBe(new ConstructionStep(27, BuildingEnums.Residence, next));
        JsonSerializer.Deserialize<NormalBuildPlan>(db.Jobs.Single().Content)!.Level.ShouldBe(5);
    }

    [Fact]
    public async Task NewSiteAndDisabledNormalVideoContractHaveVerifiedStepTargets()
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        var html = BuildPage(27, BuildingEnums.Residence, 0, 1, empty: true);
        await page.RouteAsync("https://build.test/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = html }));
        await page.GotoAsync("https://build.test/build.php?id=27");
        using var h = new TaskExecutionHarness(page); Seed(h, type: BuildingEnums.Site, level: -1);
        using var db = h.Factory.CreateDbContext();
        var goal = new NormalBuildPlan { Location = 27, Type = BuildingEnums.Residence, Level = 5 };
        (await ConstructionStepParser.CaptureAsync(page, db, new(1), new(1), goal))!.Level.ShouldBe(1);
        var building = db.Buildings.Single(); building.Type = BuildingEnums.Residence; building.Level = 1; db.SaveChanges();
        html = BuildPage(27, BuildingEnums.Residence, 1, 2, disabled: true);
        await page.GotoAsync("https://build.test/build.php?id=27");
        (await ConstructionStepParser.CaptureAsync(page, db, new(1), new(1), goal))!.Level.ShouldBe(2);
    }

    [Theory]
    [InlineData("village")]
    [InlineData("slot")]
    [InlineData("level")]
    [InlineData("queued")]
    public async Task InconsistentContextRejectsBeforeAnyConstructionClick(string mismatch)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        var html = BuildPage(27, BuildingEnums.Residence, 1, 2);
        if (mismatch == "village") html = html.Replace("data-did='1'", "data-did='2'");
        if (mismatch == "level") html = html.Replace("gid25 level1", "gid25 level2");
        await page.RouteAsync("https://build.test/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = html }));
        await page.GotoAsync("https://build.test/build.php?id=" + (mismatch == "slot" ? 26 : 27));
        using var h = new TaskExecutionHarness(page); Seed(h, queued: mismatch == "queued" ? 2 : 0);
        var command = new HandleUpgradeCommand(h.Browser, h.Factory, h.Logger);
        var result = await command.ExecuteAsync(new(new(1), new() { Location = 27, Type = BuildingEnums.Residence, Level = 5 }), TestContext.Current.CancellationToken);
        result.HasError<Retry>().ShouldBeTrue();
        await h.Browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task FieldVerificationNavigatesToFreshDorf1AfterAnUnexpectedDorf2Redirect()
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        var paths = new List<string>();
        await page.RouteAsync("https://build.test/**", r =>
        { paths.Add(new Uri(r.Request.Url).AbsolutePath); return r.FulfillAsync(new() { ContentType = "text/html", Body = r.Request.Url.Contains("dorf1") ? Fields(7, true, 8) : Center(1) }); });
        await page.GotoAsync("https://build.test/dorf2.php");
        using var h = new TaskExecutionHarness(page);
        var updates = 0;
        var result = await ConstructionReadback.ObserveAsync(h.Browser, new(1), new(18, BuildingEnums.ClayPit, 8),
            () => { updates++; page.Url.ShouldContain("dorf1.php"); return Task.FromResult(Result.Ok()); }, () => true, TestContext.Current.CancellationToken);
        result.ShouldBeTrue(); updates.ShouldBe(1); paths.Last().ShouldBe("/dorf1.php");
        await h.Browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
    }

    [Theory]
    [InlineData(1, false, 0, 2)]
    [InlineData(1, true, 2, 3)]
    public async Task StaleDatabaseOrLowerExistingQueueCannotProveTheSubmittedStep(int current, bool constructing, int queued, int submitted)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        await page.RouteAsync("https://build.test/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = Fields(current, constructing, queued) }));
        await page.GotoAsync("https://build.test/dorf1.php");
        using var h = new TaskExecutionHarness(page);
        // Simulate a misleading cached DB proof: the fresh DOM must still qualify it.
        (await ConstructionReadback.ObserveAsync(h.Browser, new(1), new(18, BuildingEnums.ClayPit, submitted),
            () => Task.FromResult(Result.Ok()), () => true, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualUpgradeTaskAdvancesOneStepAndRetainsItsMultiLevelGoal(bool uncertainRedirectWait)
    {
        await using var context = await CreateContextAsync(); var page = await context.NewPageAsync();
        var current = 1; var queued = 0; var clicks = 0;
        await page.RouteAsync("https://build.test/**", r =>
        {
            var uri = new Uri(r.Request.Url);
            if (uri.Query.Contains("clicked=")) { queued = int.Parse(uri.Query.Split("clicked=")[1]); clicks++; }
            var html = uri.AbsolutePath.EndsWith("build.php") ? BuildPage(27, BuildingEnums.Residence, current, Math.Max(current, queued) + 1) : Center(current, queued);
            return r.FulfillAsync(new() { ContentType = "text/html", Body = html });
        });
        await page.GotoAsync("https://build.test/dorf2.php");
        using var h = new TaskExecutionHarness(page); Seed(h);
        h.Browser.Click(Arg.Any<ILocator>(), Arg.Any<string?>()).Returns(async c => { await c.ArgAt<ILocator>(0).ClickAsync(); return Result.Ok(); });
        h.Browser.ExecuteJsScript(Arg.Any<string>()).Returns(async _ => { await page.GotoAsync("https://build.test/build.php?id=27&gid=25"); return Result.Ok(); });
        h.Browser.WaitPageChanged(Arg.Any<string>()).Returns(c => uncertainRedirectWait && c.Arg<string>() == "dorf" ? Retry.Error : Result.Ok());
        var services = new ServiceCollection(); services.AddMainCore(); services.AddMainCoreHandlers();
        services.AddSingleton(h.Factory); services.AddScoped(_ => h.Factory.CreateDbContext()); services.AddSingleton(h.Browser);
        services.AddSingleton<Serilog.ILogger>(h.Logger); services.AddSingleton(h.Tasks); services.AddSingleton(h.Notifications);
        services.AddTransient<UpgradeBuildingTask>();
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var task = new UpgradeBuildingTask.Task(new(1), new(1));
        h.Tasks.Add(new EvasionScanTask.Task(new(1), new(1))); // Yield after the single observed step.
        var implementation = scope.ServiceProvider.GetRequiredService<UpgradeBuildingTask>();
        (await implementation.ExecuteAsync(task, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        task.MadeProgress.ShouldBeTrue(); clicks.ShouldBe(1); queued.ShouldBe(2);
        using (var db = h.Factory.CreateDbContext()) JsonSerializer.Deserialize<NormalBuildPlan>(db.Jobs.Single().Content)!.Level.ShouldBe(5);
        current = 2; queued = 0;
        using (var db = h.Factory.CreateDbContext()) { db.QueueBuildings.RemoveRange(db.QueueBuildings); db.Buildings.Single(x => x.Location == 27).Level = 2; db.SaveChanges(); }
        await page.GotoAsync("https://build.test/dorf2.php");
        (await implementation.ExecuteAsync(task, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        clicks.ShouldBe(2); queued.ShouldBe(3);
        using (var db = h.Factory.CreateDbContext()) JsonSerializer.Deserialize<NormalBuildPlan>(db.Jobs.Single().Content)!.Level.ShouldBe(5);
    }

    [Fact]
    public async Task PendingNormalQueueIsHandledBeforeBrowserBehaviorsAndPreservesDispatcherWaitAndHold()
    {
        using var h = new TaskExecutionHarness(); Seed(h);
        var priority = new AutoBuildQueuePriority(h.Factory, h.Tasks, h.Failures);
        var behavior = new AccountTaskBehavior<AutoBuildTask.Task, Result>(h.Browser, h.Tasks, null!, null!, null!, null!, null!, priority);
        var auto = new AutoBuildTask.Task(new(1), new(1));
        (await behavior.HandleAsync(auto, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var dispatcher = h.Tasks.GetTaskList(new(1)).OfType<UpgradeBuildingTask.Task>().Single();
        dispatcher.ExecuteAt = DateTime.Now.AddHours(1); var wait = dispatcher.ExecuteAt;
        priority.TryHandle(auto, h.Logger).ShouldBeTrue(); dispatcher.ExecuteAt.ShouldBe(wait);
        h.Failures.Fail(new(1), dispatcher, uncertain: true);
        priority.TryHandle(auto, h.Logger).ShouldBeTrue(); dispatcher.IsQuarantined.ShouldBeTrue(); dispatcher.ExecuteAt.ShouldBe(wait);
        await h.Browser.DidNotReceive().Refresh(); await h.Browser.DidNotReceive().Navigate(Arg.Any<string>());
        await h.Browser.DidNotReceive().Click(Arg.Any<ILocator>(), Arg.Any<string?>());
        using var db = h.Factory.CreateDbContext(); db.Jobs.Count().ShouldBe(1);
    }

    [Fact]
    public void AtomicDispatcherEnsureReturnsTheSameExistingTaskWithoutChangingItsSchedule()
    {
        using var h = new TaskExecutionHarness(); var task = h.QueueBuild(); task.ExecuteAt = DateTime.Now.AddHours(1); var wait = task.ExecuteAt;
        var results = Enumerable.Range(0, 50).Select(_ => h.Tasks.EnsureTask(new UpgradeBuildingTask.Task(new(1), new(1)))).ToArray();
        results.ShouldAllBe(x => ReferenceEquals(x, task)); h.Tasks.GetTaskList(new(1)).OfType<UpgradeBuildingTask.Task>().Count().ShouldBe(1); task.ExecuteAt.ShouldBe(wait);
    }

    [Fact]
    public async Task ConcurrentAtomicEnsureAddsOnlyOneTask()
    {
        using var h = new TaskExecutionHarness();
        // Account-level tasks exercise the same actor lock without sharing a test
        // SQLite connection across worker threads for village ownership checks.
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => h.Tasks.EnsureTask(new LoginTask.Task(new(1))), TestContext.Current.CancellationToken)));
        results.ShouldAllBe(x => ReferenceEquals(x, results[0]));
        h.Tasks.GetTaskList(new(1)).OfType<LoginTask.Task>().Count().ShouldBe(1);
    }
}
