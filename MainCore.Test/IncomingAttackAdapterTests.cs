using MainCore.Services.Evasion;
using MainCore.Test.Parsers;

namespace MainCore.Test.Evasion;

public class IncomingAttackAdapterTests(PlaywrightFixture fixture) : ParserTestBase(fixture)
{
    // Sanitized shape from the observed rally DOM. IDs and all text are fictional.
    private const string Raid = """
        <table class='troop_details inRaid'><tbody><tr><td>
          <a class='markAttack'><img id='markSymbol_123' class='markAttack markAttack0' alt='mark attack'></a>
          <div class='infos'><div class='in'><span class='timer' counting='down' value='268' data-value='268'>0:04:28</span></div></div>
        </td></tr></tbody></table>
        """;
    private const string Friendly = """
        <table class='troop_details inSupplyOasis'><tr><td><div class='infos'><div class='in'><span class='timer' counting='down'>0:06:36</span></div></div></td></tr></table>
        <table class='troop_details outSupply'><tr><td><div class='infos'><div class='in'><span class='timer' counting='down'>0:06:36</span></div></div></td></tr></table>
        <table class='troop_details'><tr><td>own troops</td></tr></table>
        """;

    [Fact]
    public async Task ObservedRaidShapeNeedsNoAnchorTitleAndIgnoresFriendlyMovements()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(Raid + Friendly);
        var start = DateTimeOffset.UtcNow;
        var report = await IncomingAttackAdapter.ReadAsync(page);
        var end = DateTimeOffset.UtcNow;
        report.Complete.ShouldBeTrue();
        report.Rows.ShouldBe(4);
        report.HostileRows.ShouldBe(1);
        report.Attacks.Single().Id.ShouldBe("123");
        report.SampledAt.ShouldBeGreaterThanOrEqualTo(start.AddSeconds(-1));
        report.SampledAt.ShouldBeLessThanOrEqualTo(end.AddSeconds(1));
        report.Attacks.Single().ImpactAt.ShouldBe(report.SampledAt.AddSeconds(268));
    }

    [Fact]
    public async Task MultipleWavesNonEnglishBidiAndNestedTableAreReadOnce()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        var nested = Raid.Replace("<div class='infos'>", "<table><tr><td>日本語</td></tr></table><div class='infos'>")
            .Replace("0:04:28", "&#8237;0:04:28&#8236;");
        await page.SetContentAsync(nested + Raid.Replace("markSymbol_123", "markSymbol_124").Replace("0:04:28", "0:04:35"));
        var report = await IncomingAttackAdapter.ReadAsync(page);
        report.Complete.ShouldBeTrue();
        report.Attacks.Select(x => x.Id).ShouldBe(["123", "124"]);
        report.Attacks[1].ImpactAt.ShouldBe(report.SampledAt.AddSeconds(275));
    }

    [Theory]
    [InlineData("markSymbol_bad", "0:04:28", "invalid-marker")]
    [InlineData("markSymbol_123", "0:00:00", "invalid-countdown")]
    [InlineData("markSymbol_123", "unreadable", "invalid-countdown")]
    public async Task AnyMalformedHostileRowRejectsTheWholeSnapshot(string marker, string timer, string reason)
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(Raid + Raid.Replace("markSymbol_123", marker).Replace("0:04:28", timer));
        var report = await IncomingAttackAdapter.ReadAsync(page);
        report.Complete.ShouldBeFalse();
        report.Attacks.ShouldBeEmpty();
        report.Issues.ShouldContain(i => i.Reason == reason);
    }

    [Fact]
    public async Task RejectsDuplicateMarkersMissingTimersUnknownIncomingAndConflictingClasses()
    {
        await using var context = await CreateContextAsync();
        var page = await context.NewPageAsync();
        foreach (var html in new[] {
            Raid + Raid,
            Raid.Replace("counting='down'", "counting='up'"),
            "<table class='troop_details inUnknown'><tr><td>?</td></tr></table>",
            Raid.Replace("troop_details inRaid", "troop_details"),
            Raid.Replace("inRaid", "inRaid inAttack"),
            Raid.Replace("inRaid", "inReturn") })
        {
            await page.SetContentAsync(html);
            (await IncomingAttackAdapter.ReadAsync(page)).Complete.ShouldBeFalse();
        }
    }

    [Fact]
    public void DiagnosticGateLogsFirstChangedAndRecoveryWithoutRepeatingEveryScan()
    {
        var gate = new RallyDiagnosticGate();
        var now = DateTimeOffset.UtcNow;
        var fail = new RallyReport(false, [], 1, 1, [new("invalid-countdown", "troop_details inRaid", ["markSymbol_123"], ["<invalid>"], 1, 1)], now);
        gate.Observe("1:2:scan", fail, now).ShouldBe("first");
        gate.Observe("1:2:scan", fail, now.AddSeconds(10)).ShouldBeNull();
        gate.Observe("1:2:scan", fail with { Issues = [fail.Issues[0] with { Reason = "invalid-marker" }] }, now.AddSeconds(20)).ShouldBe("changed");
        gate.Observe("1:2:scan", fail with { Complete = true, Issues = [] }, now.AddSeconds(30)).ShouldBe("recovered");
    }

    [Fact]
    public void DiagnosticLogPreservesAccountRoutingContext()
    {
        var sink = new CaptureSink();
        using var logger = new Serilog.LoggerConfiguration().Enrich.WithProperty("Account", "owner_server").WriteTo.Sink(sink).CreateLogger();
        var report = new RallyReport(false, [], 1, 1, [new("invalid-marker", "troop_details inRaid", ["<invalid>"], [], 1, 0)], DateTimeOffset.UtcNow);
        RallyDiagnostics.Log(logger, 991234, 999, "scan", report);
        sink.Event.ShouldNotBeNull();
        sink.Event.Properties["Account"].ToString().ShouldBe("\"owner_server\"");
        sink.Event.Properties["AccountId"].ToString().ShouldBe("991234");
    }

    [Fact]
    public void ResourcePreparationLogContainsCorrelationAndObservedCapacity()
    {
        var sink = new CaptureSink();
        using var logger = new Serilog.LoggerConfiguration().Enrich.WithProperty("Account", "owner_server").WriteTo.Sink(sink).CreateLogger();
        var trace = new EvasionTrace(logger, 7, 8, "123", "merchant", 9, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddSeconds(30));
        trace.Resources(new MarketplaceAdapter.Prepared([10, 5, 0, 0], 15, [10, 20, 30, 40], 3, 5));
        sink.Event.ShouldNotBeNull();
        sink.Event.Properties["Account"].ToString().ShouldBe("\"owner_server\"");
        sink.Event.Properties["AccountId"].ToString().ShouldBe("7");
        sink.Event.Properties["AttackId"].ToString().ShouldBe("\"123\"");
        sink.Event.Properties["Merchants"].ToString().ShouldBe("3");
        sink.Event.Properties["Capacity"].ToString().ShouldBe("15");
        sink.Event.Properties.ShouldContainKey("Stocks");
        sink.Event.Properties.ShouldContainKey("Amounts");
    }

    private sealed class CaptureSink : Serilog.Core.ILogEventSink
    {
        public Serilog.Events.LogEvent? Event { get; private set; }
        public void Emit(Serilog.Events.LogEvent logEvent) => Event = logEvent;
    }
}
