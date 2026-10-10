using Tripwire.Schedules;
using Tripwire.Tests.Support;

namespace Tripwire.Tests.Schedules;

public class DailyScheduleTests
{
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

        Assert.Equal(TestDates.Jan1St0800Utc.AddHours(1), schedule.GetNextOccurrence(TestDates.Jan1St0800Utc));
    }

    [Fact]
    public void Next_occurrence_is_strictly_after_now()
    {
        var schedule = Create(null, new TimeOnly(8, 0), new TimeOnly(20, 0));

        Assert.Equal(TestDates.Jan1St0800Utc.AddHours(12), schedule.GetNextOccurrence(TestDates.Jan1St0800Utc));
    }

    [Fact]
    public void Next_occurrence_wraps_to_tomorrow()
    {
        var schedule = Create(null, new TimeOnly(6, 0));

        Assert.Equal(new DateTimeOffset(2024, 1, 2, 6, 0, 0, TimeSpan.Zero), schedule.GetNextOccurrence(TestDates.Jan1St0800Utc));
    }

    [Fact]
    public void Times_are_ordered_and_deduplicated()
    {
        var schedule = Create(null, new TimeOnly(20, 0), new TimeOnly(9, 0), new TimeOnly(9, 0));

        var first = schedule.GetNextOccurrence(TestDates.Jan1St0800Utc)!.Value;
        var second = schedule.GetNextOccurrence(first)!.Value;
        var third = schedule.GetNextOccurrence(second)!.Value;

        Assert.Equal(TestDates.Jan1St0800Utc.AddHours(1), first);
        Assert.Equal(TestDates.Jan1St0800Utc.AddHours(12), second);
        Assert.Equal(TestDates.Jan1St0800Utc.AddHours(25), third);
    }

    [Fact]
    public void Times_are_interpreted_in_the_given_time_zone()
    {
        // 01:00 in UTC+5 is 20:00 UTC the day before. At 08:00 UTC (13:00 local), the next one is 01:00 local tomorrow.
        var schedule = Create(TestTimeZones.UtcPlus5, new TimeOnly(1, 0));

        Assert.Equal(new DateTimeOffset(2024, 1, 1, 20, 0, 0, TimeSpan.Zero), schedule.GetNextOccurrence(TestDates.Jan1St0800Utc));
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
    public void Ambiguous_local_time_on_fall_back_runs_once_at_its_first_instance()
    {
        // 27 October 2024: 03:00 → 02:00, so 02:30 occurs twice.
        var schedule = Create(TestTimeZones.CentralEurope, new TimeOnly(2, 30));
        var now = new DateTimeOffset(2024, 10, 27, 0, 0, 0, TimeSpan.Zero);

        var first = schedule.GetNextOccurrence(now)!.Value;
        var second = schedule.GetNextOccurrence(first)!.Value;

        // 02:30 CEST (UTC+2) = 00:30 UTC, then 28 October 02:30 CET (UTC+1) = 01:30 UTC.
        Assert.Equal(new DateTimeOffset(2024, 10, 27, 0, 30, 0, TimeSpan.Zero), first);
        Assert.Equal(new DateTimeOffset(2024, 10, 28, 1, 30, 0, TimeSpan.Zero), second);
    }

    [Fact]
    public void Ambiguous_local_time_is_not_replayed_in_its_second_instance()
    {
        // 00:45 UTC is 02:45 CEST, past the first 02:30: the second one (01:30 UTC) does not run.
        var schedule = Create(TestTimeZones.CentralEurope, new TimeOnly(2, 30));
        var now = new DateTimeOffset(2024, 10, 27, 0, 45, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2024, 10, 28, 1, 30, 0, TimeSpan.Zero), schedule.GetNextOccurrence(now));
    }

    [Fact]
    public void Ambiguous_local_time_runs_in_its_second_instance_when_starting_there()
    {
        // 01:10 UTC is 02:10 CET, the second instance: the first 02:30 is gone, the second one is ahead.
        var schedule = Create(TestTimeZones.CentralEurope, new TimeOnly(2, 30));
        var now = new DateTimeOffset(2024, 10, 27, 1, 10, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2024, 10, 27, 1, 30, 0, TimeSpan.Zero), schedule.GetNextOccurrence(now));
    }

    [Fact]
    public void Ambiguous_local_time_matches_the_equivalent_cron_schedule()
    {
        var daily = Create(TestTimeZones.CentralEurope, new TimeOnly(2, 0), new TimeOnly(2, 30), new TimeOnly(2, 59));
        var cron = new CronSchedule("0,30,59 2 * * *", TestTimeZones.CentralEurope);

        for (var now = new DateTimeOffset(2024, 10, 26, 22, 0, 0, TimeSpan.Zero);
             now < new DateTimeOffset(2024, 10, 27, 3, 0, 0, TimeSpan.Zero);
             now = now.AddMinutes(1))
        {
            Assert.Equal(cron.GetNextOccurrence(now), daily.GetNextOccurrence(now));
        }
    }

    [Fact]
    public void Occurrences_are_returned_in_utc()
    {
        var schedule = Create(TestTimeZones.CentralEurope, new TimeOnly(9, 0));

        Assert.Equal(TimeSpan.Zero, schedule.GetNextOccurrence(TestDates.Jan1St0800Utc)!.Value.Offset);
    }

    [Fact]
    public void Describes_itself()
    {
        var schedule = Create(TestTimeZones.UtcPlus5, new TimeOnly(20, 0), new TimeOnly(9, 0));

        Assert.Equal("Daily(09:00, 20:00 Test/UTC+5)", schedule.ToString());
    }
}
