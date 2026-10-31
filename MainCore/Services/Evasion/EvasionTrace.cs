using System.Collections.Concurrent;

namespace MainCore.Services.Evasion;

public sealed class EvasionTrace(ILogger logger, int account, int village, string attack, string kind, int destination, DateTimeOffset? impact, DateTimeOffset? target)
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RecentRetries = new();
    private string _stage = "start";
    private int _destination = destination;
    private DateTimeOffset? _impact = impact;
    private DateTimeOffset? _target = target;
    public void SetDetails(int destinationId, DateTimeOffset? impactAt, DateTimeOffset? targetAt)
    { _destination = destinationId; _impact = impactAt; _target = targetAt; }
    public void Event(string stage, string reason)
    {
        _stage = stage;
        var now = DateTimeOffset.UtcNow;
        logger.Information("Evasion account={AccountId} village={VillageId} attack={AttackId} kind={Kind} destination={DestinationId} stage={Stage} reason={Reason} impact={Impact} target={Target} actual={Actual} driftSeconds={Drift}",
            account, village, attack, kind, _destination, stage, reason, _impact, _target, now, _target is null ? null : (now - _target.Value).TotalSeconds);
    }
    public void Failure(Exception exception)
    {
        logger.Warning("Evasion account={AccountId} village={VillageId} attack={AttackId} kind={Kind} destination={DestinationId} stage={Stage} reason=exception errorType={ErrorType}",
            account, village, attack, kind, _destination, _stage, exception.GetType().Name);
    }
    public void Retry(string stage, string reason)
    {
        _stage = stage;
        var key = $"{account}:{village}:{attack}:{kind}:{stage}:{reason}";
        var now = DateTimeOffset.UtcNow;
        lock (RecentRetries)
        {
            if (RecentRetries.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
            RecentRetries[key] = now;
        }
        Event(stage, reason);
    }
    public void Resources(MarketplaceAdapter.Prepared prepared)
    {
        _stage = "prepared";
        logger.Information("Evasion account={AccountId} village={VillageId} attack={AttackId} kind=merchant destination={DestinationId} stage=prepared merchants={Merchants} capacityPerMerchant={Each} capacity={Capacity} stocks={@Stocks} selected={@Amounts}",
            account, village, attack, _destination, prepared.Merchants, prepared.CapacityPerMerchant, prepared.Capacity, prepared.Stocks, prepared.Amounts);
    }
}

public static class EvasionDecisionLog
{
    private static readonly ConcurrentDictionary<string, (string Reason, DateTimeOffset At)> Seen = new();
    public static void Scheduling(ILogger logger, int account, int village, string attack, string kind, int destination, DateTimeOffset impact, string reason)
    {
        var key = $"{account}:{village}:{attack}:{kind}";
        var now = DateTimeOffset.UtcNow;
        lock (Seen)
        {
            if (Seen.TryGetValue(key, out var old) && old.Reason == reason && now - old.At < TimeSpan.FromMinutes(2)) return;
            Seen[key] = (reason, now);
            if (Seen.Count > 4096)
                foreach (var expired in Seen.Where(x => now - x.Value.At > TimeSpan.FromHours(1)).Select(x => x.Key).ToArray()) Seen.TryRemove(expired, out _);
        }
        var target = kind == "merchant" ? impact.AddSeconds(-30) : impact - EvasionRules.TroopLead;
        new EvasionTrace(logger, account, village, attack, kind, destination, impact, target).Event("schedule", reason);
    }
}
