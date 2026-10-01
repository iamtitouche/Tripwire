using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class FixedDelayTriggerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that counts the timers created on it, so that a test can wait for the
    /// trigger to have started its delay before advancing the clock, rather than guess with a real delay.
    /// </summary>
    private sealed class CountingTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _fake = new();
        private int _timers;

        public int TimersCreated => Volatile.Read(ref _timers);

        public override DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

        public override long GetTimestamp() => _fake.GetTimestamp();

        public override long TimestampFrequency => _fake.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => _fake.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            return _fake.CreateTimer(callback, state, dueTime, period);
        }

        public void Advance(TimeSpan delta) => _fake.Advance(delta);
    }

    /// <summary>
    /// <c>execute</c> callback whose firings stay in progress until the test completes them.
    /// </summary>
    private sealed class Iterations
    {
        private TaskCompletionSource? _current;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Drop { get; init; }

        public CancellationToken Token { get; private set; }

        public Task<Firing> ExecuteAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Interlocked.Increment(ref _calls);

            if (Drop)
            {
                return Task.FromResult(Firing.Dropped);
            }

            _current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.FromResult(new Firing(_current.Task));
        }

        public void CompleteCurrent() => _current!.SetResult();
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        var tooLong = TimeSpan.FromDays(50);

        Assert.Equal("delay", Assert.Throws<ArgumentOutOfRangeException>(() => new FixedDelayTrigger(TimeSpan.Zero)).ParamName);
        Assert.Equal("delay", Assert.Throws<ArgumentOutOfRangeException>(() => new FixedDelayTrigger(TimeSpan.FromSeconds(-1))).ParamName);
        Assert.Equal("delay", Assert.Throws<ArgumentOutOfRangeException>(() => new FixedDelayTrigger(tooLong)).ParamName);
        Assert.Equal("initialDelay", Assert.Throws<ArgumentOutOfRangeException>(() => new FixedDelayTrigger(Delay, TimeSpan.FromSeconds(-1))).ParamName);
        Assert.Equal("initialDelay", Assert.Throws<ArgumentOutOfRangeException>(() => new FixedDelayTrigger(Delay, tooLong)).ParamName);
    }

    [Fact]
    public async Task Rejects_null_execute()
    {
        var trigger = new FixedDelayTrigger(Delay, timeProvider: new FakeTimeProvider());

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => trigger.StartAsync(null!, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("execute", ex.ParamName);
    }

    [Fact]
    public async Task First_run_waits_the_delay_by_default()
    {
        var time = new CountingTimeProvider();
        var iterations = new Iterations();
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, timeProvider: time).StartAsync(iterations.ExecuteAsync, cts.Token);

        time.Advance(Delay - Tick);
        await Wait.SettleAsync();
        Assert.Equal(0, iterations.Calls);

        time.Advance(Tick);
        await Wait.UntilAsync(() => iterations.Calls == 1);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Zero_initial_delay_runs_right_away()
    {
        var iterations = new Iterations();
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, TimeSpan.Zero, new FakeTimeProvider()).StartAsync(iterations.ExecuteAsync, cts.Token);

        await Wait.UntilAsync(() => iterations.Calls == 1);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Custom_initial_delay_is_waited_before_the_first_run()
    {
        var time = new CountingTimeProvider();
        var iterations = new Iterations();
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, TimeSpan.FromSeconds(3), time).StartAsync(iterations.ExecuteAsync, cts.Token);

        time.Advance(TimeSpan.FromSeconds(3));
        await Wait.UntilAsync(() => iterations.Calls == 1);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Delay_starts_when_the_iteration_completes()
    {
        var time = new CountingTimeProvider();
        var iterations = new Iterations();
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, TimeSpan.Zero, time).StartAsync(iterations.ExecuteAsync, cts.Token);
        await Wait.UntilAsync(() => iterations.Calls == 1);

        // However long the iteration takes, nothing fires while it runs.
        time.Advance(Delay * 5);
        await Wait.SettleAsync();
        Assert.Equal(1, iterations.Calls);

        iterations.CompleteCurrent();
        await Wait.UntilAsync(() => time.TimersCreated == 1);

        time.Advance(Delay - Tick);
        await Wait.SettleAsync();
        Assert.Equal(1, iterations.Calls);

        time.Advance(Tick);
        await Wait.UntilAsync(() => iterations.Calls == 2);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Dropped_firing_starts_the_next_delay()
    {
        var time = new CountingTimeProvider();
        var iterations = new Iterations { Drop = true };
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, TimeSpan.Zero, time).StartAsync(iterations.ExecuteAsync, cts.Token);
        await Wait.UntilAsync(() => time.TimersCreated == 1);

        time.Advance(Delay);
        await Wait.UntilAsync(() => iterations.Calls == 2);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Hands_its_token_to_execute()
    {
        var iterations = new Iterations();
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, TimeSpan.Zero, new FakeTimeProvider()).StartAsync(iterations.ExecuteAsync, cts.Token);
        await Wait.UntilAsync(() => iterations.Calls == 1);

        Assert.Equal(cts.Token, iterations.Token);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Completes_cleanly_on_cancellation_during_the_delay()
    {
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, timeProvider: new FakeTimeProvider()).StartAsync(_ => TestFiring.Accepted, cts.Token);

        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stops_waiting_for_an_iteration_on_cancellation()
    {
        var iterations = new Iterations();
        using var cts = new CancellationTokenSource();
        var run = new FixedDelayTrigger(Delay, TimeSpan.Zero, new FakeTimeProvider()).StartAsync(iterations.ExecuteAsync, cts.Token);
        await Wait.UntilAsync(() => iterations.Calls == 1);

        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Propagates_exceptions_from_execute()
    {
        var run = new FixedDelayTrigger(Delay, TimeSpan.Zero, new FakeTimeProvider())
            .StartAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Slow_iterations_of_a_service_never_pile_up()
    {
        var time = new CountingTimeProvider();
        var logger = new TestLogger();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var service = new TestService(
            new FixedDelayTrigger(Delay, TimeSpan.Zero, time),
            async _ => { Interlocked.Increment(ref started); await gate.Task; },
            logger: logger);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => Volatile.Read(ref started) == 1);

        time.Advance(Delay * 10);
        await Wait.SettleAsync();
        Assert.Equal(1, Volatile.Read(ref started));

        gate.SetResult();
        await Wait.UntilAsync(() => time.TimersCreated == 1);
        time.Advance(Delay);
        await Wait.UntilAsync(() => Volatile.Read(ref started) == 2);

        await service.StopAsync(CancellationToken.None);
        Assert.Empty(logger.Warnings);
    }
}
