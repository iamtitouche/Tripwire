namespace Tripwire.Schedules;

/// <summary>
/// Occurs every day at the given times.
/// Times are interpreted in UTC by default, or in the given <see cref="TimeZoneInfo"/>.
/// </summary>
/// <remarks>
/// Daylight saving transitions are handled: a time that does not exist on a given day (spring forward)
/// is skipped for that day, and a time that occurs twice (fall back) occurs once, at its first instance.
/// </remarks>
public sealed class DailySchedule : ISchedule
{
    private readonly TimeOnly[] _times;
    private readonly TimeZoneInfo _timeZone;

    /// <param name="times">Daily times. At least one is required; duplicates are ignored.</param>
    /// <param name="timeZone">Time zone in which <paramref name="times"/> are interpreted. UTC by default.</param>
    public DailySchedule(IEnumerable<TimeOnly> times, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(times);

        _times = times.Distinct().Order().ToArray();

        if (_times.Length == 0)
        {
            throw new ArgumentException("At least one time is required.", nameof(times));
        }

        _timeZone = timeZone ?? TimeZoneInfo.Utc;
    }

    /// <inheritdoc />
    /// <remarks>Never returns <see langword="null"/>: there is always a next day.</remarks>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, _timeZone).DateTime);

        // Inside the second instance of a repeated hour, nothing of the first one is left to run: a repeated
        // time ahead is taken in its second instance.
        var firstInstance = !_timeZone.IsAmbiguousTime(after)
            || _timeZone.GetUtcOffset(after) != _timeZone.GetAmbiguousTimeOffsets(after).Min();

        // Scan a few days to cover invalid local times (daylight saving transitions) and time zone
        // offsets that can push an occurrence to the next day.
        // Stryker disable once Equality : scanning one more day never changes the result.
        for (var dayOffset = 0; dayOffset < 3; dayOffset++)
        {
            var date = localToday.AddDays(dayOffset);

            foreach (var time in _times)
            {
                var local = date.ToDateTime(time, DateTimeKind.Unspecified);

                if (_timeZone.IsInvalidTime(local))
                {
                    continue;
                }

                var utc = ToUtc(local, firstInstance);

                if (utc > after)
                {
                    return utc;
                }
            }
        }

        // Stryker disable once all : unreachable, a valid occurrence always exists within the scanned days.
        throw new InvalidOperationException("Unable to compute the next occurrence.");
    }

    /// <summary>
    /// Converts a valid local time to UTC, choosing the instance of a time that occurs twice as
    /// <see cref="CronSchedule"/> does. <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>
    /// would always pick the second one.
    /// </summary>
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

    /// <inheritdoc />
    public override string ToString()
    {
        return $"Daily({string.Join(", ", _times.Select(t => t.ToString("HH:mm")))} {_timeZone.Id})";
    }
}
