namespace Planna.Application.Calendar;

// Pure workday evaluation. No legal holiday data is bundled: callers pass owner overrides,
// otherwise Monday-Friday is a workday and Saturday/Sunday is a holiday.
public static class WorkdayCalendar
{
    public const string Holiday = "holiday";
    public const string Workday = "workday";

    public static bool IsWorkday(DateOnly date, IReadOnlyDictionary<DateOnly, string> overrides)
    {
        if (overrides.TryGetValue(date, out var kind)) return kind == Workday;
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    public static int CountWorkdays(DateOnly from, DateOnly to,
        IReadOnlyDictionary<DateOnly, string> overrides)
    {
        if (from > to) return 0;
        var count = 0;
        for (var date = from; date <= to; date = date.AddDays(1))
            if (IsWorkday(date, overrides)) count++;
        return count;
    }

    public static DateOnly? AddWorkdays(DateOnly start, int workdays,
        IReadOnlyDictionary<DateOnly, string> overrides)
    {
        if (workdays < 0) return null;
        var date = start;
        var remaining = workdays;
        while (remaining > 0)
        {
            date = date.AddDays(1);
            if (IsWorkday(date, overrides)) remaining--;
            if (date.Year - start.Year > 5) return null;
        }
        return date;
    }
}
