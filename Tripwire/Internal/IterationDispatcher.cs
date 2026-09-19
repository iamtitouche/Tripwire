namespace Tripwire.Internal;

/// <summary>
/// Applies the <see cref="OverlapPolicy"/> to every firing, regular or retry: starts the iteration when
/// a slot is free, queues the firing when all slots are busy, drops it when the queue is full. Triggers
/// never wait for an iteration: every iteration is tracked in the <see cref="InFlightTracker"/> so it
/// can be awaited at shutdown.
/// </summary>
internal sealed class IterationDispatcher : IDisposable
{
    private readonly OverlapPolicy _policy;
    private readonly SemaphoreSlim _semaphore;
    private readonly InFlightTracker _inFlight;
    private readonly Func<CancellationToken, Task> _iteration;
    private readonly Action<Exception> _reportFatal;
    private readonly ILogger? _logger;
    private readonly string _serviceName;
    private int _pending;

    /// <param name="policy">Overlap policy to apply.</param>
    /// <param name="inFlight">Tracker for the iterations and queued firings.</param>
    /// <param name="iteration">Runs one iteration. Only throws when the failure must stop the application.</param>
    /// <param name="reportFatal">Called when an iteration throws.</param>
    /// <param name="logger">Optional logger for queued and dropped firings.</param>
    /// <param name="serviceName">Service name used in log messages.</param>
    public IterationDispatcher(
        OverlapPolicy policy,
        InFlightTracker inFlight,
        Func<CancellationToken, Task> iteration,
        Action<Exception> reportFatal,
        ILogger? logger,
        string serviceName)
    {
        _policy = policy;
        _semaphore = new SemaphoreSlim(policy.MaxConcurrency, policy.MaxConcurrency);
        _inFlight = inFlight;
        _iteration = iteration;
        _reportFatal = reportFatal;
        _logger = logger;
        _serviceName = serviceName;
    }

    /// <summary>
    /// Entry point of every firing. Returns as soon as the firing has been started, queued or dropped:
    /// the iteration itself is never awaited by the caller.
    /// </summary>
    /// <param name="cancellationToken">
    /// The service's stopping token: observed while the firing waits for a slot, then handed to the iteration.
    /// </param>
    /// <param name="pendingToken">
    /// Also observed while the firing waits for a slot. For a retry, it is cancelled if the cycle is closed
    /// or rescheduled in the meantime, so that a retry that has become pointless does not run.
    /// </param>
    /// <exception cref="OperationCanceledException">Either token is already cancelled.</exception>
    public Task DispatchAsync(CancellationToken cancellationToken, CancellationToken pendingToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pendingToken.ThrowIfCancellationRequested();

        if (_semaphore.Wait(0))
        {
            _inFlight.Track(RunAsync(cancellationToken));
            return Task.CompletedTask;
        }

        var pending = Interlocked.Increment(ref _pending);

        // Lifted comparison: with a null (unbounded) MaxPending, nothing is ever dropped.
        if (pending > _policy.MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            LogDropped();
            return Task.CompletedTask;
        }

        LogQueued(pending);
        _inFlight.Track(WaitForSlotThenRunAsync(cancellationToken, pendingToken));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private void LogDropped()
    {
        if (_policy.MaxPending == 0)
        {
            // Skipping is the whole point of the policy: nothing alarming.
            _logger?.LogInformation(
                "Iteration of {ServiceType} skipped: {MaxConcurrency} iteration(s) already running.",
                _serviceName, _policy.MaxConcurrency);
        }
        else
        {
            _logger?.LogWarning(
                "Iteration of {ServiceType} dropped: {MaxPending} firing(s) already waiting for a slot, the service cannot keep up with its trigger.",
                _serviceName, _policy.MaxPending);
        }
    }

    private void LogQueued(int pending)
    {
        if (_policy.MaxPending is null)
        {
            // Nothing else bounds the queue: an accumulation is only visible here.
            _logger?.LogWarning(
                "Iteration of {ServiceType} queued: {Pending} firing(s) waiting for a slot, the service cannot keep up with its trigger.",
                _serviceName, pending);
        }
        else
        {
            _logger?.LogInformation(
                "Iteration of {ServiceType} queued: {Pending}/{MaxPending} firing(s) waiting for a slot.",
                _serviceName, pending, _policy.MaxPending);
        }
    }

    /// <summary>
    /// Waits for a slot, then runs the iteration. A cancellation that lands just before a slot is released
    /// can be lost by <see cref="SemaphoreSlim"/> (the waiter is served anyway); the tokens are therefore
    /// re-checked after acquisition, so that a firing that has become pointless does not run.
    /// </summary>
    private async Task WaitForSlotThenRunAsync(CancellationToken cancellationToken, CancellationToken pendingToken)
    {
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pendingToken);
            await _semaphore.WaitAsync(waiting.Token);

            if (waiting.IsCancellationRequested)
            {
                ReleaseSlot();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }

        await RunAsync(cancellationToken);
    }

    /// <summary>
    /// Runs an iteration holding a slot. A fatal exception is reported instead of thrown,
    /// so the tracked task never faults.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _iteration(cancellationToken);
        }
        catch (Exception ex)
        {
            _reportFatal(ex);
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private void ReleaseSlot()
    {
        try
        {
            _semaphore.Release();
        }
        // Stryker disable once all : only observable as an unobserved task exception.
        catch (ObjectDisposedException)
        {
            // The service was disposed without being stopped while this iteration was running: there is
            // nobody left to hand the slot to, and a tracked task must not fault.
        }
    }
}
