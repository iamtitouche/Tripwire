namespace Tripwire.Schedules;

/// <summary>
/// Occurs every day at the given times.
/// Times are interpreted in UTC by default, or in the given <see cref="TimeZoneInfo"/>.
/// </summary>
/// <remarks>
/// Daylight saving transitions are handled: a time that does not exist on a given day (spring forward)
/// is skipped for that day, and a time that occurs twice (fall back) occurs once.
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

                var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _timeZone));

                if (utc > after)
                {
                    return utc;
                }
            }
        }

        // Stryker disable once all : unreachable, a valid occurrence always exists within the scanned days.
        throw new InvalidOperationException("Unable to compute the next occurrence.");
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"Daily({string.Join(", ", _times.Select(t => t.ToString("HH:mm")))} {_timeZone.Id})";
    }
}
