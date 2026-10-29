using MainCore.Entities;
using MainCore.Services.Evasion;
using Microsoft.Extensions.DependencyInjection;

namespace MainCore.Test.Evasion;

public class EvasionSidebarCoordinatorTests
{
    [Fact]
    public void AccountRefreshGateIsRegisteredAsOneSharedSingleton()
    {
        var services = new ServiceCollection();
        services.AddMainCore();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<EvasionSidebarCoordinator>().ShouldBeSameAs(provider.GetRequiredService<EvasionSidebarCoordinator>());
    }
    [Fact]
    public async Task ThreeIdleVillagesShareOneRefreshAndDoNotNavigate()
    {
        var clock = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var coordinator = new EvasionSidebarCoordinator(() => clock);
        var refreshes = 0; var switches = 0; var rallies = 0;
        async Task Scan(bool attack, bool recovery)
        {
            var status = await coordinator.EnsureFreshAsync(new AccountId(1), () => { refreshes++; return Task.FromResult(true); });
            if (status is SidebarRefreshStatus.Failed or SidebarRefreshStatus.WaitingAfterFailure) return;
            if (EvasionSidebarCoordinator.ShouldInspect(attack, recovery)) { switches++; rallies++; }
        }
        await Scan(false, false); await Scan(false, false); await Scan(false, false);
        refreshes.ShouldBe(1); switches.ShouldBe(0); rallies.ShouldBe(0);
        await Scan(true, false); // A different village still gets inspected during the shared interval.
        switches.ShouldBe(1); rallies.ShouldBe(1); refreshes.ShouldBe(1);
        await Scan(false, true); // A pending recovery also needs its rally point.
        switches.ShouldBe(2); rallies.ShouldBe(2); refreshes.ShouldBe(1);
        clock = clock.AddSeconds(10);
        await Scan(false, false);
        refreshes.ShouldBe(2);
    }

    [Fact]
    public async Task FailedRefreshIsThrottledAndNeverTreatedAsFresh()
    {
        var clock = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var coordinator = new EvasionSidebarCoordinator(() => clock);
        var attempts = 0;
        Task<bool> Fail() { attempts++; return Task.FromResult(false); }
        (await coordinator.EnsureFreshAsync(new(1), Fail)).ShouldBe(SidebarRefreshStatus.Failed);
        (await coordinator.EnsureFreshAsync(new(1), Fail)).ShouldBe(SidebarRefreshStatus.WaitingAfterFailure);
        attempts.ShouldBe(1);
        clock = clock.AddSeconds(10);
        (await coordinator.EnsureFreshAsync(new(1), () => { attempts++; return Task.FromResult(true); })).ShouldBe(SidebarRefreshStatus.Refreshed);
        (await coordinator.EnsureFreshAsync(new(1), Fail)).ShouldBe(SidebarRefreshStatus.Reused);
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task AccountsHaveIndependentRefreshIntervals()
    {
        var coordinator = new EvasionSidebarCoordinator(() => DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var attempts = 0;
        Task<bool> Refresh() { attempts++; return Task.FromResult(true); }
        (await coordinator.EnsureFreshAsync(new(1), Refresh)).ShouldBe(SidebarRefreshStatus.Refreshed);
        (await coordinator.EnsureFreshAsync(new(2), Refresh)).ShouldBe(SidebarRefreshStatus.Refreshed);
        (await coordinator.EnsureFreshAsync(new(1), Refresh)).ShouldBe(SidebarRefreshStatus.Reused);
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task SlowRefreshAndClockAdjustmentDoNotCauseAnImmediateSecondReload()
    {
        var clock = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var coordinator = new EvasionSidebarCoordinator(() => clock);
        var attempts = 0;
        (await coordinator.EnsureFreshAsync(new(1), () => { attempts++; clock = clock.AddSeconds(15); return Task.FromResult(true); })).ShouldBe(SidebarRefreshStatus.Refreshed);
        (await coordinator.EnsureFreshAsync(new(1), () => { attempts++; return Task.FromResult(true); })).ShouldBe(SidebarRefreshStatus.Reused);
        clock = clock.AddMinutes(-1);
        (await coordinator.EnsureFreshAsync(new(1), () => { attempts++; return Task.FromResult(true); })).ShouldBe(SidebarRefreshStatus.Reused);
        attempts.ShouldBe(1);
    }
}
