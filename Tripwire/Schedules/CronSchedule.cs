using Tripwire.Internal;

namespace Tripwire.Schedules;

/// <summary>
/// Occurs according to a standard five-field cron expression
/// (<c>minute hour day-of-month month day-of-week</c>), interpreted in UTC by default or in the given
/// <see cref="TimeZoneInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// Fields accept <c>*</c>, lists (<c>1,15</c>), ranges (<c>MON-FRI</c>), steps (<c>*/15</c>, <c>1-30/5</c>,
/// <c>5/10</c>) and three-letter names for months and days of week (case-insensitive). <c>0</c> and <c>7</c>
/// both mean Sunday. When both the day of month and the day of week are restricted, a day matches if either
/// does, as in Vixie cron. The macros <c>@yearly</c> (<c>@annually</c>),
/// <c>@monthly</c>, <c>@weekly</c>, <c>@daily</c> (<c>@midnight</c>) and <c>@hourly</c> are accepted.
/// </para>
/// <para>
/// Daylight saving transitions are handled: a wall-clock time that does not exist (spring forward) is skipped,
/// and a wall-clock time that occurs twice (fall back) occurs once for an expression with fixed hours
/// (<c>30 2 * * *</c>), at its first instance, and in both instances for an expression with an interval in
/// the hour field (<c>*/15 * * * *</c>, <c>0 * * * *</c>), so that the interval is kept.
/// </para>
/// </remarks>
public sealed class CronSchedule : ISchedule
{
    private readonly CronExpression _expression;
    private readonly TimeZoneInfo _timeZone;

    /// <param name="expression">Five-field cron expression or macro.</param>
    /// <param name="timeZone">Time zone in which the expression is interpreted. UTC by default.</param>
    /// <exception cref="ArgumentException">The expression is not valid.</exception>
    public CronSchedule(string expression, TimeZoneInfo? timeZone = null)
    {
        _expression = CronExpression.Parse(expression);
        Expression = expression;
        _timeZone = timeZone ?? TimeZoneInfo.Utc;
    }

    /// <summary>
    /// The expression as given.
    /// </summary>
    public string Expression { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="null"/> for an expression that never matches (e.g. <c>0 0 31 2 *</c>).
    /// </remarks>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        var offset = _timeZone.GetUtcOffset(after);
        var local = after.ToOffset(offset).DateTime;

        if (!_timeZone.IsAmbiguousTime(local))
        {
            // The usual case. A repeated hour ahead is entered at its first instance.
            return Search(local, firstInstance: true);
        }

        if (offset == _timeZone.GetAmbiguousTimeOffsets(local).Min())
        {
            // Inside the second instance of a repeated hour: nothing of the first one is left to run.
            return Search(local, firstInstance: false);
        }

        // Inside the first instance of a repeated hour.
        var next = NextValidLocal(local);

        if (next is null)
        {
            return null;
        }

        if (_timeZone.IsAmbiguousTime(next.Value) || !_expression.HourIsInterval)
        {
            return ToUtc(next.Value, firstInstance: true);
        }

        // The wall clock is about to be set back and the expression is an interval: replay the repeated
        // hour from its start, in its second instance.
        var windowStart = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);

        while (_timeZone.IsAmbiguousTime(windowStart.AddMinutes(-1)))
        {
            windowStart = windowStart.AddMinutes(-1);
        }

        return Search(windowStart.AddMinutes(-1), firstInstance: false);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"Cron({Expression} {_timeZone.Id})";
    }

    private DateTimeOffset? Search(DateTime from, bool firstInstance)
    {
        var next = NextValidLocal(from);
        return next is null ? null : ToUtc(next.Value, firstInstance);
    }

    /// <summary>
    /// Next match of the expression that exists in the time zone: a match falling in a skipped hour
    /// (spring forward) is passed over.
    /// </summary>
    private DateTime? NextValidLocal(DateTime from)
    {
        var candidate = from;

        while (true)
        {
            var next = _expression.GetNextLocal(candidate);

            if (next is null || !_timeZone.IsInvalidTime(next.Value))
            {
                return next;
            }

            candidate = next.Value;
        }
    }

    private DateTimeOffset ToUtc(DateTime local, bool firstInstance)
    {
        TimeSpan offset;

        if (_timeZone.IsAmbiguousTime(local))
        {
            // The first instance of a repeated hour is the one with the larger offset (daylight time).
            var offsets = _timeZone.GetAmbiguousTimeOffsets(local);
            offset = firstInstance ? offsets.Max() : offsets.Min();
        }
        else
        {
            offset = _timeZone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
