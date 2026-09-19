namespace Tripwire.Internal;

/// <summary>
/// Applies the <see cref="FailureHandlingMode"/> to every iteration: logs and ignores, stops the
/// application, or drives the retry cycle shared by all the service's triggers.
/// </summary>
internal sealed class FailureHandler
{
    private readonly FailureHandlingMode _mode;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly string _serviceName;
    private readonly InFlightTracker _inFlight;
    private readonly Func<CancellationToken, CancellationToken, Task> _dispatch;
#if NET9_0_OR_GREATER
    private readonly Lock _retryLock = new();
#else
    private readonly object _retryLock = new();
#endif
    private RetryCycle? _retryCycle;

    /// <param name="mode">Failure handling mode to apply.</param>
    /// <param name="timeProvider">Time source for retry delays.</param>
    /// <param name="logger">Optional logger for failed, retried and recovered iterations.</param>
    /// <param name="serviceName">Service name used in log messages.</param>
    /// <param name="inFlight">Tracker for pending retries.</param>
    /// <param name="dispatch">
    /// Dispatches a retry like a regular firing: first argument is the token handed to the iteration,
    /// second the token observed while the retry is pending.
    /// </param>
    public FailureHandler(
        FailureHandlingMode mode,
        TimeProvider timeProvider,
        ILogger? logger,
        string serviceName,
        InFlightTracker inFlight,
        Func<CancellationToken, CancellationToken, Task> dispatch)
    {
        _mode = mode;
        _timeProvider = timeProvider;
        _logger = logger;
        _serviceName = serviceName;
        _inFlight = inFlight;
        _dispatch = dispatch;
    }

    /// <summary>
    /// Runs <paramref name="iteration"/> and applies the failure handling mode to its outcome.
    /// Only throws under <see cref="FailureHandlingMode.StopOnFailure"/>; cancellation is swallowed.
    /// </summary>
    /// <param name="iteration">The work to run.</param>
    /// <param name="cancellationToken">The service's stopping token, handed to the iteration and to retries.</param>
    public async Task RunAsync(Func<CancellationToken, Task> iteration, CancellationToken cancellationToken)
    {
        try
        {
            await iteration(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (_mode.Kind != FailureHandlingMode.Mode.StopOnFailure)
        {
            OnFailed(ex, cancellationToken);
            return;
        }
        catch (Exception ex) when (_mode.Kind == FailureHandlingMode.Mode.StopOnFailure)
        {
            // The host stops gracefully (StopHost) and would otherwise exit with code 0: set the exit code
            // first so the failure is visible to whoever runs the process.
            Environment.ExitCode = _mode.ExitCode;
            _logger?.LogError(
                ex,
                "Iteration of {ServiceType} failed; stopping the application with exit code {ExitCode}.",
                _serviceName, _mode.ExitCode);
            throw;
        }

        OnSucceeded();
    }

    private void OnSucceeded()
    {
        lock (_retryLock)
        {
            // Only the Retry mode ever opens a cycle.
            if (_retryCycle is null)
            {
                return;
            }

            // A success, whichever trigger started it, recovers the current failure: the cycle is closed
            // and any pending retry is cancelled.
            _retryCycle.CancelPending();
            _logger?.LogInformation(
                "Iteration of {ServiceType} succeeded after {Attempts} failed attempt(s); retry cycle closed.",
                _serviceName, _retryCycle.Attempts);
            _retryCycle = null;
        }
    }

    private void OnFailed(Exception exception, CancellationToken stoppingToken)
    {
        if (_mode.Kind == FailureHandlingMode.Mode.Ignore)
        {
            _logger?.LogError(exception, "Iteration of {ServiceType} failed.", _serviceName);
            return;
        }

        lock (_retryLock)
        {
            var now = _timeProvider.GetUtcNow();

            if (_retryCycle is null || (_retryCycle.IsExhausted && now >= _retryCycle.RetryAgainAt))
            {
                _retryCycle = new RetryCycle(_mode, now);
            }

            if (_retryCycle.IsExhausted)
            {
                _logger?.LogError(
                    exception,
                    "Iteration of {ServiceType} failed; retries are exhausted, none before {RetryAgainAt}.",
                    _serviceName, _retryCycle.RetryAgainAt);
                return;
            }

            // Any failure, retry or regular firing, consumes an attempt and restarts the wait from zero.
            _retryCycle.Attempts++;
            _retryCycle.CancelPending();

            if (_retryCycle.IsExhausted)
            {
                _logger?.LogError(
                    exception,
                    "Iteration of {ServiceType} failed after {Attempts} attempts; no more retries before {RetryAgainAt}.",
                    _serviceName, _retryCycle.Attempts, _retryCycle.RetryAgainAt);
                return;
            }

            var delay = _mode.GetDelay(_retryCycle.Attempts);
            _logger?.LogWarning(
                exception,
                "Iteration of {ServiceType} failed (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}.",
                _serviceName, _retryCycle.Attempts, _mode.MaxAttempts, delay);

            var pending = new CancellationTokenSource();
            _retryCycle.Pending = pending;
            _inFlight.Track(RetryAsync(delay, pending.Token, stoppingToken));
        }
    }

    /// <summary>
    /// Waits for the delay, then dispatches a new iteration like a regular trigger would.
    /// Gives up silently if the retry is cancelled before it starts, or at shutdown.
    /// </summary>
    private async Task RetryAsync(TimeSpan delay, CancellationToken pendingToken, CancellationToken stoppingToken)
    {
        try
        {
            using (var delaying = CancellationTokenSource.CreateLinkedTokenSource(pendingToken, stoppingToken))
            {
                await Task.Delay(delay, _timeProvider, delaying.Token);
            }

            // The dispatch returns as soon as the firing is queued, so the pending token handed over must
            // outlive this method: the cycle's own token is passed, not a linked one disposed on return.
            await _dispatch(stoppingToken, pendingToken);
        }
        catch (OperationCanceledException) when (pendingToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A tracked task must never fault; in Retry mode, an iteration does not throw.
            _logger?.LogError(ex, "Unexpected error while retrying an iteration of {ServiceType}.", _serviceName);
        }
    }

    /// <summary>
    /// State of a retry cycle: opened by the first failure, closed by the first success.
    /// </summary>
    private sealed class RetryCycle(FailureHandlingMode mode, DateTimeOffset firstFailureAt)
    {
        public int Attempts { get; set; }

        public CancellationTokenSource? Pending { get; set; }

        public bool IsExhausted => Attempts >= mode.MaxAttempts;

        public DateTimeOffset RetryAgainAt => firstFailureAt + mode.AllowRetryAgainAfter;

        public void CancelPending()
        {
            Pending?.Cancel();
            Pending = null;
        }
    }
}
