using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TravianBotSharp.ControlProtocol;

namespace MainCore.Services.ChatControl;

public sealed class ControlBridge(ApplicationControlService control) : BackgroundService
{
    private readonly HashSet<Task> _clients = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var instance = new Mutex(false, "Local\\" + PipeProtocol.Name(AppContext.BaseDirectory), out var created);
        if (!created) throw new InvalidOperationException("Another control bridge is already running for this application directory.");
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeProtocol.Name(AppContext.BaseDirectory), PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                var task = Serve(pipe, stoppingToken);
                pipe = null;
                lock (_clients) _clients.Add(task);
                _ = task.ContinueWith(t => { lock (_clients) _clients.Remove(t); }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { pipe?.Dispose(); break; }
            catch (IOException) { pipe?.Dispose(); await Task.Delay(500, stoppingToken); }
        }
        Task[] pending;
        lock (_clients) pending = [.. _clients];
        await Task.WhenAll(pending);
    }
    private async Task Serve(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var request = await PipeProtocol.ReadAsync<ControlRequest>(pipe, timeout.Token);
                var response = request.Version == PipeProtocol.Version
                    ? await control.ExecuteAsync(request.Method, request.Arguments, timeout.Token)
                    : new ControlResponse(PipeProtocol.Version, null, "protocol_version", "Unsupported protocol version.");
                if (JsonSerializer.SerializeToUtf8Bytes(response).Length > PipeProtocol.MaximumBytes)
                    response = new ControlResponse(PipeProtocol.Version, null, "response_too_large", "The response exceeds the bridge size limit; reduce the requested data.");
                await PipeProtocol.WriteAsync(pipe, response, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or OperationCanceledException) { }
            catch (Exception) { try { await PipeProtocol.WriteAsync(pipe, new ControlResponse(PipeProtocol.Version, null, "internal_error", "The application could not process the request."), timeout.Token); } catch (Exception) { } }
        }
    }
}
