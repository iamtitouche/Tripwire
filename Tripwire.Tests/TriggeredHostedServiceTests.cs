using Moq;
using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests;

/// <summary>
/// Covers the wiring between the trigger, the dispatcher, the failure handler and the host lifecycle.
/// The overlap and failure semantics themselves are covered by the tests of the internal components.
/// </summary>
[Collection(ExitCodeCollection.Name)]
public class TriggeredHostedServiceTests
{
    private static readonly Func<CancellationToken, Task> Noop = _ => Task.CompletedTask;

    private static async Task<(ManualTrigger Trigger, TestService Service)> StartAsync(
        Func<CancellationToken, Task> body,
        OverlapPolicy? policy = null,
        FailureHandlingMode? failureHandling = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        var trigger = new ManualTrigger();
        var service = new TestService(trigger, body, policy, failureHandling, logger, timeProvider);
        await service.StartAsync(CancellationToken.None);
        await trigger.Started;
        return (trigger, service);
    }

    [Fact]
    public void Rejects_null_trigger()
    {
        Assert.Throws<ArgumentNullException>(() => new TestService(null!, Noop));
    }

    [Fact]
    public async Task Runs_the_iteration_when_the_trigger_fires()
    {
        var calls = 0;
        var (trigger, service) = await StartAsync(_ => { calls++; return Task.CompletedTask; });

        await trigger.FireAsync();
        await Wait.UntilAsync(() => calls == 1);
        await trigger.FireAsync();
        await Wait.UntilAsync(() => calls == 2);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Firing_returns_before_the_iteration_completes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        var (trigger, service) = await StartAsync(async _ => { started = true; await gate.Task; });

        // Bounded: under a blocking contract the firing would only return once the iteration completed.
        await trigger.FireAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => started);

        gate.SetResult();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Firing_returns_before_a_synchronous_iteration_completes()
    {
        // An iteration that does its work synchronously, held until released.
        using var gate = new ManualResetEventSlim();
        var (trigger, service) = await StartAsync(_ =>
        {
            gate.Wait();
            return Task.CompletedTask;
        });

        // Fired off the test thread: if the iteration runs inline, the firing only returns once it is released.
        var firing = Task.Run(trigger.FireAsync);

        try
        {
            var returned = await Task.WhenAny(firing, Task.Delay(TimeSpan.FromSeconds(2))) == firing;

            Assert.True(returned, "The firing is blocked by the synchronous iteration.");
        }
        finally
        {
            gate.Set();
            await firing;
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Queued_firings_are_dropped_on_stop_and_the_running_iteration_completes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var (trigger, service) = await StartAsync(async _ => { calls++; await gate.Task; });

        await trigger.FireAsync();                 // runs
        await trigger.FireAsync();                 // queued
        await Wait.UntilAsync(() => calls == 1);

        var stopping = service.StopAsync(CancellationToken.None);
        gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, calls);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Completes_cleanly_when_stopped()
    {
        var (_, service) = await StartAsync(Noop);

        // Bounded: ExecuteAsync must complete once the trigger has stopped, without waiting for anything else.
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Dispose_without_stop_cancels_the_trigger_and_rejects_further_firings()
    {
        var (trigger, service) = await StartAsync(Noop);

        service.Dispose();

        Assert.True(trigger.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trigger.FireAsync());
    }

    [Fact]
    public async Task Trigger_failure_faults_the_service()
    {
        var trigger = new Mock<IServiceTrigger>();
        trigger
            .Setup(t => t.StartAsync(It.IsAny<Func<CancellationToken, Task<Firing>>>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Yield(); throw new InvalidOperationException("trigger boom"); });
        var service = new TestService(trigger.Object, Noop);

        // BackgroundService.StartAsync returns ExecuteTask itself when it has already completed, so
        // depending on scheduling the failure surfaces from either call.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        });
        Assert.Equal("trigger boom", ex.Message);
    }

    // --- No firing lost while an iteration is running -----------------------------------------

    [Fact]
    public async Task Periodic_ticks_landing_during_a_slow_iteration_are_queued_not_coalesced()
    {
        var timeProvider = new FakeTimeProvider();
        var logger = new TestLogger();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var trigger = new SignallingTrigger(new PeriodicTrigger(TimeSpan.FromMinutes(1), timeProvider));
        var service = new TestService(
            trigger,
            async _ => { Interlocked.Increment(ref calls); await gate.Task; },
            logger: logger);
        await service.StartAsync(CancellationToken.None);
        await trigger.Started;

        // Each tick is let through before the next one: PeriodicTimer itself coalesces ticks that land
        // before the loop is back waiting, which a real clock never provokes when execute returns at once.
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => calls == 1);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => logger.Warnings.Count() == 1);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => logger.Warnings.Count() == 2);
        Assert.Equal(1, calls);

        gate.SetResult();
        await Wait.UntilAsync(() => calls == 3);   // the two missed ticks run one after the other

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Scheduled_times_landing_during_a_slow_iteration_are_queued_not_lost()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 7, 59, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var trigger = new SignallingTrigger(new ScheduleTrigger([new TimeOnly(8, 0), new TimeOnly(8, 5)], timeProvider));
        var service = new TestService(
            trigger,
            async _ => { Interlocked.Increment(ref calls); await gate.Task; });
        await service.StartAsync(CancellationToken.None);
        await trigger.Started;

        timeProvider.Advance(TimeSpan.FromMinutes(1));      // 08:00
        await Wait.UntilAsync(() => calls == 1);
        await Wait.SettleAsync();                   // the trigger is back waiting for 08:05
        timeProvider.Advance(TimeSpan.FromMinutes(10));     // 08:10: 08:05 landed while the iteration was running
        await Wait.SettleAsync();
        Assert.Equal(1, calls);

        gate.SetResult();
        await Wait.UntilAsync(() => calls == 2);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Skip_drops_periodic_ticks_landing_during_a_slow_iteration()
    {
        var timeProvider = new FakeTimeProvider();
        var logger = new TestLogger();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var trigger = new SignallingTrigger(new PeriodicTrigger(TimeSpan.FromMinutes(1), timeProvider));
        var service = new TestService(
            trigger,
            async _ => { Interlocked.Increment(ref calls); await gate.Task; },
            OverlapPolicy.Skip,
            logger: logger);
        await service.StartAsync(CancellationToken.None);
        await trigger.Started;

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => calls == 1);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => logger.Informations.Count(e => e.Message.Contains("skipped")) == 1);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => logger.Informations.Count(e => e.Message.Contains("skipped")) == 2);

        gate.SetResult();
        await Wait.SettleAsync();
        Assert.Equal(1, calls);

        await service.StopAsync(CancellationToken.None);
    }

    // --- Lifecycle of unawaited work ------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Iterations_are_awaited_on_stop(int maxConcurrency)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var (trigger, service) = await StartAsync(async _ => { await gate.Task; finished = true; }, OverlapPolicy.Concurrent(maxConcurrency));

        await trigger.FireAsync();
        var stopping = service.StopAsync(CancellationToken.None);
        await Wait.SettleAsync();

        Assert.False(stopping.IsCompleted);
        Assert.False(finished);

        gate.SetResult();
        await stopping;

        Assert.True(finished);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Iterations_receive_cancellation_on_stop(int maxConcurrency)
    {
        var observed = false;
        var (trigger, service) = await StartAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { observed = true; throw; }
        }, OverlapPolicy.Concurrent(maxConcurrency));

        await trigger.FireAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.True(observed);
    }

    [Fact]
    public async Task Pending_retry_keeps_the_service_running_after_the_trigger_completes()
    {
        // OnStartupTrigger completes right away; the service must stay alive while a retry is pending.
        var timeProvider = new FakeTimeProvider();
        var attempts = 0;
        var service = new TestService(
            new OnStartupTrigger(),
            _ => { attempts++; return attempts < 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask; },
            failureHandling: FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromMinutes(10), maxDelay: TimeSpan.FromMinutes(10)),
            timeProvider: timeProvider);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => attempts == 1);
        await Wait.SettleAsync();
        Assert.False(service.ExecuteTask!.IsCompleted);

        // A ten-minute delay can only elapse through the injected TimeProvider.
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await Wait.UntilAsync(() => attempts == 2);
        await service.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Iteration_outlives_a_composite_trigger_whose_children_have_all_completed()
    {
        // A child that fires once, then completes, like OnStartupTrigger.
        var child = new Mock<IServiceTrigger>();
        child
            .Setup(t => t.StartAsync(It.IsAny<Func<CancellationToken, Task<Firing>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<CancellationToken, Task<Firing>> execute, CancellationToken ct) => { await execute(ct); });

        // The service keeps the task returned by StartAsync to itself: the composite is wrapped to get it, so that
        // the assertion runs only once the composite's finally block, where the cancellation happens, has run.
        var composite = new CompositeTrigger(child.Object);
        Task? compositeRun = null;
        var trigger = new Mock<IServiceTrigger>();
        trigger
            .Setup(t => t.StartAsync(It.IsAny<Func<CancellationToken, Task<Firing>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<Firing>> execute, CancellationToken ct) => compositeRun = composite.StartAsync(execute, ct));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iterationToken = CancellationToken.None;
        var completed = false;
        var service = new TestService(trigger.Object, async ct =>
        {
            iterationToken = ct;
            started.SetResult();
            await gate.Task.WaitAsync(ct);
            completed = true;
        });

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await compositeRun!.WaitAsync(TimeSpan.FromSeconds(2));

        // The trigger completing normally is not a shutdown: the running iteration must not be cancelled.
        Assert.False(iterationToken.IsCancellationRequested);

        gate.SetResult();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completed);
    }

    [Fact]
    public async Task Synchronous_startup_iteration_does_not_block_the_service_start()
    {
        // An iteration that does its work synchronously, as the README example does, held until released.
        using var gate = new ManualResetEventSlim();
        var service = new TestService(new OnStartupTrigger(), _ =>
        {
            gate.Wait();
            return Task.CompletedTask;
        });

        // Started off the test thread: if the iteration runs inline, StartAsync only returns once it is released.
        var start = Task.Run(() => service.StartAsync(CancellationToken.None));

        try
        {
            var returned = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2))) == start;

            Assert.True(returned, "StartAsync is blocked by the synchronous startup iteration.");
        }
        finally
        {
            gate.Set();
            await start;
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pending_retry_is_interrupted_cleanly_by_shutdown()
    {
        var timeProvider = new FakeTimeProvider();
        var attempts = 0;
        var (trigger, service) = await StartAsync(
            _ => { attempts++; throw new InvalidOperationException("boom"); },
            failureHandling: FailureHandlingMode.Retry(maxAttempts: 5, delay: TimeSpan.FromMinutes(1)), timeProvider: timeProvider);

        await trigger.FireAsync();
        await Wait.UntilAsync(() => attempts == 1);

        await service.StopAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Wait.SettleAsync();

        Assert.Equal(1, attempts);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    // --- Failure handling wiring ----------------------------------------------------------------

    [Fact]
    public async Task Ignore_is_the_default_and_the_service_keeps_running()
    {
        var logger = new TestLogger();
        var (trigger, service) = await StartAsync(_ => throw new InvalidOperationException("boom"), logger: logger);

        await trigger.FireAsync();
        await Wait.UntilAsync(() => logger.Errors.Count() == 1);
        await trigger.FireAsync();
        await Wait.UntilAsync(() => logger.Errors.Count() == 2);

        Assert.All(logger.Errors, e => Assert.Equal("Iteration of TestService failed.", e.Message));

        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopOnFailure_does_not_surface_through_the_firing()
    {
        var (trigger, service) = await StartAsync(
            _ => throw new InvalidOperationException("boom"),
            OverlapPolicy.Wait, FailureHandlingMode.StopOnFailure);

        Environment.ExitCode = 0;
        var firing = trigger.FireAsync();

        Assert.True(firing.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2)));
        Environment.ExitCode = 0;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopOnFailure_faults_the_service_and_cancels_the_trigger(int maxConcurrency)
    {
        var logger = new TestLogger();
        var (trigger, service) = await StartAsync(
            async _ => { await Task.Yield(); throw new InvalidOperationException("boom"); },
            OverlapPolicy.Concurrent(maxConcurrency), FailureHandlingMode.StopOnFailure, logger);

        Environment.ExitCode = 0;
        await trigger.FireAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("boom", ex.Message);
        Assert.True(trigger.Token.IsCancellationRequested);
        Assert.Contains("exit code 1", Assert.Single(logger.Errors).Message);
        Assert.Equal(1, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task Fatal_failure_waits_for_the_trigger_to_stop_before_propagating()
    {
        var triggerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trigger = new Mock<IServiceTrigger>();
        trigger
            .Setup(t => t.StartAsync(It.IsAny<Func<CancellationToken, Task<Firing>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<CancellationToken, Task<Firing>> execute, CancellationToken ct) =>
            {
                await execute(ct);                              // starts the failing iteration
                await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                await triggerStopped.Task;                      // takes its time to wind down after cancellation
            });
        var service = new TestService(
            trigger.Object,
            async _ => { await Task.Yield(); throw new InvalidOperationException("boom"); },
            failureHandling: FailureHandlingMode.StopOnFailure);

        Environment.ExitCode = 0;
        await service.StartAsync(CancellationToken.None);
        await Wait.SettleAsync();

        Assert.False(service.ExecuteTask!.IsCompleted);

        triggerStopped.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task Stop_waits_for_in_flight_iterations_after_a_fatal_failure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var (trigger, service) = await StartAsync(
            async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) { await gate.Task; return; }   // ignores cancellation
                await Task.Yield();
                throw new InvalidOperationException("boom");
            },
            OverlapPolicy.Concurrent(2), FailureHandlingMode.StopOnFailure);

        Environment.ExitCode = 0;
        await trigger.FireAsync();                                   // long-running iteration
        await trigger.FireAsync();                                   // fatal iteration
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2)));

        var stopping = service.StopAsync(CancellationToken.None);
        await Wait.SettleAsync();
        Assert.False(stopping.IsCompleted);                          // the first iteration is still in flight

        gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task Cancellation_during_an_iteration_is_not_treated_as_a_failure()
    {
        var logger = new TestLogger();
        var (trigger, service) = await StartAsync(ct => Task.Delay(Timeout.Infinite, ct), failureHandling: FailureHandlingMode.StopOnFailure, logger: logger);

        await trigger.FireAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    // --- Retry through the overlap policy -------------------------------------------------------

    [Fact]
    public async Task Wait_cancels_a_retry_still_waiting_for_its_slot_when_the_running_iteration_succeeds()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var (trigger, service) = await StartAsync(
            async _ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1) throw new InvalidOperationException("boom");
                await gate.Task;
            },
            OverlapPolicy.Wait, FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)), timeProvider: timeProvider);

        await trigger.FireAsync();                     // failure: retry in 1 s
        await Wait.UntilAsync(() => attempts == 1);
        await trigger.FireAsync();                     // another firing starts and holds the slot
        await Wait.UntilAsync(() => attempts == 2);

        timeProvider.Advance(TimeSpan.FromSeconds(1));         // the retry fires and waits for the slot
        await Wait.SettleAsync();

        gate.SetResult();                              // the running iteration succeeds: cycle closed
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, attempts);                     // the retry never ran
    }

    [Fact]
    public async Task Skip_drops_a_retry_landing_during_an_iteration()
    {
        var timeProvider = new FakeTimeProvider();
        var logger = new TestLogger();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var (trigger, service) = await StartAsync(
            async _ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1) throw new InvalidOperationException("boom");
                await gate.Task;
            },
            OverlapPolicy.Skip, FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)), logger, timeProvider);

        await trigger.FireAsync();
        await Wait.UntilAsync(() => attempts == 1);
        await trigger.FireAsync();
        await Wait.UntilAsync(() => attempts == 2);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => logger.Informations.Any(e => e.Message.Contains("skipped")));

        gate.SetResult();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Concurrent_allows_a_retry_and_a_regular_iteration_at_the_same_time()
    {
        var timeProvider = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var inFlight = 0;
        var (trigger, service) = await StartAsync(
            async _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("boom");
                Interlocked.Increment(ref inFlight);
                await gate.Task;
            },
            OverlapPolicy.Concurrent(2), FailureHandlingMode.Retry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)), timeProvider: timeProvider);

        await trigger.FireAsync();                     // failure: retry in 1 s
        // The iteration runs on the thread pool: the retry delay may not be scheduled yet, so the clock is
        // advanced until the retry starts.
        await Wait.UntilAsync(() =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            return inFlight == 1;
        });

        await trigger.FireAsync();                     // a regular firing starts alongside
        await Wait.UntilAsync(() => inFlight == 2);

        gate.SetResult();
        await service.StopAsync(CancellationToken.None);
    }
}
