namespace Tripwire.Triggers;

/// <summary>
/// Runs the service with a fixed delay between the end of an iteration and the start of the next, however
/// long the iterations take: a slow iteration pushes the next one back instead of piling firings up.
/// Unlike <see cref="PeriodicTrigger"/>, which fires at a fixed rate, this trigger's own runs never overlap
/// each other.
/// </summary>
/// <remarks>
/// <para>
/// That guarantee only covers its own firings. In a <see cref="CompositeTrigger"/>, the other triggers keep
/// firing on their own clock, and the <see cref="OverlapPolicy"/> queues their firings behind its run, drops
/// them, or runs them alongside it, as usual.
/// </para>
/// <para>
/// The delay starts when the firing is over (<see cref="Firing.Completion"/>): after the iteration, whether it
/// succeeded or failed, including the time the firing spent queued behind another trigger's iteration. A
/// firing dropped by the <see cref="OverlapPolicy"/> (possible only in a <see cref="CompositeTrigger"/>) simply
/// starts the next delay.
/// </para>
/// <para>
/// Retries scheduled by <see cref="FailureHandlingMode.Retry"/> are independent firings: the delay does not wait
/// for them, and they do not reset it.
/// </para>
/// </remarks>
public class FixedDelayTrigger : IServiceTrigger
{
    /// <summary>
    /// Longest delay <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> accepts, about 49 days.
    /// </summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly TimeSpan _delay;
    private readonly TimeSpan _initialDelay;
    private readonly TimeProvider _timeProvider;

    /// <param name="delay">
    /// Time to wait after the end of an iteration before the next one. Must be strictly positive, and at most
    /// about 49 days.
    /// </param>
    /// <param name="initialDelay">
    /// Time to wait after startup before the first iteration. <paramref name="delay"/> by default, as the first
    /// run of a <see cref="PeriodicTrigger"/>; <see cref="TimeSpan.Zero"/> runs it right away.
    /// </param>
    /// <param name="timeProvider">Time source, replaceable in tests. <see cref="TimeProvider.System"/> by default.</param>
    public FixedDelayTrigger(TimeSpan delay, TimeSpan? initialDelay = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay, MaxDelay);

        var actualInitialDelay = initialDelay ?? delay;
        ArgumentOutOfRangeException.ThrowIfLessThan(actualInitialDelay, TimeSpan.Zero, nameof(initialDelay));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(actualInitialDelay, MaxDelay, nameof(initialDelay));

        _delay = delay;
        _initialDelay = actualInitialDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        try
        {
            await Task.Delay(_initialDelay, _timeProvider, cancellationToken);

            while (true)
            {
                var firing = await execute(cancellationToken);
                await firing.Completion.WaitAsync(cancellationToken);
                await Task.Delay(_delay, _timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
