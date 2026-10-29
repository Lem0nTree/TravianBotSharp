namespace MainCore.Services.Evasion;

public enum SidebarRefreshStatus { Refreshed, Reused, Failed, WaitingAfterFailure }

// One browser owner runs village tasks serially. This shared gate prevents each
// village from independently reloading the account's already-shared sidebar.
[RegisterSingleton<EvasionSidebarCoordinator>]
public sealed class EvasionSidebarCoordinator(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(120);
    private sealed class State { public DateTimeOffset LastAttempt; public bool Fresh; public bool HasAttempt; }
    private readonly object _gate = new();
    private readonly Dictionary<int, State> _accounts = [];
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public static bool ShouldInspect(bool underAttack, bool unresolvedRecovery) => underAttack || unresolvedRecovery;

    public async Task<SidebarRefreshStatus> EnsureFreshAsync(AccountId accountId, Func<Task<bool>> refresh)
    {
        State state;
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountId.Value, out var existing)) _accounts[accountId.Value] = state = new();
            else state = existing;
            var now = Now;
            if (state.HasAttempt && now < state.LastAttempt) state.LastAttempt = now;
            if (state.HasAttempt && now - state.LastAttempt < PollInterval)
                return state.Fresh ? SidebarRefreshStatus.Reused : SidebarRefreshStatus.WaitingAfterFailure;
            state.HasAttempt = true;
            state.LastAttempt = now;
            state.Fresh = false;
        }
        bool success;
        try { success = await refresh(); }
        catch { success = false; }
        lock (_gate) { state.Fresh = success; state.LastAttempt = Now; }
        return success ? SidebarRefreshStatus.Refreshed : SidebarRefreshStatus.Failed;
    }
}
