using MainCore.Tasks.Base;

namespace MainCore.Services.Evasion;

// Only actual evasion work implements these markers. Scan tasks do not reserve
// the browser while waiting; deadline tasks reserve it shortly before execution.
public interface IEvasionTask { }
public interface IEvasionDeadlineTask : IEvasionTask { }

public static class EvasionScheduling
{
    public static BaseTask? Select(IEnumerable<BaseTask> tasks, DateTime now, TimeSpan reservation)
    {
        var snapshot = tasks.ToArray();
        var login = snapshot.OfType<LoginTask.Task>().Where(x => x.ExecuteAt <= now).OrderBy(x => x.ExecuteAt).FirstOrDefault();
        if (login is not null) return login;
        var deadlines = snapshot.OfType<IEvasionDeadlineTask>().Cast<BaseTask>().OrderBy(x => x.ExecuteAt).ToArray();
        var deadline = deadlines.FirstOrDefault();
        if (deadline is not null && deadline.ExecuteAt <= now) return deadline;
        var emergency = snapshot.OfType<IEvasionTask>().Cast<BaseTask>()
            .Where(x => x.ExecuteAt <= now).OrderBy(x => x.ExecuteAt).FirstOrDefault();
        // Keep scans running during the wider normal-task reservation, but reserve
        // the final five seconds for the deadline itself.
        if (emergency is not null && (deadline is null || deadline.ExecuteAt > now.AddSeconds(5))) return emergency;
        if (deadline is not null && deadline.ExecuteAt <= now + reservation) return null;
        return snapshot.Where(x => x is not IEvasionTask && x.ExecuteAt <= now).OrderBy(x => x.ExecuteAt).FirstOrDefault();
    }
}
