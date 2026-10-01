namespace Tripwire;

/// <summary>
/// Determines when a <see cref="TriggeredHostedService"/> runs.
/// </summary>
public interface IServiceTrigger
{
    /// <summary>
    /// Starts the trigger. Calls <paramref name="execute"/> each time the service should run,
    /// and returns when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="execute">
    /// The firing to report. It returns as soon as the service has taken the firing into account (iteration
    /// started, queued or dropped according to its <see cref="OverlapPolicy"/>), never waits for the iteration
    /// itself, and does not throw for iteration failures, which the service handles according to its
    /// <see cref="FailureHandlingMode"/>. A trigger therefore never blocks and never misses an occurrence
    /// because an iteration is slow. The returned <see cref="Firing"/> tells whether the firing was dropped
    /// and lets a trigger that wants to pace itself on the iterations await their completion.
    /// </param>
    /// <param name="cancellationToken">Signalled when the application shuts down.</param>
    Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken);
}
