using System.Collections.Concurrent;
using Tripwire.Internal;
using Tripwire.Tests.Support;

namespace Tripwire.Tests.Internal;

public class IterationDispatcherTests
{
    private sealed class Harness : IDisposable
    {
        public InFlightTracker InFlight { get; } = new();

        public TestLogger Logger { get; } = new();

        public List<Exception> Fatal { get; } = [];

        public IterationDispatcher Dispatcher { get; }

        public Harness(OverlapPolicy policy, Func<CancellationToken, Task> iteration)
        {
            Dispatcher = new IterationDispatcher(policy, InFlight, iteration, Fatal.Add, Logger, "Svc");
        }

        public Task DispatchAsync(CancellationToken pendingToken = default)
        {
            return Dispatcher.DispatchAsync(CancellationToken.None, pendingToken);
        }

        public Task WhenEmptyAsync()
        {
            return InFlight.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
        }
    }

    /// <summary>
    /// Iteration body that blocks on a gate and counts its starts, to control iteration lifetimes from the test.
    /// </summary>
    private sealed class Blocking
    {
        private int _started;

        public TaskCompletionSource Gate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started => _started;

        public async Task RunAsync(CancellationToken _)
        {
            Interlocked.Increment(ref _started);
            await Gate.Task;
        }

        public void ReleaseAll()
        {
            Gate.TrySetResult();
        }

        /// <summary>
        /// Releases the iterations blocked so far and arms a new gate for the next ones.
        /// </summary>
        public void ReleaseCurrent()
        {
            var gate = Gate;
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
        }
    }

    // --- Return contract --------------------------------------------------------------------------

    [Fact]
    public async Task Returns_as_soon_as_the_iteration_has_started()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait, body.RunAsync);

        var firing = h.DispatchAsync();

        Assert.True(firing.IsCompletedSuccessfully);
        await Wait.UntilAsync(() => body.Started == 1);

        body.ReleaseAll();
        await h.WhenEmptyAsync();
    }

    [Fact]
    public async Task Tracks_every_iteration_until_it_completes()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait, body.RunAsync);

        await h.DispatchAsync();
        var empty = h.InFlight.WhenEmptyAsync();
        await Wait.SettleAsync();
        Assert.False(empty.IsCompleted);

        body.ReleaseAll();
        await empty.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Reports_the_iteration_exception_instead_of_throwing_and_releases_the_slot()
    {
        var calls = 0;
        using var h = new Harness(OverlapPolicy.Wait, _ => { calls++; throw new InvalidOperationException("boom"); });

        await h.DispatchAsync();
        await h.WhenEmptyAsync();
        await h.DispatchAsync();
        await h.WhenEmptyAsync();

        Assert.Equal(2, calls);
        Assert.Equal(2, h.Fatal.Count);
        Assert.All(h.Fatal, ex => Assert.Equal("boom", ex.Message));
    }

    // --- Wait: unbounded queue ------------------------------------------------------------------

    [Fact]
    public async Task Wait_queues_firings_and_runs_them_one_at_a_time()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait, body.RunAsync);

        await h.DispatchAsync();
        await h.DispatchAsync();
        await h.DispatchAsync();
        await Wait.UntilAsync(() => body.Started == 1);
        await Wait.SettleAsync();
        Assert.Equal(1, body.Started);

        body.ReleaseCurrent();
        await Wait.UntilAsync(() => body.Started == 2);
        await Wait.SettleAsync();
        Assert.Equal(2, body.Started);

        body.ReleaseCurrent();
        await Wait.UntilAsync(() => body.Started == 3);

        body.ReleaseAll();
        await h.WhenEmptyAsync();
        Assert.Empty(h.Fatal);
    }

    [Fact]
    public async Task Wait_runs_queued_firings_in_order()
    {
        var order = new List<int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = 0;
        using var h = new Harness(OverlapPolicy.Wait, async _ =>
        {
            var id = Interlocked.Increment(ref next);
            if (id == 1) await gate.Task;   // holds the slot while the others queue up
            lock (order) order.Add(id);
        });

        for (var i = 0; i < 5; i++)
        {
            await h.DispatchAsync();
        }

        gate.SetResult();
        await h.WhenEmptyAsync();

        Assert.Equal([1, 2, 3, 4, 5], order);
    }

    [Fact]
    public async Task Wait_logs_a_warning_with_the_queue_depth_each_time_a_firing_has_to_wait()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait, body.RunAsync);

        await h.DispatchAsync();
        await h.DispatchAsync();
        await h.DispatchAsync();

        var warnings = h.Logger.Warnings.Select(e => e.Message).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("queued: 1 firing(s) waiting", warnings[0]);
        Assert.Contains("queued: 2 firing(s) waiting", warnings[1]);
        Assert.Empty(h.Logger.Informations);

        body.ReleaseAll();
        await h.WhenEmptyAsync();
    }

    [Fact]
    public async Task Wait_does_not_log_when_a_slot_is_free()
    {
        using var h = new Harness(OverlapPolicy.Wait, _ => Task.CompletedTask);

        await h.DispatchAsync();
        await h.WhenEmptyAsync();
        await h.DispatchAsync();
        await h.WhenEmptyAsync();

        Assert.Empty(h.Logger.Entries);
    }

    // --- Skip and bounded queues ----------------------------------------------------------------

    [Fact]
    public async Task Skip_drops_a_firing_while_an_iteration_is_running_and_logs_it()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Skip, body.RunAsync);

        await h.DispatchAsync();
        await Wait.UntilAsync(() => body.Started == 1);
        var skipped = h.DispatchAsync();

        Assert.True(skipped.IsCompletedSuccessfully);
        Assert.Equal(1, body.Started);
        Assert.Contains("skipped", Assert.Single(h.Logger.Informations).Message);
        Assert.Empty(h.Logger.Warnings);

        body.ReleaseAll();
        await h.WhenEmptyAsync();
        await h.DispatchAsync();
        await Wait.UntilAsync(() => body.Started == 2);
        body.ReleaseAll();
        await h.WhenEmptyAsync();
    }

    [Fact]
    public async Task Bounded_queue_accepts_up_to_MaxPending_then_drops()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait.WithMaxPending(2), body.RunAsync);

        await h.DispatchAsync();            // runs
        await h.DispatchAsync();            // queued 1/2
        await h.DispatchAsync();            // queued 2/2
        await h.DispatchAsync();            // dropped
        await h.DispatchAsync();            // dropped
        await Wait.UntilAsync(() => body.Started == 1);

        body.ReleaseCurrent();
        await Wait.UntilAsync(() => body.Started == 2);
        body.ReleaseCurrent();
        await Wait.UntilAsync(() => body.Started == 3);
        body.ReleaseAll();
        await h.WhenEmptyAsync();

        Assert.Equal(3, body.Started);
    }

    [Fact]
    public async Task Bounded_queue_logs_information_when_queued_and_a_warning_when_dropped()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait.WithMaxPending(1), body.RunAsync);

        await h.DispatchAsync();
        await h.DispatchAsync();
        await h.DispatchAsync();

        var queued = Assert.Single(h.Logger.Informations);
        Assert.Contains("queued: 1/1 firing(s) waiting", queued.Message);
        var dropped = Assert.Single(h.Logger.Warnings);
        Assert.Contains("dropped: 1 firing(s) already waiting", dropped.Message);

        body.ReleaseAll();
        await h.WhenEmptyAsync();
    }

    [Fact]
    public async Task Bounded_queue_frees_a_pending_slot_once_the_firing_starts()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait.WithMaxPending(1), body.RunAsync);

        await h.DispatchAsync();            // runs
        await h.DispatchAsync();            // queued
        await h.DispatchAsync();            // dropped
        await Wait.UntilAsync(() => body.Started == 1);

        body.ReleaseCurrent();              // the queued firing starts: the pending slot is free again
        await Wait.UntilAsync(() => body.Started == 2);
        await h.DispatchAsync();            // queued, not dropped

        body.ReleaseCurrent();
        await Wait.UntilAsync(() => body.Started == 3);
        body.ReleaseAll();
        await h.WhenEmptyAsync();

        Assert.Equal(3, body.Started);
        Assert.Single(h.Logger.Warnings);
    }

    [Fact]
    public async Task MaxPending_applies_on_top_of_the_concurrency_limit()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Concurrent(2).WithMaxPending(0), body.RunAsync);

        await h.DispatchAsync();
        await h.DispatchAsync();
        await h.DispatchAsync();            // dropped: both slots busy, nothing may wait
        await Wait.UntilAsync(() => body.Started == 2);
        await Wait.SettleAsync();

        Assert.Equal(2, body.Started);
        Assert.Contains("2 iteration(s) already running", Assert.Single(h.Logger.Informations).Message);

        body.ReleaseAll();
        await h.WhenEmptyAsync();
    }

    // --- Concurrent -----------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_runs_up_to_the_limit_then_queues()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Concurrent(2), body.RunAsync);

        await h.DispatchAsync();
        await h.DispatchAsync();
        await Wait.UntilAsync(() => body.Started == 2);

        var third = h.DispatchAsync();
        Assert.True(third.IsCompletedSuccessfully);
        await Wait.SettleAsync();
        Assert.Equal(2, body.Started);
        Assert.Contains("queued", Assert.Single(h.Logger.Warnings).Message);

        body.ReleaseCurrent();
        await Wait.UntilAsync(() => body.Started == 3);
        body.ReleaseAll();
        await h.WhenEmptyAsync();
    }

    [Fact]
    public async Task Concurrent_releases_the_slot_when_the_iteration_completes()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Concurrent(1), body.RunAsync);

        await h.DispatchAsync();
        body.ReleaseCurrent();
        await h.WhenEmptyAsync();

        await h.DispatchAsync();
        await Wait.UntilAsync(() => body.Started == 2);
        Assert.Empty(h.Logger.Entries);   // a leaked slot would have queued the second firing

        body.ReleaseAll();
        await h.WhenEmptyAsync();
    }

    // --- Pending token --------------------------------------------------------------------------

    [Fact]
    public async Task Already_cancelled_pending_token_aborts_without_running()
    {
        var calls = 0;
        using var h = new Harness(OverlapPolicy.Wait, _ => { calls++; return Task.CompletedTask; });
        using var pending = new CancellationTokenSource();
        await pending.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.DispatchAsync(pending.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Already_cancelled_stopping_token_aborts_without_running()
    {
        // A trigger that fires after shutdown must not start an iteration, even with a free slot.
        var calls = 0;
        using var h = new Harness(OverlapPolicy.Wait, _ => { calls++; return Task.CompletedTask; });
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Dispatcher.DispatchAsync(stopping.Token, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Stopping_token_cancelled_while_waiting_for_a_slot_aborts_without_running()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait, body.RunAsync);
        using var stopping = new CancellationTokenSource();

        await h.Dispatcher.DispatchAsync(stopping.Token, CancellationToken.None);
        await Wait.UntilAsync(() => body.Started == 1);
        await h.Dispatcher.DispatchAsync(stopping.Token, CancellationToken.None);   // queued

        await stopping.CancelAsync();
        body.ReleaseAll();
        await h.WhenEmptyAsync();

        Assert.Equal(1, body.Started);
    }

    [Fact]
    public async Task Pending_token_cancelled_while_waiting_for_a_slot_aborts_without_running()
    {
        var body = new Blocking();
        using var h = new Harness(OverlapPolicy.Wait.WithMaxPending(1), body.RunAsync);
        using var pending = new CancellationTokenSource();

        await h.DispatchAsync();
        await Wait.UntilAsync(() => body.Started == 1);
        await h.DispatchAsync(pending.Token);

        await pending.CancelAsync();
        // The cancelled firing gives its place back on a thread pool continuation: wait for it rather
        // than for a fixed delay, which is not enough on a loaded CI runner.
        await Wait.UntilAsync(() => h.Dispatcher.PendingCount == 0);

        // The pending slot was given back: another firing can be queued instead of dropped.
        await h.DispatchAsync();
        Assert.Empty(h.Logger.Warnings);

        body.ReleaseCurrent();
        await Wait.UntilAsync(() => body.Started == 2);
        body.ReleaseAll();
        await h.WhenEmptyAsync();
        Assert.Equal(2, body.Started);
    }

    [Fact]
    public async Task Pending_token_cancelled_right_before_the_slot_is_released_still_aborts()
    {
        var rounds = new ConcurrentQueue<RoundContext>();
        var started = 0;

        using var h = new Harness(OverlapPolicy.Wait, async _ =>
        {
            Assert.True(rounds.TryDequeue(out var context));

            Interlocked.Increment(ref started);

            await context.Gate.Task;

            context.Pending?.Cancel();
        });

        for (var round = 1; round <= 20; round++)
        {
            var gate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var pending = new CancellationTokenSource();

            rounds.Enqueue(new RoundContext(gate, pending));

            await h.DispatchAsync();
            await Wait.UntilAsync(() => Volatile.Read(ref started) == round);

            await h.DispatchAsync(pending.Token);
            await Wait.SettleAsync(10);

            gate.SetResult();
            await h.WhenEmptyAsync();

            Assert.Equal(round, started);
        }

        // Every slot acquired by an aborted firing was given back:
        // a plain firing still runs.
        var finalGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        rounds.Enqueue(new RoundContext(finalGate, null));

        await h.DispatchAsync();
        await Wait.UntilAsync(() => Volatile.Read(ref started) == 21);
        
        finalGate.SetResult();
        await h.WhenEmptyAsync();
    }

    private sealed record RoundContext(
        TaskCompletionSource Gate,
        CancellationTokenSource? Pending);

    // --- Disposal -------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_rejects_further_firings()
    {
        var h = new Harness(OverlapPolicy.Wait, _ => Task.CompletedTask);

        h.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.DispatchAsync());
    }
}
