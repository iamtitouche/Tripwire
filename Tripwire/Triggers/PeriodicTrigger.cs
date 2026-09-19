namespace Tripwire.Triggers;

/// <summary>
/// Runs the service at a fixed interval. The first run happens one interval after startup.
/// </summary>
public class PeriodicTrigger : IServiceTrigger
{
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;

    /// <param name="interval">Interval between two runs. Must be strictly positive.</param>
    /// <param name="timeProvider">Time source, replaceable in tests. <see cref="TimeProvider.System"/> by default.</param>
    public PeriodicTrigger(TimeSpan interval, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task StartAsync(Func<CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        using var timer = new PeriodicTimer(_interval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await execute(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
