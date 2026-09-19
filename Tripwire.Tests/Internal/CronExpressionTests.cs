using System.Globalization;
using Tripwire.Internal;

namespace Tripwire.Tests.Internal;

public class CronExpressionTests
{
    private static DateTime At(string local)
    {
        return DateTime.Parse(local, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    /// <summary>
    /// The first <paramref name="count"/> matches after <paramref name="after"/>, as ISO strings for readable assertions.
    /// </summary>
    private static string[] Next(string expression, string after, int count)
    {
        var cron = CronExpression.Parse(expression);
        var results = new List<string>();
        var current = At(after);

        for (var i = 0; i < count; i++)
        {
            var next = cron.GetNextLocal(current);

            if (next is null)
            {
                break;
            }

            results.Add(next.Value.ToString("s", CultureInfo.InvariantCulture));
            current = next.Value;
        }

        return [.. results];
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.Equal("expression", Assert.Throws<ArgumentNullException>(() => CronExpression.Parse(null!)).ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 0 *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/a * * * *")]
    [InlineData("1/2/3 * * * *")]
    [InlineData("10-5 * * * *")]
    [InlineData("1- * * * *")]
    [InlineData("-1 * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("MON * * * *")]
    [InlineData("* * * FOO *")]
    [InlineData("* * * * SAT-SUN")]
    [InlineData("@nope")]
    public void Rejects_invalid_expressions(string expression)
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpression.Parse(expression));

        Assert.Equal("expression", ex.ParamName);
        Assert.Contains(expression, ex.Message);
    }

    [Theory]
    [InlineData("* * 32 * *", "32 is out of range 1-31 in the day of month field")]
    [InlineData("-1 * * * *", "'-1' is not a valid value in the minute field")]
    [InlineData("*/0 * * * *", "'*/0' has an invalid step in the minute field")]
    [InlineData("10-5 * * * *", "'10-5' is not an increasing range in the minute field")]
    [InlineData("* * * *", "expected 5 fields (minute, hour, day of month, month, day of week), got 4")]
    public void Error_names_the_field_and_the_token(string expression, string reason)
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpression.Parse(expression));

        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    // Every minute, seconds truncated.
    [InlineData("* * * * *", "2024-01-01T08:00:30", "2024-01-01T08:01:00")]
    // Steps.
    [InlineData("*/15 * * * *", "2024-01-01T08:00:00", "2024-01-01T08:15:00", "2024-01-01T08:30:00", "2024-01-01T08:45:00", "2024-01-01T09:00:00")]
    [InlineData("5/20 * * * *", "2024-01-01T08:00:00", "2024-01-01T08:05:00", "2024-01-01T08:25:00", "2024-01-01T08:45:00", "2024-01-01T09:05:00")]
    [InlineData("1-30/10 * * * *", "2024-01-01T08:00:00", "2024-01-01T08:01:00", "2024-01-01T08:11:00", "2024-01-01T08:21:00", "2024-01-01T09:01:00")]
    [InlineData("*/1 * * * *", "2024-01-01T08:00:00", "2024-01-01T08:01:00", "2024-01-01T08:02:00")]
    // Degenerate range and duplicates.
    [InlineData("5-5 * * * *", "2024-01-01T08:00:00", "2024-01-01T08:05:00", "2024-01-01T09:05:00")]
    [InlineData("1,1 * * * *", "2024-01-01T08:00:00", "2024-01-01T08:01:00", "2024-01-01T09:01:00")]
    [InlineData("1-3,2 * * * *", "2024-01-01T08:00:00", "2024-01-01T08:01:00", "2024-01-01T08:02:00", "2024-01-01T08:03:00")]
    // Lists and ranges.
    [InlineData("0 9-17 * * *", "2024-01-01T08:00:00", "2024-01-01T09:00:00")]
    [InlineData("0 9-17 * * *", "2024-01-01T17:00:00", "2024-01-02T09:00:00")]
    [InlineData("0 8,20 * * *", "2024-01-01T08:00:00", "2024-01-01T20:00:00", "2024-01-02T08:00:00")]
    // Days of week: 1 January 2024 is a Monday, 5 January a Friday.
    [InlineData("0 8 * * MON-FRI", "2024-01-05T09:00:00", "2024-01-08T08:00:00")]
    [InlineData("0 8 * * mon-fri", "2024-01-05T09:00:00", "2024-01-08T08:00:00")]
    [InlineData("0 8 * * 1-5", "2024-01-05T09:00:00", "2024-01-08T08:00:00")]
    [InlineData("0 0 * * 0", "2024-01-01T00:00:00", "2024-01-07T00:00:00")]
    [InlineData("0 0 * * 7", "2024-01-01T00:00:00", "2024-01-07T00:00:00")]
    [InlineData("0 0 * * SUN", "2024-01-01T00:00:00", "2024-01-07T00:00:00")]
    [InlineData("0 0 * * 5-7", "2024-01-01T00:00:00", "2024-01-05T00:00:00", "2024-01-06T00:00:00", "2024-01-07T00:00:00", "2024-01-12T00:00:00")]
    // Months.
    [InlineData("0 0 1 * *", "2024-12-15T00:00:00", "2025-01-01T00:00:00")]
    [InlineData("0 0 1 JAN,JUL *", "2024-02-01T00:00:00", "2024-07-01T00:00:00", "2025-01-01T00:00:00")]
    [InlineData("0 0 29 2 *", "2024-03-01T00:00:00", "2028-02-29T00:00:00")]
    [InlineData("0 0 31 * *", "2024-01-31T00:00:00", "2024-03-31T00:00:00", "2024-05-31T00:00:00")]
    // Day of month and day of week both restricted: either matches (the 13th or a Friday).
    [InlineData("0 0 13 * FRI", "2024-01-01T00:00:00", "2024-01-05T00:00:00", "2024-01-12T00:00:00", "2024-01-13T00:00:00", "2024-01-19T00:00:00")]
    // A day of month starting with * is not restricted: both must match (an odd day that is a Friday).
    [InlineData("0 0 */2 * FRI", "2024-01-01T00:00:00", "2024-01-05T00:00:00", "2024-01-19T00:00:00")]
    // Macros.
    [InlineData("@hourly", "2024-01-01T08:30:00", "2024-01-01T09:00:00")]
    [InlineData("@daily", "2024-01-01T08:30:00", "2024-01-02T00:00:00")]
    [InlineData("@midnight", "2024-01-01T08:30:00", "2024-01-02T00:00:00")]
    [InlineData("@weekly", "2024-01-01T08:30:00", "2024-01-07T00:00:00")]
    [InlineData("@monthly", "2024-01-01T08:30:00", "2024-02-01T00:00:00")]
    [InlineData("@yearly", "2024-01-01T08:30:00", "2025-01-01T00:00:00")]
    [InlineData("@annually", "2024-01-01T08:30:00", "2025-01-01T00:00:00")]
    [InlineData("@DAILY", "2024-01-01T08:30:00", "2024-01-02T00:00:00")]
    // Surrounding and repeated whitespace.
    [InlineData("  0   8  *  *  * ", "2024-01-01T08:00:00", "2024-01-02T08:00:00")]
    public void Finds_the_next_matches(string expression, string after, params string[] expected)
    {
        Assert.Equal(expected, Next(expression, after, expected.Length));
    }

    [Fact]
    public void Returns_null_when_nothing_ever_matches()
    {
        Assert.Null(CronExpression.Parse("0 0 31 2 *").GetNextLocal(At("2024-01-01T00:00:00")));
    }

    [Fact]
    public void Result_is_strictly_after_and_at_minute_resolution()
    {
        var next = CronExpression.Parse("0 8 * * *").GetNextLocal(At("2024-01-01T08:00:00"));

        Assert.Equal(At("2024-01-02T08:00:00"), next);
        Assert.Equal(DateTimeKind.Unspecified, next!.Value.Kind);
    }

    [Theory]
    [InlineData("* * * * *", true)]
    [InlineData("0 */2 * * *", true)]
    [InlineData("0 8-17/3 * * *", true)]
    [InlineData("0 8 * * *", false)]
    [InlineData("0 8-17 * * *", false)]
    [InlineData("0 8,20 * * *", false)]
    public void Hour_is_an_interval_when_the_field_is_a_star_or_has_a_step(string expression, bool expected)
    {
        Assert.Equal(expected, CronExpression.Parse(expression).HourIsInterval);
    }
}
