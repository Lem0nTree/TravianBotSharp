using System.Collections.Concurrent;
using MainCore.Services.Telegram;

namespace MainCore.Services.ChatControl;

public sealed class ObservationTracker : IDisposable
{
    public sealed record Times(DateTimeOffset? Fields = null, DateTimeOffset? Center = null, DateTimeOffset? Resources = null, DateTimeOffset? Attack = null);
    private readonly ConcurrentDictionary<(int Account, int Village), Times> _times = new();
    private readonly IDisposable _subscription;
    public ObservationTracker(RxQueue notifications) => _subscription = notifications.GetObservable<TelegramVillageScan>().Subscribe(new Observer(Record));
    private void Record(TelegramVillageScan scan)
    {
        var now = DateTimeOffset.UtcNow;
        _times.AddOrUpdate((scan.AccountId.Value, scan.VillageId.Value), _ => Apply(new(), scan, now), (_, old) => Apply(old, scan, now));
    }
    private static Times Apply(Times old, TelegramVillageScan scan, DateTimeOffset now) => old with {
        Fields = scan.Buildings?.Any(x => x.Slot <= 18) == true ? now : old.Fields,
        Center = scan.Buildings?.Any(x => x.Slot > 18) == true ? now : old.Center,
        Resources = scan.Storage is not null ? now : old.Resources,
        Attack = scan.UnderAttack is not null ? now : old.Attack
    };
    public Times Get(int account, int village) => _times.GetValueOrDefault((account, village)) ?? new();
    public void Dispose() => _subscription.Dispose();
    private sealed class Observer(Action<TelegramVillageScan> next) : IObserver<TelegramVillageScan>
    { public void OnNext(TelegramVillageScan value) => next(value); public void OnError(Exception error) { } public void OnCompleted() { } }
}
