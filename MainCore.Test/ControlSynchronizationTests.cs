using FluentResults;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Errors;
using MainCore.Tasks;
using Microsoft.Playwright;

namespace MainCore.Test;

public sealed class ControlSynchronizationTests
{
    [Fact]
    public async Task ControlLeaseSerializesSameAccountButNotOtherAccountsAndSupportsCancellation()
    {
        using var h = new TaskExecutionHarness();
        using var first = await h.Tasks.AcquireControlAsync(new(1), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var waiting = h.Tasks.AcquireControlAsync(new(1), cancellation.Token);
        waiting.IsCompleted.ShouldBeFalse();
        using var other = await h.Tasks.AcquireControlAsync(new(2), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        first.Dispose(); // Disposing a lease twice must not admit two writers.
        using var second = await h.Tasks.AcquireControlAsync(new(1), TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Tasks.AcquireControlAsync(new(1), cancelled.Token));
    }

    [Fact]
    public async Task SchedulerCannotSelectDuringControlMutationAndRechecksPausedStatus()
    {
        using var h = new TaskExecutionHarness();
        h.QueueBuild();
        using var control = await h.Tasks.AcquireControlAsync(new(1), TestContext.Current.CancellationToken);
        var execution = h.Timer.Execute(new(1));
        execution.IsCompleted.ShouldBeFalse();
        h.Tasks.IsExecuting(new(1)).ShouldBeFalse();
        h.Tasks.SetStatus(new(1), StatusEnums.Paused);
        control.Dispose();
        await execution.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await h.Build.DidNotReceive().HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PauseCancelsWithoutDeadlockAndWaitsForEntireExecution()
    {
        using var h = new TaskExecutionHarness();
        var finished = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken executionToken = default;
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            executionToken = call.Arg<CancellationToken>();
            return new ValueTask<Result>(finished.Task);
        });
        h.QueueBuild();
        var execution = h.Timer.Execute(new(1));
        using var control = await h.Tasks.AcquireControlAsync(new(1), TestContext.Current.CancellationToken);
        var pause = h.Tasks.StopCurrentTask(new(1), TestContext.Current.CancellationToken);
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Pausing);
        executionToken.IsCancellationRequested.ShouldBeTrue();
        pause.IsCompleted.ShouldBeFalse();
        h.Tasks.IsExecuting(new(1)).ShouldBeTrue();
        finished.SetResult(Result.Ok());
        await Task.WhenAll(execution, pause).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
        h.Tasks.IsExecuting(new(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task OutcomeRecoveryRemainsExecutingAndPausePreservesUncertainHold()
    {
        using var h = new TaskExecutionHarness();
        var recovery = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var locator = Substitute.For<ILocator>();
        locator.CountAsync().Returns(recovery.Task);
        h.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(locator);
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Result.Fail(new UncertainTaskOutcome("ambiguous response"))));
        var task = h.QueueBuild();
        var execution = h.Timer.Execute(new(1));
        task.IsQuarantined.ShouldBeTrue();
        h.Tasks.IsExecuting(new(1)).ShouldBeTrue();
        using var control = await h.Tasks.AcquireControlAsync(new(1), TestContext.Current.CancellationToken);
        var pause = h.Tasks.StopCurrentTask(new(1), TestContext.Current.CancellationToken);
        pause.IsCompleted.ShouldBeFalse();
        recovery.SetResult(0);
        await Task.WhenAll(execution, pause).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        task.IsQuarantined.ShouldBeTrue();
        h.Tasks.IsExecuting(new(1)).ShouldBeFalse();
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
    }

    [Fact]
    public async Task CancelledPauseWaitDoesNotClaimQuiescenceOrReleaseTheRunningTask()
    {
        using var h = new TaskExecutionHarness();
        var finished = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Build.HandleAsync(Arg.Any<UpgradeBuildingTask.Task>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result>(finished.Task));
        h.QueueBuild();
        var execution = h.Timer.Execute(new(1));
        using var control = await h.Tasks.AcquireControlAsync(new(1), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var pause = h.Tasks.StopCurrentTask(new(1), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pause);
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Pausing);
        h.Tasks.IsExecuting(new(1)).ShouldBeTrue();
        control.Dispose();
        finished.SetResult(Result.Ok());
        await execution.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        h.Tasks.GetStatus(new(1)).ShouldBe(StatusEnums.Paused);
        h.Tasks.IsExecuting(new(1)).ShouldBeFalse();
    }
}
