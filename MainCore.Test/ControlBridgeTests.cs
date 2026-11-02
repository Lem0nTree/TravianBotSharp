using System.IO.Pipes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MainCore.Infrasturecture.Persistence;
using MainCore.Entities;
using MainCore.Services;
using MainCore.Services.ChatControl;
using TravianBotSharp.ControlProtocol;

namespace MainCore.Test;

[Collection("MCP bridge")]
public class ControlBridgeTests
{
    [Fact]
    public async Task InvalidFrameDoesNotStopListenerAndConcurrentClientsReachSameService()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        factory.CreateDbContext().Returns(_ => new AppDbContext(options));
        using (var db = factory.CreateDbContext()) { db.Database.EnsureCreated(); db.Accounts.Add(new Account { Id = 1, Username = "test", Server = "game.invalid" }); db.SaveChanges(); }
        var notifications = new RxQueue(factory);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var tasks = new TaskManager(notifications, factory, logger);
        using var observations = new ObservationTracker(notifications);
        var mutations = new ControlMutations(factory, tasks, notifications);
        var service = new ApplicationControlService(factory, tasks, mutations, observations);
        using var bridge = new ControlBridge(service);
        var ct = TestContext.Current.CancellationToken;
        await bridge.StartAsync(ct);
        await using (var bad = new NamedPipeClientStream(".", PipeProtocol.Name(AppContext.BaseDirectory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await bad.ConnectAsync(5000, ct);
            await bad.WriteAsync(BitConverter.GetBytes(PipeProtocol.MaximumBytes + 1), ct);
        }
        async Task<ControlResponse> Query()
        {
            await using var pipe = new NamedPipeClientStream(".", PipeProtocol.Name(AppContext.BaseDirectory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000, ct);
            await PipeProtocol.WriteAsync(pipe, new ControlRequest(PipeProtocol.Version, "list_accounts", JsonSerializer.SerializeToElement(new { })), ct);
            return await PipeProtocol.ReadAsync<ControlResponse>(pipe, ct);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Query()));
        results.ShouldAllBe(x => x.ErrorCode == null);
        results.ShouldAllBe(x => JsonSerializer.Serialize(x.Result).Contains("test"));
        await bridge.StopAsync(ct);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }
}


[CollectionDefinition("MCP bridge", DisableParallelization = true)]
public class McpBridgeCollection;
