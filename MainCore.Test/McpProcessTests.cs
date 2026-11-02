using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Services.ChatControl;

namespace MainCore.Test;

[Collection("MCP bridge")]
public class McpProcessTests
{
    [Fact]
    public async Task StdioToolsCallReadAndWriteThroughRunningBridge()
    {
        using var h = new TaskExecutionHarness(); h.Tasks.SetStatus(new(1), StatusEnums.Paused);
        using (var db = h.Factory.CreateDbContext()) { db.Jobs.Add(new Job { VillageId = 1, Position = 0, Type = JobTypeEnums.NormalBuild, Content = "{}" }); db.SaveChanges(); }
        using var observations = new ObservationTracker(h.Notifications);
        using var bridge = new ControlBridge(new ApplicationControlService(h.Factory, h.Tasks, new ControlMutations(h.Factory, h.Tasks, h.Notifications), observations));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await bridge.StartAsync(ct);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TravBotSharp.sln"))) root = root.Parent;
        root.ShouldNotBeNull();
        var config = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var dll = Path.Combine(root.FullName, "TravianBotSharp.Mcp", "bin", config, "net10.0", "TravianBotSharp.Mcp.dll");
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnet = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet.exe");
        using var process = Process.Start(new ProcessStartInfo(dotnet) { ArgumentList = { dll, "--app-directory", AppContext.BaseDirectory }, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var stderr = process.StandardError.ReadToEndAsync(ct);
        async Task<JsonElement> Call(object request)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            var line = await process.StandardOutput.ReadLineAsync(ct);
            line.ShouldNotBeNull();
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        try
        {
            var init = await Call(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } });
            init.GetProperty("result").GetProperty("instructions").GetString()!.ShouldContain("cached");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            var read = await Call(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "get_build_queue", arguments = new { accountId = 1, villageId = 1 } } });
            var data = read.GetProperty("result").GetProperty("structuredContent").GetProperty("Result").GetProperty("Data");
            var revision = data.GetProperty("Revision").GetString(); var jobId = data.GetProperty("Jobs")[0].GetProperty("Id").GetInt32();
            var write = await Call(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "remove_build_job", arguments = new { accountId = 1, villageId = 1, jobId, expectedRevision = revision, requestId = "stdio-test" } } });
            write.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
            var error = await Call(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "get_village_snapshot", arguments = new { accountId = 1, villageId = 999 } } });
            error.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
            error.GetProperty("result").GetProperty("structuredContent").GetProperty("ErrorCode").GetString().ShouldBe("village_not_found");
            using var verify = h.Factory.CreateDbContext(); verify.Jobs.ShouldBeEmpty();
        }
        finally { process.StandardInput.Close(); if (!process.WaitForExit(5000)) process.Kill(true); await bridge.StopAsync(ct); }
    }
}
