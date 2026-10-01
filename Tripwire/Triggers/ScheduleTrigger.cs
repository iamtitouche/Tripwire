using Tripwire.Schedules;

namespace Tripwire.Triggers;

/// <summary>
/// Runs the service at the occurrences of an <see cref="ISchedule"/>: every day at given times
/// (<see cref="DailySchedule"/>), on a cron expression (<see cref="CronSchedule"/>), or any schedule of your own.
/// </summary>
public class ScheduleTrigger : IServiceTrigger
{
    /// <summary>
    /// Longest single wait: <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> caps at about
    /// 49 days, which a yearly schedule exceeds, so the wait for a far occurrence is made in chunks.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private readonly ISchedule _schedule;
    private readonly TimeProvider _timeProvider;

    /// <param name="schedule">Computes the occurrences.</param>
    /// <param name="timeProvider">Time source, replaceable in tests. <see cref="TimeProvider.System"/> by default.</param>
    public ScheduleTrigger(ISchedule schedule, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        _schedule = schedule;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs the service every day at the given times, interpreted in UTC by default or in the given
    /// <see cref="TimeZoneInfo"/>. Shorthand for a <see cref="DailySchedule"/>.
    /// </summary>
    /// <param name="times">Daily run times. At least one is required; duplicates are ignored.</param>
    /// <param name="timeProvider">Time source, replaceable in tests. <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="timeZone">Time zone in which <paramref name="times"/> are interpreted. UTC by default.</param>
    public ScheduleTrigger(
        IEnumerable<TimeOnly> times,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null)
        : this(new DailySchedule(times, timeZone), timeProvider)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// Completes when the schedule has no next occurrence, like <see cref="OnStartupTrigger"/> once it has fired.
    /// </remarks>
    public async Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var next = _schedule.GetNextOccurrence(_timeProvider.GetUtcNow());

                if (next is null)
                {
                    return;
                }

                await DelayUntilAsync(next.Value, cancellationToken);
                await execute(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DelayUntilAsync(DateTimeOffset instant, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = instant - _timeProvider.GetUtcNow();

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            // Stryker disable once Equality : at exactly MaxWait both branches yield the same value.
            await Task.Delay(remaining < MaxWait ? remaining : MaxWait, _timeProvider, cancellationToken);
        }
    }
}
