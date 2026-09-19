using Tripwire.Internal;

namespace Tripwire;

/// <summary>
/// Hosted service whose runs are driven by an <see cref="IServiceTrigger"/>.
/// The behaviour when the trigger fires during an iteration is defined by <see cref="OverlapPolicy"/>,
/// the behaviour when an iteration fails by <see cref="FailureHandlingMode"/>.
/// </summary>
public abstract class TriggeredHostedService : BackgroundService
{
    private readonly IServiceTrigger _trigger;
    private readonly InFlightTracker _inFlight = new();
    private readonly TaskCompletionSource _fatal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FailureHandler _failureHandler;
    private readonly IterationDispatcher _dispatcher;

    /// <param name="trigger">The trigger that determines when <see cref="ExecuteIteration"/> is called.</param>
    /// <param name="overlapPolicy">
    /// Behaviour when the trigger fires during an iteration. <see cref="OverlapPolicy.Wait"/> by default.
    /// </param>
    /// <param name="failureHandling">
    /// Behaviour when an iteration throws an exception, shared by all the service's triggers.
    /// <see cref="FailureHandlingMode.Ignore"/> by default.
    /// </param>
    /// <param name="logger">
    /// Optional logger, used to report skipped, retried and failed iterations.
    /// </param>
    /// <param name="timeProvider">
    /// Time source for the delays between attempts (<see cref="FailureHandlingMode.Retry"/>).
    /// <see cref="TimeProvider.System"/> by default.
    /// </param>
    protected TriggeredHostedService(
        IServiceTrigger trigger,
        OverlapPolicy? overlapPolicy = null,
        FailureHandlingMode? failureHandling = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        _trigger = trigger;
        var serviceName = GetType().Name;

        // Retries are dispatched like regular firings, and every firing runs through the failure handler:
        // the two components reference each other through RunIterationAsync and DispatchRetryAsync.
        _failureHandler = new FailureHandler(
            failureHandling ?? FailureHandlingMode.Ignore,
            timeProvider ?? TimeProvider.System,
            logger,
            serviceName,
            _inFlight,
            DispatchRetryAsync);

        _dispatcher = new IterationDispatcher(
            overlapPolicy ?? OverlapPolicy.Wait,
            _inFlight,
            RunIterationAsync,
            ReportFatal,
            logger,
            serviceName);
    }

    private Task RunIterationAsync(CancellationToken cancellationToken)
    {
        return _failureHandler.RunAsync(ExecuteIteration, cancellationToken);
    }

    private Task DispatchRetryAsync(CancellationToken cancellationToken, CancellationToken pendingToken)
    {
        return _dispatcher.DispatchAsync(cancellationToken, pendingToken);
    }

    private void ReportFatal(Exception exception)
    {
        _fatal.TrySetException(exception);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Iterations are never awaited by the trigger: the dispatcher tracks them, and a fatal failure
        // surfaces through _fatal.
        var trigger = _trigger.StartAsync(ct => _dispatcher.DispatchAsync(ct, ct), linked.Token);

        if (await Task.WhenAny(trigger, _fatal.Task) == trigger)
        {
            await trigger;

            // The trigger has completed (OnStartupTrigger, or host shutdown), but iterations, queued
            // firings or retries may still be in flight and fail fatally.
            await Task.WhenAny(_inFlight.WhenEmptyAsync(), _fatal.Task);
        }

        if (!_fatal.Task.IsFaulted)
        {
            return;
        }

        // An iteration of a critical service failed: stop the trigger and the in-flight iterations,
        // then propagate the exception so the host stops.
        linked.Cancel();
        await trigger.ContinueWith(_ => { }, TaskScheduler.Default);
        await _fatal.Task;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await _inFlight.WhenEmptyAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();

        // Stryker disable once Statement : releasing the semaphore has no observable effect once the token is cancelled.
        _dispatcher.Dispose();

        // Stryker disable once Statement : no finalizer, kept for the CA1816 Dispose pattern.
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The work to do on each firing.
    /// </summary>
    /// <param name="cancellationToken">Signalled when the application shuts down.</param>
    protected abstract Task ExecuteIteration(CancellationToken cancellationToken);
}
