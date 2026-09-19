using System.Globalization;

namespace Tripwire.Internal;

/// <summary>
/// A parsed standard cron expression (minute, hour, day of month, month, day of week) that finds the next
/// matching wall-clock time. Time zones are the concern of <see cref="Schedules.CronSchedule"/>.
/// </summary>
internal sealed class CronExpression
{
    private static readonly Dictionary<string, string> Macros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *",
        ["@monthly"] = "0 0 1 * *",
        ["@weekly"] = "0 0 * * 0",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@hourly"] = "0 * * * *",
    };

    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly ulong _minutes;
    private readonly ulong _hours;
    private readonly ulong _daysOfMonth;
    private readonly ulong _months;
    private readonly ulong _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronExpression(
        ulong minutes,
        ulong hours,
        ulong daysOfMonth,
        ulong months,
        ulong daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted,
        bool hourIsInterval)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
        HourIsInterval = hourIsInterval;
    }

    /// <summary>
    /// Whether the hour field is an interval (<c>*</c> or a step) rather than fixed hours. Decides how a
    /// wall-clock hour that occurs twice is handled: an interval runs in both instances, fixed hours once.
    /// </summary>
    public bool HourIsInterval { get; }

    /// <summary>
    /// Parses a five-field expression or a macro (<c>@daily</c>, <c>@hourly</c>…).
    /// </summary>
    /// <exception cref="ArgumentException">The expression is not valid.</exception>
    public static CronExpression Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var text = expression.Trim();

        if (Macros.TryGetValue(text, out var expanded))
        {
            text = expanded;
        }

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length != 5)
        {
            throw Invalid(expression, $"expected 5 fields (minute, hour, day of month, month, day of week), got {fields.Length}");
        }

        var minutes = ParseField(fields[0], "minute", 0, 59, null, expression);
        var hours = ParseField(fields[1], "hour", 0, 23, null, expression);
        var daysOfMonth = ParseField(fields[2], "day of month", 1, 31, null, expression);
        var months = ParseField(fields[3], "month", 1, 12, MonthNames, expression);
        var daysOfWeek = ParseField(fields[4], "day of week", 0, 7, DayNames, expression);

        // 7 is Sunday too. Bit 7 itself is never looked up, so it can stay.
        if (Has(daysOfWeek, 7))
        {
            daysOfWeek |= 1UL;
        }

        return new CronExpression(
            minutes,
            hours,
            daysOfMonth,
            months,
            daysOfWeek,
            dayOfMonthRestricted: !fields[2].StartsWith('*'),
            dayOfWeekRestricted: !fields[4].StartsWith('*'),
            hourIsInterval: fields[1].StartsWith('*') || fields[1].Contains('/'));
    }

    /// <summary>
    /// Returns the next matching wall-clock time strictly after <paramref name="afterLocal"/>, at minute
    /// resolution, or <see langword="null"/> when there is none within ten years (e.g. <c>0 0 31 2 *</c>).
    /// </summary>
    public DateTime? GetNextLocal(DateTime afterLocal)
    {
        var t = new DateTime(afterLocal.Year, afterLocal.Month, afterLocal.Day, afterLocal.Hour, afterLocal.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);

        // Ten years covers 29 February, up to eight years apart around a non-leap century year.
        var limit = afterLocal.AddYears(10);

        // Stryker disable once Equality : no expression has its next match exactly at the bound.
        while (t <= limit)
        {
            if (!Has(_months, t.Month))
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }

            if (!Has(_hours, t.Hour))
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
                continue;
            }

            if (!Has(_minutes, t.Minute))
            {
                t = t.AddMinutes(1);
                continue;
            }

            return t;
        }

        return null;
    }

    /// <summary>
    /// Standard cron rule: when both the day of month and the day of week are restricted, a day matches
    /// if either does; otherwise both must match (the unrestricted one always does).
    /// </summary>
    private bool DayMatches(DateTime t)
    {
        var dayOfMonth = Has(_daysOfMonth, t.Day);
        var dayOfWeek = Has(_daysOfWeek, (int)t.DayOfWeek);

        return _dayOfMonthRestricted && _dayOfWeekRestricted
            ? dayOfMonth || dayOfWeek
            : dayOfMonth && dayOfWeek;
    }

    private static bool Has(ulong mask, int value)
    {
        return (mask & (1UL << value)) != 0;
    }

    /// <summary>
    /// Parses one field: a comma-separated list of <c>*</c>, <c>*/step</c>, <c>value</c>, <c>value/step</c>,
    /// <c>low-high</c> or <c>low-high/step</c>, into a bit mask of the allowed values.
    /// </summary>
    private static ulong ParseField(string field, string name, int min, int max, string[]? names, string expression)
    {
        ulong mask = 0;

        foreach (var item in field.Split(','))
        {
            var parts = item.Split('/');

            if (item.Length == 0 || parts.Length > 2)
            {
                throw Invalid(expression, $"'{item}' is not valid in the {name} field");
            }

            var step = 1;

            if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1))
            {
                throw Invalid(expression, $"'{item}' has an invalid step in the {name} field");
            }

            int low, high;
            var range = parts[0];
            var dash = range.IndexOf('-');

            if (range == "*")
            {
                low = min;
                high = max;
            }
            // A leading dash is not a range: the token is rejected as a whole, by ParseValue.
            else if (dash > 0)
            {
                low = ParseValue(range[..dash], name, min, max, names, expression);
                high = ParseValue(range[(dash + 1)..], name, min, max, names, expression);

                if (low > high)
                {
                    throw Invalid(expression, $"'{range}' is not an increasing range in the {name} field");
                }
            }
            else
            {
                low = ParseValue(range, name, min, max, names, expression);
                // "value/step" means from value to the end of the field
                high = parts.Length == 2 ? max : low;
            }

            for (var value = low; value <= high; value += step)
            {
                mask |= 1UL << value;
            }
        }

        return mask;
    }

    private static int ParseValue(string token, string name, int min, int max, string[]? names, string expression)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            if (value < min || value > max)
            {
                throw Invalid(expression, $"{value} is out of range {min}-{max} in the {name} field");
            }

            return value;
        }

        if (names is not null)
        {
            var index = Array.FindIndex(names, n => n.Equals(token, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                // Month names start at 1, day names at 0: the field's minimum.
                return min + index;
            }
        }

        throw Invalid(expression, $"'{token}' is not a valid value in the {name} field");
    }

    private static ArgumentException Invalid(string expression, string reason)
    {
        return new ArgumentException($"Invalid cron expression '{expression}': {reason}.", nameof(expression));
    }
}
