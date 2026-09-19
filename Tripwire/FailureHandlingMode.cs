namespace Tripwire;

/// <summary>
/// Behaviour of a <see cref="TriggeredHostedService"/> when an iteration throws an exception.
/// </summary>
public sealed class FailureHandlingMode
{
    internal enum Mode
    {
        Ignore,
        StopOnFailure,
        Retry,
    }

    private const int DefaultExitCode = 1;

    private FailureHandlingMode(
        Mode kind,
        int maxAttempts = 1,
        TimeSpan delay = default,
        double backoffFactor = 1,
        TimeSpan maxDelay = default,
        TimeSpan allowRetryAgainAfter = default,
        int exitCode = DefaultExitCode)
    {
        Kind = kind;
        MaxAttempts = maxAttempts;
        Delay = delay;
        BackoffFactor = backoffFactor;
        MaxDelay = maxDelay;
        AllowRetryAgainAfter = allowRetryAgainAfter;
        ExitCode = exitCode;
    }

    /// <summary>
    /// The exception is logged and the iteration abandoned. The service keeps running
    /// and will fire again on the next occurrence.
    /// </summary>
    public static FailureHandlingMode Ignore { get; } = new(Mode.Ignore);

    /// <summary>
    /// The exception is logged, <see cref="Environment.ExitCode"/> is set to 1, and the exception is propagated
    /// out of the service, which stops the application (the host's default behaviour,
    /// <c>BackgroundServiceExceptionBehavior.StopHost</c>). The host shuts down gracefully but the process
    /// reports a failure, so orchestrators and service managers can react.
    /// </summary>
    public static FailureHandlingMode StopOnFailure { get; } = new(Mode.StopOnFailure);

    /// <summary>
    /// Same as <see cref="StopOnFailure"/>, with a custom process exit code.
    /// </summary>
    /// <param name="exitCode">Exit code reported by the process. Must be non-zero.</param>
    public static FailureHandlingMode StopOnFailureWithExitCode(int exitCode)
    {
        if (exitCode == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exitCode), "The exit code must be non-zero to report a failure.");
        }

        return new FailureHandlingMode(Mode.StopOnFailure, exitCode: exitCode);
    }

    /// <summary>
    /// After a failure, the service runs a new iteration after a delay. The retry cycle is shared by all
    /// the service's triggers: any failed iteration counts as an attempt and restarts the wait with the next
    /// delay; any successful iteration closes the cycle. The delay is multiplied by
    /// <paramref name="backoffFactor"/> on each attempt, without exceeding <paramref name="maxDelay"/>.
    /// Once <paramref name="maxAttempts"/> is reached, failures are logged without retrying, until a success
    /// closes the cycle or <paramref name="allowRetryAgainAfter"/> has elapsed since the first failure of
    /// the cycle.
    /// </summary>
    /// <param name="maxAttempts">Maximum number of attempts per cycle, first run included. 3 by default.</param>
    /// <param name="delay">Delay before the second attempt. 1 second by default.</param>
    /// <param name="backoffFactor">Factor applied to the delay on each subsequent attempt. 2 by default (1 s, 2 s, 4 s…).</param>
    /// <param name="maxDelay">Cap on the delay between two attempts. 5 minutes by default.</param>
    /// <param name="allowRetryAgainAfter">
    /// Duration, counted from the first failure of the cycle, after which a new failure may open a new cycle
    /// while the previous one is exhausted. Zero by default: the next failure opens a new cycle.
    /// </param>
    public static FailureHandlingMode Retry(
        int maxAttempts = 3,
        TimeSpan? delay = null,
        double backoffFactor = 2,
        TimeSpan? maxDelay = null,
        TimeSpan? allowRetryAgainAfter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(backoffFactor, 1);

        var actualDelay = delay ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(actualDelay, TimeSpan.Zero, nameof(delay));

        var actualMaxDelay = maxDelay ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfLessThan(actualMaxDelay, actualDelay, nameof(maxDelay));

        var actualAllowRetryAgainAfter = allowRetryAgainAfter ?? TimeSpan.Zero;
        ArgumentOutOfRangeException.ThrowIfLessThan(actualAllowRetryAgainAfter, TimeSpan.Zero, nameof(allowRetryAgainAfter));

        return new FailureHandlingMode(Mode.Retry, maxAttempts, actualDelay, backoffFactor, actualMaxDelay, actualAllowRetryAgainAfter);
    }

    internal Mode Kind { get; }

    internal int MaxAttempts { get; }

    internal TimeSpan Delay { get; }

    internal double BackoffFactor { get; }

    internal TimeSpan MaxDelay { get; }

    internal TimeSpan AllowRetryAgainAfter { get; }

    internal int ExitCode { get; }

    /// <summary>
    /// Delay to wait after attempt <paramref name="attempt"/> (starting at 1) has failed, capped at
    /// <see cref="MaxDelay"/>. Computed in double so it never overflows: a large factor simply yields
    /// infinity, which is brought back to the cap.
    /// </summary>
    internal TimeSpan GetDelay(int attempt)
    {
        var ticks = Delay.Ticks * Math.Pow(BackoffFactor, attempt - 1);

        // Stryker disable once Equality : at exactly MaxDelay both branches yield the same value.
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind switch
        {
            Mode.Retry => $"{Kind}({MaxAttempts}, {Delay}, x{BackoffFactor}, max {MaxDelay}, again after {AllowRetryAgainAfter})",
            Mode.StopOnFailure when ExitCode != DefaultExitCode => $"{Kind}(exit code {ExitCode})",
            _ => Kind.ToString(),
        };
    }
}
