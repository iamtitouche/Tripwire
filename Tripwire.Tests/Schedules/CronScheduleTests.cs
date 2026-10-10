using Tripwire.Schedules;
using Tripwire.Tests.Support;

namespace Tripwire.Tests.Schedules;

public class CronScheduleTests
{
    private static DateTimeOffset Utc(int month, int day, int hour, int minute, int second = 0)
    {
        return new DateTimeOffset(2024, month, day, hour, minute, second, TimeSpan.Zero);
    }

    /// <summary>
    /// The first <paramref name="count"/> occurrences after <paramref name="after"/>, each computed from the previous one.
    /// </summary>
    private static DateTimeOffset[] Next(CronSchedule schedule, DateTimeOffset after, int count)
    {
        var results = new List<DateTimeOffset>();
        var current = after;

        for (var i = 0; i < count; i++)
        {
            var next = schedule.GetNextOccurrence(current);

            if (next is null)
            {
                break;
            }

            results.Add(next.Value);
            current = next.Value;
        }

        return [.. results];
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Equal("expression", Assert.Throws<ArgumentNullException>(() => new CronSchedule(null!)).ParamName);
        Assert.Equal("expression", Assert.Throws<ArgumentException>(() => new CronSchedule("* * *")).ParamName);
    }

    [Fact]
    public void Interprets_the_expression_in_utc_by_default()
    {
        var schedule = new CronSchedule("0 8 * * *");

        Assert.Equal(TestDates.Jan1St0800Utc.AddDays(1), schedule.GetNextOccurrence(TestDates.Jan1St0800Utc));
    }

    [Fact]
    public void Interprets_the_expression_in_the_given_time_zone()
    {
        // 01:00 in UTC+5 is 20:00 UTC the day before. At 08:00 UTC (13:00 local), the next one is 01:00 local tomorrow.
        var schedule = new CronSchedule("0 1 * * *", TestTimeZones.UtcPlus5);

        Assert.Equal(Utc(1, 1, 20, 0), schedule.GetNextOccurrence(TestDates.Jan1St0800Utc));
    }

    [Fact]
    public void Returns_null_when_nothing_ever_matches()
    {
        var schedule = new CronSchedule("0 0 31 2 *", TestTimeZones.CentralEurope);

        Assert.Null(schedule.GetNextOccurrence(TestDates.Jan1St0800Utc));

        // Also from inside a repeated hour (27 October 2024, 00:30 UTC is 02:30 CEST, the first instance).
        Assert.Null(schedule.GetNextOccurrence(Utc(10, 27, 0, 30)));
    }

    // 31 March 2024, 02:00 CET → 03:00 CEST: 02:00–02:59 do not exist. 01:00 UTC is 03:00 CEST.

    [Fact]
    public void Fixed_time_in_a_skipped_hour_is_skipped_for_that_day()
    {
        var schedule = new CronSchedule("30 2 * * *", TestTimeZones.CentralEurope);

        // 1 April 02:30 CEST (UTC+2) = 00:30 UTC.
        Assert.Equal(Utc(4, 1, 0, 30), schedule.GetNextOccurrence(Utc(3, 31, 0, 0)));
    }

    [Fact]
    public void Interval_continues_across_a_skipped_hour()
    {
        var schedule = new CronSchedule("*/30 * * * *", TestTimeZones.CentralEurope);

        // 00:30 UTC is 01:30 CET; 02:00 and 02:30 do not exist; 03:00 CEST is 01:00 UTC.
        Assert.Equal([Utc(3, 31, 1, 0), Utc(3, 31, 1, 30)], Next(schedule, Utc(3, 31, 0, 30), 2));
    }

    // 27 October 2024, 03:00 CEST → 02:00 CET: 02:00–02:59 occur twice.
    // First instance (CEST, UTC+2): 00:00–00:59 UTC. Second instance (CET, UTC+1): 01:00–01:59 UTC. 03:00 CET is 02:00 UTC.

    [Fact]
    public void Fixed_time_in_a_repeated_hour_runs_once_at_its_first_instance()
    {
        var schedule = new CronSchedule("30 2 * * *", TestTimeZones.CentralEurope);

        // From 23:00 UTC the day before (01:00 CEST): 02:30 CEST, then 02:30 CET the next day.
        Assert.Equal([Utc(10, 27, 0, 30), Utc(10, 28, 1, 30)], Next(schedule, Utc(10, 26, 23, 0), 2));
    }

    [Fact]
    public void Fixed_hours_after_a_repeated_hour_are_not_replayed()
    {
        var schedule = new CronSchedule("0 3 * * *", TestTimeZones.CentralEurope);

        // From inside the first instance: 03:00 CET.
        Assert.Equal(Utc(10, 27, 2, 0), schedule.GetNextOccurrence(Utc(10, 27, 0, 30)));
    }

    [Fact]
    public void Interval_runs_in_both_instances_of_a_repeated_hour()
    {
        var schedule = new CronSchedule("*/15 * * * *", TestTimeZones.CentralEurope);

        DateTimeOffset[] expected =
        [
            Utc(10, 27, 0, 0), Utc(10, 27, 0, 15), Utc(10, 27, 0, 30), Utc(10, 27, 0, 45),
            Utc(10, 27, 1, 0), Utc(10, 27, 1, 15), Utc(10, 27, 1, 30), Utc(10, 27, 1, 45),
            Utc(10, 27, 2, 0),
        ];

        Assert.Equal(expected, Next(schedule, Utc(10, 26, 23, 50), expected.Length));
    }

    [Fact]
    public void Interval_resumes_from_inside_the_first_instance()
    {
        var schedule = new CronSchedule("*/15 * * * *", TestTimeZones.CentralEurope);

        Assert.Equal([Utc(10, 27, 0, 45), Utc(10, 27, 1, 0), Utc(10, 27, 1, 15)], Next(schedule, Utc(10, 27, 0, 30, 30), 3));
    }

    [Fact]
    public void Interval_resumes_from_inside_the_second_instance()
    {
        var schedule = new CronSchedule("*/15 * * * *", TestTimeZones.CentralEurope);

        Assert.Equal([Utc(10, 27, 1, 30), Utc(10, 27, 1, 45), Utc(10, 27, 2, 0)], Next(schedule, Utc(10, 27, 1, 20), 3));
    }

    [Fact]
    public void Every_minute_replays_the_repeated_hour_from_its_start()
    {
        var schedule = new CronSchedule("* * * * *", TestTimeZones.CentralEurope);

        // 02:58 CEST, 02:59 CEST, then the clock is set back: 02:00 CET, 02:01 CET.
        Assert.Equal([Utc(10, 27, 0, 58), Utc(10, 27, 0, 59), Utc(10, 27, 1, 0), Utc(10, 27, 1, 1)], Next(schedule, Utc(10, 27, 0, 57), 4));
    }

    [Fact]
    public void Hourly_runs_at_both_instances_of_a_repeated_hour()
    {
        var schedule = new CronSchedule("0 * * * *", TestTimeZones.CentralEurope);

        // 02:00 CEST, 02:00 CET, 03:00 CET.
        Assert.Equal([Utc(10, 27, 0, 0), Utc(10, 27, 1, 0), Utc(10, 27, 2, 0)], Next(schedule, Utc(10, 26, 23, 30), 3));
    }

    [Fact]
    public void Interval_with_no_match_in_the_repeated_hour_moves_on()
    {
        var schedule = new CronSchedule("0 */3 * * *", TestTimeZones.CentralEurope);

        // From inside the first instance of 02:xx, which hour 2 does not match: 03:00 CET.
        Assert.Equal(Utc(10, 27, 2, 0), schedule.GetNextOccurrence(Utc(10, 27, 0, 10)));
    }

    [Fact]
    public void Describes_itself()
    {
        var schedule = new CronSchedule("0 8 * * MON-FRI", TestTimeZones.UtcPlus5);

        Assert.Equal("0 8 * * MON-FRI", schedule.Expression);
        Assert.Equal("Cron(0 8 * * MON-FRI Test/UTC+5)", schedule.ToString());
    }
}
