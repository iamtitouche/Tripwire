using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class PeriodicTriggerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodicTrigger(TimeSpan.Zero)).ParamName);
        Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodicTrigger(TimeSpan.FromSeconds(-1))).ParamName);
    }

    [Fact]
    public async Task Rejects_null_execute()
    {
        var trigger = new PeriodicTrigger(Interval, new FakeTimeProvider());

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => trigger.StartAsync(null!, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("execute", ex.ParamName);
    }

    [Fact]
    public async Task Does_not_fire_before_the_first_interval()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new PeriodicTrigger(Interval, time).StartAsync(_ => { calls++; return Task.CompletedTask; }, cts.Token);

        time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        await Wait.SettleAsync();

        Assert.Equal(0, calls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Fires_once_per_interval()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new PeriodicTrigger(Interval, time).StartAsync(_ => { calls++; return Task.CompletedTask; }, cts.Token);

        for (var expected = 1; expected <= 3; expected++)
        {
            time.Advance(Interval);
            await Wait.UntilAsync(() => calls == expected);
        }

        cts.Cancel();
        await run;
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Completes_cleanly_on_cancellation()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var run = new PeriodicTrigger(Interval, time).StartAsync(_ => Task.CompletedTask, cts.Token);

        cts.Cancel();

        await run;
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Propagates_exceptions_from_execute()
    {
        var time = new FakeTimeProvider();
        var run = new PeriodicTrigger(Interval, time).StartAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);

        time.Advance(Interval);

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }
}
