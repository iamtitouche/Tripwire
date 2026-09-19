using Tripwire.Internal;
using Tripwire.Tests.Support;

namespace Tripwire.Tests.Internal;

[Collection(ExitCodeCollection.Name)]
public class FailureHandlerTests
{
    /// <summary>
    /// Drives a <see cref="FailureHandler"/> without a service: <see cref="FireAsync"/> plays a regular
    /// trigger, and retries are dispatched straight back into the handler, recording the pending token.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private int _attempts;
        private int _retriesCompleted;

        public FakeTimeProvider Time { get; } = new();

        public TestLogger Logger { get; } = new();

        public InFlightTracker InFlight { get; } = new();

        private FailureHandler Handler { get; }

        public Func<int, Task> Body { get; set; } = _ => Task.CompletedTask;

        public Exception? DispatchFailure { get; set; }

        /// <summary>
        /// Simulates a dispatcher that queues the retry: the dispatch returns at once without running it.
        /// </summary>
        public bool QueueRetries { get; init; }

        public int Attempts => _attempts;

        public int RetriesDispatched { get; private set; }

        public CancellationToken LastPendingToken { get; private set; }

        public Harness(FailureHandlingMode mode)
        {
            Handler = new FailureHandler(mode, Time, Logger, "Svc", InFlight, DispatchRetryAsync);
        }

        public Task FireAsync() => Handler.RunAsync(Iteration, _stopping.Token);

        /// <summary>
        /// Waits until <paramref name="count"/> retries have fully completed, including the scheduling
        /// of the next one. Waiting on the attempt counter alone would race with that scheduling.
        /// </summary>
        public Task WaitForRetriesAsync(int count) => Wait.UntilAsync(() => Volatile.Read(ref _retriesCompleted) >= count);

        public void Stop() => _stopping.Cancel();

        public void Dispose() => _stopping.Dispose();

        private Task Iteration(CancellationToken cancellationToken) => Body(Interlocked.Increment(ref _attempts));

        private async Task DispatchRetryAsync(CancellationToken cancellationToken, CancellationToken pendingToken)
        {
            pendingToken.ThrowIfCancellationRequested();
            RetriesDispatched++;
            LastPendingToken = pendingToken;

            if (DispatchFailure is not null)
            {
                throw DispatchFailure;
            }

            if (QueueRetries)
            {
                return;
            }

            await Handler.RunAsync(Iteration, cancellationToken);
            Interlocked.Increment(ref _retriesCompleted);
        }
    }

    private static Func<int, Task> FailsUntil(int attempt) =>
        n => n < attempt ? throw new InvalidOperationException($"boom {n}") : Task.CompletedTask;

    private static Func<int, Task> AlwaysFails() =>
        n => throw new InvalidOperationException($"boom {n}");

    // --- Ignore ---------------------------------------------------------------------------------

    [Fact]
    public async Task Ignore_logs_the_error_with_a_constant_template_and_does_not_throw()
    {
        using var h = new Harness(FailureHandlingMode.Ignore);
        h.Body = AlwaysFails();

        await h.FireAsync();
        await h.FireAsync();

        Assert.Equal(2, h.Attempts);
        Assert.Equal(2, h.Logger.Errors.Count());
        Assert.All(h.Logger.Errors, e => Assert.Equal("Iteration of Svc failed.", e.Message));
        Assert.All(h.Logger.Errors, e => Assert.IsType<InvalidOperationException>(e.Exception));
        Assert.Equal(0, h.RetriesDispatched);
    }

    // --- StopOnFailure --------------------------------------------------------------------------

    [Fact]
    public async Task StopOnFailure_logs_sets_the_exit_code_and_rethrows()
    {
        using var h = new Harness(FailureHandlingMode.StopOnFailure);
        h.Body = AlwaysFails();
        Environment.ExitCode = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(h.FireAsync);

        Assert.Equal("boom 1", ex.Message);
        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains("exit code 1", Assert.Single(h.Logger.Errors).Message);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task StopOnFailure_can_use_a_custom_exit_code()
    {
        using var h = new Harness(FailureHandlingMode.StopOnFailureWithExitCode(42));
        h.Body = AlwaysFails();
        Environment.ExitCode = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(h.FireAsync);

        Assert.Equal(42, Environment.ExitCode);
        Assert.Contains("exit code 42", Assert.Single(h.Logger.Errors).Message);
        Environment.ExitCode = 0;
    }

    // --- Cancellation ---------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_of_the_stopping_token_is_not_a_failure()
    {
        using var h = new Harness(FailureHandlingMode.StopOnFailure);
        h.Body = _ => throw new OperationCanceledException();
        h.Stop();

        await h.FireAsync();

        Assert.Empty(h.Logger.Entries);
    }

    [Fact]
    public async Task Cancellation_does_not_close_an_open_retry_cycle()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)));
        h.Body = n => n == 1 ? throw new InvalidOperationException("boom") : throw new OperationCanceledException();

        await h.FireAsync();   // opens a cycle
        h.Stop();
        await h.FireAsync();   // cancelled: neither a success nor a failure

        Assert.Empty(h.Logger.Informations);
        Assert.Single(h.Logger.Warnings);
    }

    [Fact]
    public async Task OperationCanceledException_without_cancellation_is_a_failure()
    {
        // An internal timeout (OCE from another token) is not a host cancellation.
        using var h = new Harness(FailureHandlingMode.Ignore);
        h.Body = _ => throw new OperationCanceledException("internal timeout");

        await h.FireAsync();

        Assert.Single(h.Logger.Errors);
    }

    // --- Retry: basic cycle ---------------------------------------------------------------------

    [Fact]
    public async Task Retry_retries_after_the_configured_delays_then_closes_the_cycle()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1), backoffFactor: 2));
        h.Body = FailsUntil(3);

        await h.FireAsync();
        Assert.Equal(1, h.Attempts);

        h.Time.Advance(TimeSpan.FromMilliseconds(999));
        await Wait.SettleAsync();
        Assert.Equal(1, h.Attempts);

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await h.WaitForRetriesAsync(1);
        Assert.Equal(2, h.Attempts);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.SettleAsync();
        Assert.Equal(2, h.Attempts);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.WaitForRetriesAsync(2);
        Assert.Equal(3, h.Attempts);
        await h.InFlight.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, h.RetriesDispatched);
        Assert.Equal(2, h.Logger.Warnings.Count());
        Assert.Empty(h.Logger.Errors);
        Assert.Contains("retry cycle closed", Assert.Single(h.Logger.Informations).Message);
    }

    [Fact]
    public async Task Retry_gives_up_after_max_attempts_and_logs_an_error()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1), backoffFactor: 1));
        h.Body = AlwaysFails();

        await h.FireAsync();

        for (var expected = 2; expected <= 3; expected++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await h.WaitForRetriesAsync(expected - 1);
            Assert.Equal(expected, h.Attempts);
        }

        h.Time.Advance(TimeSpan.FromMinutes(10));
        await Wait.SettleAsync();

        Assert.Equal(3, h.Attempts);
        Assert.Equal(2, h.Logger.Warnings.Count());
        Assert.Contains("after 3 attempts", Assert.Single(h.Logger.Errors).Message);
        await h.InFlight.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Retry_with_a_single_attempt_behaves_like_Ignore()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 1));
        h.Body = AlwaysFails();

        await h.FireAsync();

        Assert.Equal(1, h.Attempts);
        Assert.Empty(h.Logger.Warnings);
        Assert.Single(h.Logger.Errors);
        Assert.Equal(0, h.RetriesDispatched);
    }

    [Fact]
    public async Task Pending_retry_is_tracked_and_cancelled_by_the_stopping_token()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 5, delay: TimeSpan.FromMinutes(1)));
        h.Body = AlwaysFails();

        await h.FireAsync();
        var empty = h.InFlight.WhenEmptyAsync();
        await Wait.SettleAsync();
        Assert.False(empty.IsCompleted);

        h.Stop();
        await empty.WaitAsync(TimeSpan.FromSeconds(1));

        h.Time.Advance(TimeSpan.FromMinutes(5));
        await Wait.SettleAsync();
        Assert.Equal(1, h.Attempts);
        Assert.Equal(0, h.RetriesDispatched);
    }

    // --- Retry: cycle shared between firings ----------------------------------------------------

    [Fact]
    public async Task Success_from_another_firing_closes_the_cycle_and_cancels_the_pending_retry()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)));
        h.Body = FailsUntil(2);

        await h.FireAsync();   // fails: retry scheduled in 1 s
        await h.FireAsync();   // another trigger succeeds: the failure is recovered
        Assert.Contains("retry cycle closed", Assert.Single(h.Logger.Informations).Message);

        h.Time.Advance(TimeSpan.FromSeconds(5));
        await Wait.SettleAsync();

        Assert.Equal(2, h.Attempts);
        Assert.Equal(0, h.RetriesDispatched);
        await h.InFlight.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(h.Logger.Errors);   // the cancelled retry is silent
    }

    [Fact]
    public async Task Failure_while_stopping_does_not_schedule_a_retry()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)));
        h.Body = AlwaysFails();
        h.Stop();

        await h.FireAsync();
        await h.InFlight.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));

        h.Time.Advance(TimeSpan.FromSeconds(5));
        await Wait.SettleAsync();
        Assert.Equal(0, h.RetriesDispatched);
    }

    [Fact]
    public async Task Unexpected_dispatch_failure_is_logged_and_does_not_fault_the_tracked_retry()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)));
        h.Body = AlwaysFails();
        h.DispatchFailure = new InvalidOperationException("dispatcher exploded");

        await h.FireAsync();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.InFlight.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));

        var error = Assert.Single(h.Logger.Errors);
        Assert.Contains("Unexpected error while retrying", error.Message);
        Assert.Equal("dispatcher exploded", error.Exception!.Message);
    }

    [Fact]
    public async Task Failure_from_another_firing_counts_as_an_attempt_and_restarts_the_delay()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 5, delay: TimeSpan.FromSeconds(1), backoffFactor: 2));
        h.Body = AlwaysFails();

        await h.FireAsync();   // attempt 1: retry in 1 s
        h.Time.Advance(TimeSpan.FromMilliseconds(500));

        await h.FireAsync();   // attempt 2 (another trigger): wait restarted, retry in 2 s
        Assert.Equal(2, h.Attempts);

        h.Time.Advance(TimeSpan.FromMilliseconds(500));   // the original deadline has passed: nothing
        await Wait.SettleAsync();
        Assert.Equal(2, h.Attempts);

        h.Time.Advance(TimeSpan.FromMilliseconds(1499));  // 1.999 s after the restart: nothing
        await Wait.SettleAsync();
        Assert.Equal(2, h.Attempts);

        h.Time.Advance(TimeSpan.FromMilliseconds(1));     // 2 s: retry
        await h.WaitForRetriesAsync(1);
        Assert.Equal(3, h.Attempts);

        Assert.Equal(1, h.RetriesDispatched);
        Assert.Contains("attempt 2/5", h.Logger.Warnings.ElementAt(1).Message);
    }

    [Fact]
    public async Task Pending_token_handed_to_the_dispatcher_is_cancelled_when_the_cycle_closes()
    {
        // The dispatcher may still be waiting for a slot when a success closes the cycle.
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Body = n => n == 1 ? throw new InvalidOperationException("boom") : gate.Task;

        await h.FireAsync();                          // fails: retry in 1 s
        h.Time.Advance(TimeSpan.FromSeconds(1));      // the retry is dispatched and blocks on the gate
        await Wait.UntilAsync(() => h.RetriesDispatched == 1);
        Assert.False(h.LastPendingToken.IsCancellationRequested);

        var success = h.FireAsync();                  // another firing, also blocked on the gate
        gate.SetResult();
        await success;

        Assert.True(h.LastPendingToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Pending_token_stays_live_after_the_dispatch_has_returned()
    {
        // The dispatcher returns as soon as the retry is queued; the token it keeps must still be cancelled
        // when a success closes the cycle later on, or the queued retry would run anyway.
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1))) { QueueRetries = true };
        h.Body = n => n == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;

        await h.FireAsync();                          // fails: retry in 1 s
        h.Time.Advance(TimeSpan.FromSeconds(1));      // the retry is dispatched and queued
        await Wait.UntilAsync(() => h.RetriesDispatched == 1);
        await h.InFlight.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));   // RetryAsync has returned
        Assert.False(h.LastPendingToken.IsCancellationRequested);

        await h.FireAsync();                          // success closes the cycle

        Assert.True(h.LastPendingToken.IsCancellationRequested);
    }

    // --- Retry: exhaustion window ---------------------------------------------------------------

    [Fact]
    public async Task Exhausted_cycle_ignores_failures_until_allow_retry_again_after_has_elapsed()
    {
        // 30-minute periodic trigger, 5 attempts 10 s apart, new cycle allowed 45 min after the first failure.
        using var h = new Harness(FailureHandlingMode.Retry(
            maxAttempts: 5, delay: TimeSpan.FromSeconds(10), backoffFactor: 1, allowRetryAgainAfter: TimeSpan.FromMinutes(45)));
        h.Body = AlwaysFails();

        await h.FireAsync();   // t = 0: failure, attempt 1

        for (var expected = 2; expected <= 5; expected++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(10));
            await h.WaitForRetriesAsync(expected - 1);
            Assert.Equal(expected, h.Attempts);
        }

        // t = 40 s: cycle exhausted.
        Assert.Contains("after 5 attempts", Assert.Single(h.Logger.Errors).Message);

        // t = 30 min: the periodic run fails, no retry (45 min have not elapsed).
        h.Time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(40));
        await h.FireAsync();
        Assert.Equal(6, h.Attempts);
        Assert.Contains("retries are exhausted", h.Logger.Errors.Last().Message);

        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.SettleAsync();
        Assert.Equal(6, h.Attempts);

        // t = 60 min: the periodic run fails, 45 min have elapsed, new cycle with retries.
        h.Time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(10));
        await h.FireAsync();
        Assert.Equal(7, h.Attempts);
        Assert.Contains("attempt 1/5", h.Logger.Warnings.Last().Message);

        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.WaitForRetriesAsync(5);
        Assert.Equal(8, h.Attempts);
    }

    [Fact]
    public async Task A_new_cycle_opens_exactly_when_allow_retry_again_after_has_elapsed()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 1, allowRetryAgainAfter: TimeSpan.FromMinutes(45)));
        h.Body = AlwaysFails();

        await h.FireAsync();   // t = 0: exhausted right away (single attempt)

        h.Time.Advance(TimeSpan.FromMinutes(45) - TimeSpan.FromTicks(1));
        await h.FireAsync();
        Assert.Contains("retries are exhausted", h.Logger.Errors.Last().Message);

        h.Time.Advance(TimeSpan.FromTicks(1));
        await h.FireAsync();   // t = 45 min exactly: new cycle
        Assert.Contains("after 1 attempts", h.Logger.Errors.Last().Message);
    }

    [Fact]
    public async Task Success_after_exhaustion_closes_the_cycle_so_the_next_failure_retries_again()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 2, delay: TimeSpan.FromSeconds(1), allowRetryAgainAfter: TimeSpan.FromHours(1)));
        h.Body = _ => throw new InvalidOperationException("boom");

        await h.FireAsync();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.WaitForRetriesAsync(1);

        Assert.Equal(2, h.Attempts);

        h.Body = _ => Task.CompletedTask;
        await h.FireAsync(); // success: cycle closed

        h.Body = _ => throw new InvalidOperationException("boom");
        await h.FireAsync(); // new failure

        Assert.Equal(4, h.Attempts);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.WaitForRetriesAsync(2);

        Assert.Equal(5, h.Attempts);
    }

    [Fact]
    public async Task By_default_the_next_failure_after_exhaustion_opens_a_new_cycle()
    {
        using var h = new Harness(FailureHandlingMode.Retry(maxAttempts: 2, delay: TimeSpan.FromSeconds(1)));
        h.Body = AlwaysFails();

        await h.FireAsync();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.WaitForRetriesAsync(1);                  // exhausted
        Assert.Equal(2, h.Attempts);

        await h.FireAsync();                             // allowRetryAgainAfter = 0: new cycle
        Assert.Equal(3, h.Attempts);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.WaitForRetriesAsync(2);
        Assert.Equal(4, h.Attempts);
    }
}
