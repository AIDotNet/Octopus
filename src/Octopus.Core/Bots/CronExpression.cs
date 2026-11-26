namespace Octopus.Core.Bots;

/// <summary>
/// A standard 5-field cron expression (<c>minute hour day-of-month month day-of-week</c>) evaluated in local time,
/// the format Claude Code's <c>CronCreate</c> tool uses. Supports <c>*</c>, lists, ranges, steps, and
/// three-letter month/weekday names. When both day fields are restricted, a day matches either (Vixie cron).
/// </summary>
public sealed class CronExpression
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _days;
    private readonly bool[] _months;
    private readonly bool[] _weekdays;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronExpression(string text, string[] fields, bool[] minutes, bool[] hours, bool[] days, bool[] months, bool[] weekdays)
    {
        Text = text;
        Fields = fields;
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
        _dayOfMonthRestricted = !fields[2].StartsWith('*');
        _dayOfWeekRestricted = !fields[4].StartsWith('*');
    }

    /// <summary>The expression as written.</summary>
    public string Text { get; }

    /// <summary>The five raw fields: minute, hour, day-of-month, month, day-of-week.</summary>
    public IReadOnlyList<string> Fields { get; }

    public static bool TryParse(string? text, out CronExpression? expression)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;

        var minutes = new bool[60];
        var hours = new bool[24];
        var days = new bool[32];
        var months = new bool[13];
        var weekdays = new bool[8];
        if (!TryParseField(fields[0], 0, 59, null, minutes)
            || !TryParseField(fields[1], 0, 23, null, hours)
            || !TryParseField(fields[2], 1, 31, null, days)
            || !TryParseField(fields[3], 1, 12, MonthNames, months)
            || !TryParseField(fields[4], 0, 7, DayNames, weekdays))
        {
            return false;
        }
        // 7 is an alias for Sunday.
        if (weekdays[7]) weekdays[0] = true;

        expression = new CronExpression(text.Trim(), fields, minutes, hours, days, months, weekdays[..7]);
        return true;
    }

    /// <summary>The first matching minute strictly after <paramref name="after"/>, or null when none within five years.</summary>
    public DateTime? Next(DateTime after)
    {
        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var limit = t.AddYears(5);
        while (t < limit)
        {
            if (!_months[t.Month])
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind).AddMonths(1);
                continue;
            }
            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }
            if (!_hours[t.Hour])
            {
                t = t.Date.AddHours(t.Hour + 1);
                continue;
            }
            if (!_minutes[t.Minute])
            {
                t = t.AddMinutes(1);
                continue;
            }
            return t;
        }
        return null;
    }

    /// <summary>The first matching local time strictly after <paramref name="after"/>.</summary>
    public DateTimeOffset? Next(DateTimeOffset after)
    {
        if (Next(after.ToLocalTime().DateTime) is not { } local) return null;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private bool DayMatches(DateTime t)
    {
        var dom = _days[t.Day];
        var dow = _weekdays[(int)t.DayOfWeek];
        return _dayOfMonthRestricted && _dayOfWeekRestricted ? dom || dow : dom && dow;
    }

    private static bool TryParseField(string field, int min, int max, string[]? names, bool[] target)
    {
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0) return false;
            var rangePart = part;
            var step = 1;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0) return false;
                rangePart = part[..slash];
            }

            int start, end;
            if (rangePart == "*")
            {
                start = min;
                end = max;
            }
            else
            {
                var dash = rangePart.IndexOf('-');
                if (dash > 0)
                {
                    if (!TryParseValue(rangePart[..dash], names, out start) || !TryParseValue(rangePart[(dash + 1)..], names, out end)) return false;
                }
                else
                {
                    if (!TryParseValue(rangePart, names, out start)) return false;
                    // "5/15" means "from 5 to the end, every 15".
                    end = slash >= 0 ? max : start;
                }
            }

            if (start < min || end > max || start > end) return false;
            for (var v = start; v <= end; v += step)
            {
                target[v] = true;
            }
        }
        return true;
    }

    private static bool TryParseValue(string text, string[]? names, out int value)
    {
        if (int.TryParse(text, out value)) return true;
        if (names is not null)
        {
            var idx = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                // Month names are 1-based; weekday names are 0-based.
                value = names.Length == 12 ? idx + 1 : idx;
                return true;
            }
        }
        value = 0;
        return false;
    }
}
