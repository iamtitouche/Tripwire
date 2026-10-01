using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class CompositeTriggerTests
{
    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Equal("triggers", Assert.Throws<ArgumentNullException>(() => new CompositeTrigger(null!)).ParamName);
        Assert.Equal("triggers", Assert.Throws<ArgumentException>(() => new CompositeTrigger()).ParamName);
        Assert.Equal("triggers", Assert.Throws<ArgumentException>(() => new CompositeTrigger(new OnStartupTrigger(), null!)).ParamName);
        Assert.Equal("triggers", Assert.Throws<ArgumentException>(() => new CompositeTrigger(null!, new OnStartupTrigger())).ParamName);
    }

    [Fact]
    public async Task Rejects_null_execute_before_starting_any_child()
    {
        // ManualTrigger does not validate execute itself: the composite must enforce the contract.
        var child = new ManualTrigger();
        var trigger = new CompositeTrigger(child);

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => trigger.StartAsync(null!, CancellationToken.None));

        Assert.Equal("execute", ex.ParamName);
        Assert.False(child.Started.IsCompleted);
    }

    [Fact]
    public async Task Relays_firings_from_every_child()
    {
        var a = new ManualTrigger();
        var b = new ManualTrigger();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new CompositeTrigger(a, b).StartAsync(_ => { calls++; return TestFiring.Accepted; }, cts.Token);
        await Task.WhenAll(a.Started, b.Started);

        await a.FireAsync();
        await b.FireAsync();
        await a.FireAsync();

        Assert.Equal(3, calls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Completes_when_all_children_complete()
    {
        var trigger = new CompositeTrigger(new OnStartupTrigger(), new OnStartupTrigger());

        var run = trigger.StartAsync(_ => TestFiring.Accepted, CancellationToken.None);

        await run;
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Completes_cleanly_on_cancellation()
    {
        var a = new ManualTrigger();
        var b = new ManualTrigger();
        using var cts = new CancellationTokenSource();
        var run = new CompositeTrigger(a, b).StartAsync(_ => TestFiring.Accepted, cts.Token);
        await Task.WhenAll(a.Started, b.Started);
        await Wait.SettleAsync();
        Assert.False(run.IsCompleted);   // children are running: the composite must not complete

        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(run.IsCompletedSuccessfully);
        Assert.True(a.Token.IsCancellationRequested);
        Assert.True(b.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Failing_child_cancels_siblings_and_propagates()
    {
        var sibling = new ManualTrigger();
        var failing = new DelegateTrigger(async (_, _) => { await Task.Yield(); throw new InvalidOperationException("boom"); });
        var run = new CompositeTrigger(sibling, failing).StartAsync(_ => TestFiring.Accepted, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);

        Assert.Equal("boom", ex.Message);
        Assert.True(sibling.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Failing_child_waits_for_siblings_to_stop_before_propagating()
    {
        var siblingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = new DelegateTrigger(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            await siblingStopped.Task;   // takes its time to wind down after cancellation
        });
        var failing = new DelegateTrigger(async (_, _) => { await Task.Yield(); throw new InvalidOperationException("boom"); });
        var run = new CompositeTrigger(sibling, failing).StartAsync(_ => TestFiring.Accepted, CancellationToken.None);

        await Wait.SettleAsync();
        Assert.False(run.IsCompleted);

        siblingStopped.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Child_throwing_synchronously_still_cancels_already_started_siblings()
    {
        var sibling = new ManualTrigger();
        var failing = new DelegateTrigger((_, _) => throw new InvalidOperationException("sync boom"));
        var run = new CompositeTrigger(sibling, failing).StartAsync(_ => TestFiring.Accepted, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);

        Assert.True(sibling.Started.IsCompleted);
        Assert.True(sibling.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Failure_is_propagated_immediately_not_at_shutdown()
    {
        var neverEnding = new ManualTrigger();
        var failing = new DelegateTrigger(async (_, _) => { await Task.Yield(); throw new InvalidOperationException("boom"); });
        var run = new CompositeTrigger(neverEnding, failing).StartAsync(_ => TestFiring.Accepted, CancellationToken.None);

        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(run, completed);
    }

    [Fact]
    public async Task Relays_failures_from_execute()
    {
        // A service-level StopOnFailure surfaces through execute, then through the composite.
        var trigger = new CompositeTrigger(new ManualTrigger(), new OnStartupTrigger());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            trigger.StartAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None));
    }
}
