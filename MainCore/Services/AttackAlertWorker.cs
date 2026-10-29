using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace MainCore.Services;

// Browser tasks enqueue immutable observations and never wait for disk or Twilio.
public sealed class AttackAlertWorker(AttackAlertService alerts, TwilioAttackAlertSettings settings, Func<DateTimeOffset>? clock = null) : BackgroundService
{
    private sealed record VillageSnapshot(int Id, string Name, int X, int Y, bool UnderAttack);
    private sealed record Observation(AccountId AccountId, VillageSnapshot[] Villages, long Sequence, DateTimeOffset Created);
    private readonly Channel<Observation> _observations = Channel.CreateBounded<Observation>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });
    private volatile bool _accepting;
    private readonly object _gate = new();
    private readonly Dictionary<(int Account, int Village), long> _latestClear = [];
    private long _sequence;
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public bool Enabled => settings.Enabled && _accepting;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _accepting = true;
        return base.StartAsync(cancellationToken);
    }

    public bool Publish(AccountId accountId, IReadOnlyCollection<VillageDto> villages)
    {
        if (!_accepting || !settings.Enabled || villages.Count == 0 || accountId == AccountId.Empty) return false;
        var snapshot = villages.Select(v => new VillageSnapshot(v.Id.Value, v.Name ?? "", v.X, v.Y, v.IsUnderAttack)).ToArray();
        lock (_gate)
        {
            if (!_accepting) return false;
            var sequence = ++_sequence;
            if (_observations.Writer.TryWrite(new(accountId, snapshot, sequence, Now)))
            {
                foreach (var village in snapshot.Where(v => !v.UnderAttack)) _latestClear[(accountId.Value, village.Id)] = sequence;
                return true;
            }
        }
        Serilog.Log.Warning("Twilio attack observation queue is full or shutting down; this observation was skipped.");
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var observation in _observations.Reader.ReadAllAsync(stoppingToken))
            {
                VillageDto[] villages;
                lock (_gate)
                {
                    villages = observation.Villages.Select(v =>
                    {
                        var key = (observation.AccountId.Value, v.Id);
                        var superseded = _latestClear.TryGetValue(key, out var clear) && clear > observation.Sequence;
                        if (!superseded && clear != 0 && clear <= observation.Sequence) _latestClear.Remove(key);
                        return new VillageDto { Id = new VillageId(v.Id), Name = v.Name, X = v.X, Y = v.Y, IsUnderAttack = v.UnderAttack && !superseded };
                    }).ToArray();
                }
                var fresh = Now - observation.Created <= TimeSpan.FromMinutes(2);
                try { await alerts.CheckAsync(observation.AccountId, villages, stoppingToken, allowCall: fresh); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { Serilog.Log.Warning("Twilio attack observation failed ({ErrorType}).", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _accepting = false;
        _observations.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}
