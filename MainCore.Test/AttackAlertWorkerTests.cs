using System.Net;
using System.Text.Json;
using MainCore.DTO;
using MainCore.Entities;
using MainCore.Services;
using Microsoft.Playwright;

namespace MainCore.Test;

public sealed class AttackAlertWorkerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tbs-twilio-worker-" + Guid.NewGuid().ToString("N"));
    private static TwilioAttackAlertSettings Settings() => new() { Enabled = true, AccountSid = "AC" + new string('a', 32), AuthToken = "fake-token", FromNumber = "+15005550006", ToNumber = "+393000000000" };
    public AttackAlertWorkerTests() => Directory.CreateDirectory(_directory);
    private AttackAlertWorker Create(GatedCalls calls, Func<DateTimeOffset>? clock = null)
    {
        var settings = Settings();
        return new(new AttackAlertService(settings, calls, new AttackAlertStateStore(Path.Combine(_directory, "state.json"))), settings, clock);
    }

    [Fact]
    public async Task QueuedClearSupersedesOlderAttackWithoutLosingARearmedEpisode()
    {
        var calls = new GatedCalls();
        using var worker = Create(calls);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            worker.Publish(new(1), [new VillageDto { Id = new(1), Name = "Blocking", IsUnderAttack = true }]);
            await calls.FirstReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            worker.Publish(new(1), [new VillageDto { Id = new(2), Name = "Old", IsUnderAttack = true }]);
            worker.Publish(new(1), [new VillageDto { Id = new(2), Name = "Clear", IsUnderAttack = false }]);
            worker.Publish(new(1), [new VillageDto { Id = new(2), Name = "Fresh", IsUnderAttack = true }]);
            calls.Release.TrySetResult();
            await calls.SecondReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            calls.Count.ShouldBe(2);
            calls.SecondName.ShouldBe("Fresh");
        }
        finally { calls.Release.TrySetResult(); await worker.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task ExpiredObservationUpdatesDeduplicationWithoutPlacingACall()
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        var calls = new GatedCalls();
        using var worker = Create(calls, () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero));
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            worker.Publish(new(1), [new VillageDto { Id = new(1), Name = "Blocking", IsUnderAttack = true }]);
            await calls.FirstReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            worker.Publish(new(1), [new VillageDto { Id = new(2), Name = "Stale", IsUnderAttack = true }]);
            Interlocked.Add(ref ticks, TimeSpan.FromMinutes(3).Ticks);
            worker.Publish(new(1), [new VillageDto { Id = new(3), Name = "Fresh", IsUnderAttack = true }]);
            calls.Release.TrySetResult();
            await calls.SecondReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            calls.Count.ShouldBe(2);
            calls.SecondName.ShouldBe("Fresh");
            var state = File.ReadAllText(Path.Combine(_directory, "state.json"));
            state.ShouldContain("2");
        }
        finally { calls.Release.TrySetResult(); await worker.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task OrderedQueueDoesNotBlockBrowserAndSnapshotsAreCopied()
    {
        var calls = new GatedCalls();
        using var worker = Create(calls);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            calls.Count.ShouldBe(0); // Starting the worker itself never places a call.
            var village = new VillageDto { Id = new(1), Name = "Initial", IsUnderAttack = true };
            worker.Publish(new(1), [village]).ShouldBeTrue();
            await calls.FirstReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            // These writes complete synchronously even while the first call is blocked.
            worker.Publish(new(1), [new VillageDto { Id = new(1), Name = "Clear", IsUnderAttack = false }]).ShouldBeTrue();
            worker.Publish(new(1), [village]).ShouldBeTrue();
            village.IsUnderAttack = false;
            village.Name = "Changed after enqueue";
            calls.Release.TrySetResult();
            await calls.SecondReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            calls.Count.ShouldBe(2);
            calls.SecondName.ShouldBe("Initial");
        }
        finally
        {
            calls.Release.TrySetResult();
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task EvasionScanHookReadsActualSidebarFieldsAndQueuesAttack()
    {
        var calls = new GatedCalls();
        using var worker = Create(calls);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var page = Substitute.For<IPage>();
            using var json = JsonDocument.Parse("[{\"IdStr\":\"42\",\"Name\":\"Home\",\"CoordinateX\":\"1\",\"CoordinateY\":\"-2\",\"Active\":true,\"UnderAttack\":true}]");
            page.EvaluateAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>()).Returns(json.RootElement.Clone());
            (await AttackAlertObservation.CaptureAsync(page, new AccountId(1), worker)).ShouldBeTrue();
            await calls.FirstReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            calls.Count.ShouldBe(1);
        }
        finally
        {
            calls.Release.TrySetResult();
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class GatedCalls : ITwilioCallClient
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public string? SecondName { get; private set; }
        public TaskCompletionSource FirstReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CallAsync(IReadOnlyCollection<VillageDto> villages, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _count);
            if (number == 1) { FirstReceived.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            else { SecondName = villages.Single().Name; SecondReceived.TrySetResult(); }
            return "CA-fake";
        }
    }
}

public sealed class TwilioCallClientTests
{
    [Fact]
    public async Task PostsVoiceRequestWithEncodedMessageToTwilioUsingMockTransport()
    {
        var handler = new CaptureHandler();
        var settings = new TwilioAttackAlertSettings { Enabled = true, AccountSid = "AC" + new string('a', 32), AuthToken = "fake-token", FromNumber = "+15005550006", ToNumber = "+393000000000" };
        using var client = new TwilioCallClient(new HttpClient(handler), settings);
        var sid = await client.CallAsync([new VillageDto { Id = new(1), Name = "A & B", X = 3, Y = -4, IsUnderAttack = true }], TestContext.Current.CancellationToken);
        sid.ShouldBe("CAfake");
        handler.Method.ShouldBe(HttpMethod.Post);
        handler.Uri!.Host.ShouldBe("api.twilio.com");
        handler.AuthScheme.ShouldBe("Basic");
        var decoded = Uri.UnescapeDataString(handler.Body!.Replace("+", " "));
        decoded.ShouldContain("A &amp; B");
        decoded.ShouldContain("TimeLimit=60");
        decoded.ShouldContain("Timeout=30");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? AuthScheme { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method; Uri = request.RequestUri; AuthScheme = request.Headers.Authorization?.Scheme;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"sid\":\"CAfake\"}") };
        }
    }
}

public class StartupPathTests
{
    [Fact]
    public void DatabasePathUsesExecutableDirectoryRatherThanLauncherDirectory()
    {
        var executableDirectory = Path.Combine(Path.GetTempPath(), "installed bot");
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(MainCore.AppMixins.DatabaseConnectionString(executableDirectory));
        connection.DataSource.ShouldBe(Path.Combine(Path.GetFullPath(executableDirectory), "TBS.db"));
        Path.IsPathFullyQualified(connection.DataSource).ShouldBeTrue();
    }
}
