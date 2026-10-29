using MainCore.DTO;
using MainCore.Entities;
using MainCore.Parsers;
using MainCore.Services;
using MainCore.Test.Parsers;

namespace MainCore.Test;

public class AttackSidebarBrowserTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    [Fact]
    public async Task ActualSidebarJavascriptPreservesFlagsAndScanHookQueuesObservation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twilio-sidebar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new TwilioAttackAlertSettings { Enabled = true, AccountSid = "AC" + new string('a', 32), AuthToken = "fake", FromNumber = "+15005550006", ToNumber = "+393000000000" };
        var calls = new FakeCalls();
        using var worker = new AttackAlertWorker(new AttackAlertService(settings, calls, new AttackAlertStateStore(Path.Combine(directory, "state.json"))), settings);
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await using var context = await CreateContextAsync();
            var page = await context.NewPageAsync();
            await page.SetContentAsync("""
                <div id='sidebarBoxVillageList'>
                  <div class='listEntry village active attack' data-did='42'><a><span class='name'>Home</span></a><span class='coordinateX'>12</span><span class='coordinateY'>-5</span></div>
                  <div class='listEntry village' data-did='43'><a><span class='name'>Other</span></a><span class='coordinateX'>2</span><span class='coordinateY'>3</span></div>
                </div>
                """);
            var parsed = await VillagePanelParser.Get(page);
            parsed.Single(x => x.Id == new VillageId(42)).IsActive.ShouldBeTrue();
            parsed.Single(x => x.Id == new VillageId(42)).IsUnderAttack.ShouldBeTrue();
            parsed.Single(x => x.Id == new VillageId(43)).IsUnderAttack.ShouldBeFalse();
            (await AttackAlertObservation.CaptureAsync(page, new AccountId(1), worker)).ShouldBeTrue();
            var observed = await calls.Received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            observed.Single().Name.ShouldBe("Home");
            observed.Single().Id.ShouldBe(new VillageId(42));
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeCalls : ITwilioCallClient
    {
        public TaskCompletionSource<VillageDto[]> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> CallAsync(IReadOnlyCollection<VillageDto> villages, CancellationToken cancellationToken)
        {
            Received.TrySetResult(villages.ToArray());
            return Task.FromResult("CA-fake");
        }
    }
}
