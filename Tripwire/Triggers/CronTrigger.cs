using Tripwire.Schedules;

namespace Tripwire.Triggers;

/// <summary>
/// Runs the service on a standard cron expression, interpreted in UTC by default or in the given
/// <see cref="TimeZoneInfo"/>. Shorthand for a <see cref="ScheduleTrigger"/> over a <see cref="CronSchedule"/>,
/// which documents the supported syntax.
/// </summary>
public class CronTrigger : ScheduleTrigger
{
    /// <param name="expression">Five-field cron expression or macro, e.g. <c>0 8 * * MON-FRI</c>.</param>
    /// <param name="timeProvider">Time source, replaceable in tests. <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="timeZone">Time zone in which the expression is interpreted. UTC by default.</param>
    /// <exception cref="ArgumentException">The expression is not valid.</exception>
    public CronTrigger(string expression, TimeProvider? timeProvider = null, TimeZoneInfo? timeZone = null)
        : base(new CronSchedule(expression, timeZone), timeProvider)
    {
    }
}
