using System.Globalization;

namespace Planna.Infrastructure.Tasks;

// Minimal RFC 5545 subset: FREQ=DAILY|WEEKLY|MONTHLY with INTERVAL, COUNT/UNTIL and WEEKLY BYDAY.
// Unsupported parts are rejected instead of silently normalized.
public static class RecurrenceExpander
{
    public sealed record Rule(string Frequency, int Interval, int? Count, DateTimeOffset? Until,
        IReadOnlySet<DayOfWeek> ByDay);

    private static readonly Dictionary<string, DayOfWeek> Weekdays = new()
    {
        ["MO"] = DayOfWeek.Monday,
        ["TU"] = DayOfWeek.Tuesday,
        ["WE"] = DayOfWeek.Wednesday,
        ["TH"] = DayOfWeek.Thursday,
        ["FR"] = DayOfWeek.Friday,
        ["SA"] = DayOfWeek.Saturday,
        ["SU"] = DayOfWeek.Sunday
    };

    public static bool TryParse(string rrule, out Rule rule)
    {
        rule = new Rule("", 1, null, null, new HashSet<DayOfWeek>());
        if (string.IsNullOrWhiteSpace(rrule) || rrule.Length > 1000) return false;
        string? frequency = null;
        var interval = 1;
        int? count = null;
        DateTimeOffset? until = null;
        var byDay = new HashSet<DayOfWeek>();
        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || pair[1].Length == 0) return false;
            var key = pair[0].ToUpperInvariant();
            var value = pair[1].ToUpperInvariant();
            switch (key)
            {
                case "FREQ":
                    if (value is not ("DAILY" or "WEEKLY" or "MONTHLY")) return false;
                    frequency = value;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out interval) ||
                        interval is < 1 or > 365) return false;
                    break;
                case "COUNT":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount) ||
                        parsedCount is < 1 or > 1000) return false;
                    count = parsedCount;
                    break;
                case "UNTIL":
                    if (!DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedUntil))
                        return false;
                    until = parsedUntil;
                    break;
                case "BYDAY":
                    foreach (var token in value.Split(','))
                    {
                        if (!Weekdays.TryGetValue(token, out var day)) return false;
                        byDay.Add(day);
                    }
                    if (byDay.Count == 0) return false;
                    break;
                case "BYMONTHDAY":
                case "BYSETPOS":
                case "WKST":
                case "BYMONTH":
                case "BYYEARDAY":
                case "BYWEEKNO":
                    return false;
                default:
                    return false;
            }
        }
        if (frequency is null || count is not null && until is not null) return false;
        if (byDay.Count > 0 && frequency != "WEEKLY") return false;
        rule = new Rule(frequency, interval, count, until, byDay);
        return true;
    }

    // Returns local start times in chronological order within [fromLocal, toLocalExclusive).
    public static IReadOnlyList<DateTime> Expand(Rule rule, DateTime startLocal, DateTime fromLocal,
        DateTime toLocalExclusive, TimeZoneInfo zone)
    {
        var result = new List<DateTime>();
        var generated = 0;
        foreach (var candidate in Candidates(rule, startLocal))
        {
            if (++generated > 5000 || (rule.Count is { } count && generated > count)) break;
            if (candidate >= toLocalExclusive) break;
            if (rule.Until is { } until && TryResolveInstant(zone, candidate, out var instant) && instant > until) break;
            if (candidate >= fromLocal) result.Add(candidate);
        }
        return result;
    }

    private static IEnumerable<DateTime> Candidates(Rule rule, DateTime start)
    {
        switch (rule.Frequency)
        {
            case "DAILY":
                for (var day = start; ; day = day.AddDays(rule.Interval))
                    yield return day;
            case "WEEKLY":
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                var days = rule.ByDay.Count > 0 ? rule.ByDay : new HashSet<DayOfWeek> { start.DayOfWeek };
                for (var week = 0; ; week++)
                {
                    var baseDate = weekStart.AddDays(7 * rule.Interval * week);
                    foreach (var day in days.Select(d => baseDate.AddDays(((int)d + 6) % 7)).OrderBy(x => x))
                        if (day >= start.Date)
                            yield return day.Add(start.TimeOfDay);
                    if (week >= 2000) yield break;
                }
            case "MONTHLY":
                for (var month = 0; ; month++)
                {
                    var anchor = new DateTime(start.Year, start.Month, 1).AddMonths(rule.Interval * month);
                    if (start.Day > DateTime.DaysInMonth(anchor.Year, anchor.Month)) continue;
                    yield return new DateTime(anchor.Year, anchor.Month, start.Day).Add(start.TimeOfDay);
                }
            default:
                yield break;
        }
    }

    // DST policy: nonexistent local times are skipped; ambiguous local times yield one occurrence at the earliest instant.
    public static bool TryResolveInstant(TimeZoneInfo zone, DateTime local, out DateTimeOffset instant)
    {
        instant = default;
        if (zone.IsInvalidTime(local)) return false;
        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            instant = new DateTimeOffset(local, offset).ToUniversalTime();
            return true;
        }
        instant = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        return true;
    }
}
