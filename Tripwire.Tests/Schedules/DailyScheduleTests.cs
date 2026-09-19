using Tripwire.Schedules;
using Tripwire.Tests.Support;

namespace Tripwire.Tests.Schedules;

public class DailyScheduleTests
{
    private static readonly DateTimeOffset Jan1_0800Utc = new(2024, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static DailySchedule Create(TimeZoneInfo? zone = null, params TimeOnly[] times)
    {
        return new DailySchedule(times, zone);
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Equal("times", Assert.Throws<ArgumentNullException>(() => new DailySchedule(null!)).ParamName);
        Assert.Equal("times", Assert.Throws<ArgumentException>(() => new DailySchedule([])).ParamName);
    }

    [Fact]
    public void Next_occurrence_is_the_next_time_today()
    {
        var schedule = Create(null, new TimeOnly(9, 0), new TimeOnly(20, 0));

        Assert.Equal(Jan1_0800Utc.AddHours(1), schedule.GetNextOccurrence(Jan1_0800Utc));
    }

    [Fact]
    public void Next_occurrence_is_strictly_after_now()
    {
        var schedule = Create(null, new TimeOnly(8, 0), new TimeOnly(20, 0));

        Assert.Equal(Jan1_0800Utc.AddHours(12), schedule.GetNextOccurrence(Jan1_0800Utc));
    }

    [Fact]
    public void Next_occurrence_wraps_to_tomorrow()
    {
        var schedule = Create(null, new TimeOnly(6, 0));

        Assert.Equal(new DateTimeOffset(2024, 1, 2, 6, 0, 0, TimeSpan.Zero), schedule.GetNextOccurrence(Jan1_0800Utc));
    }

    [Fact]
    public void Times_are_ordered_and_deduplicated()
    {
        var schedule = Create(null, new TimeOnly(20, 0), new TimeOnly(9, 0), new TimeOnly(9, 0));

        var first = schedule.GetNextOccurrence(Jan1_0800Utc)!.Value;
        var second = schedule.GetNextOccurrence(first)!.Value;
        var third = schedule.GetNextOccurrence(second)!.Value;

        Assert.Equal(Jan1_0800Utc.AddHours(1), first);
        Assert.Equal(Jan1_0800Utc.AddHours(12), second);
        Assert.Equal(Jan1_0800Utc.AddHours(25), third);
    }

    [Fact]
    public void Times_are_interpreted_in_the_given_time_zone()
    {
        // 01:00 in UTC+5 is 20:00 UTC the day before. At 08:00 UTC (13:00 local), the next one is 01:00 local tomorrow.
        var schedule = Create(TestTimeZones.UtcPlus5, new TimeOnly(1, 0));

        Assert.Equal(new DateTimeOffset(2024, 1, 1, 20, 0, 0, TimeSpan.Zero), schedule.GetNextOccurrence(Jan1_0800Utc));
    }

    [Fact]
    public void Invalid_local_time_on_spring_forward_is_skipped_for_that_day()
    {
        // 31 March 2024: 02:00 → 03:00, so 02:30 does not exist that day.
        var schedule = Create(TestTimeZones.CentralEurope, new TimeOnly(2, 30));
        var now = new DateTimeOffset(2024, 3, 31, 0, 0, 0, TimeSpan.Zero);

        var next = schedule.GetNextOccurrence(now);

        // 1 April 02:30 CEST (UTC+2) = 00:30 UTC.
        Assert.Equal(new DateTimeOffset(2024, 4, 1, 0, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Ambiguous_local_time_on_fall_back_runs_once()
    {
        // 27 October 2024: 03:00 → 02:00, so 02:30 occurs twice.
        var schedule = Create(TestTimeZones.CentralEurope, new TimeOnly(2, 30));
        var now = new DateTimeOffset(2024, 10, 27, 0, 0, 0, TimeSpan.Zero);

        var first = schedule.GetNextOccurrence(now)!.Value;
        var second = schedule.GetNextOccurrence(first)!.Value;

        Assert.Equal(new DateOnly(2024, 10, 27), DateOnly.FromDateTime(first.UtcDateTime));
        Assert.Equal(new DateOnly(2024, 10, 28), DateOnly.FromDateTime(second.UtcDateTime));
    }

    [Fact]
    public void Describes_itself()
    {
        var schedule = Create(TestTimeZones.UtcPlus5, new TimeOnly(20, 0), new TimeOnly(9, 0));

        Assert.Equal("Daily(09:00, 20:00 Test/UTC+5)", schedule.ToString());
    }
}
