using System.Globalization;
using System.Text.RegularExpressions;
using Planna.Application.Ai;
using Planna.Infrastructure.Tasks;

namespace Planna.Infrastructure.Ai;

// Deterministic first-stage parser ("先规则解析"): extracts a single date/time phrase and the remaining title.
// It never interprets instructions and never mutates data; output is a draft for user confirmation.
public sealed partial class RuleBasedParseProvider : IAiParseProvider
{
    public Task<AiParseResponse> ParseAsync(AiParseInput input, CancellationToken ct)
    {
        var text = input.Text.Trim();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(input.TimeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(input.Now, zone);
        var assumptions = new List<string>();
        var unresolved = new List<string>();

        DateTime? date = null;
        var dateToken = string.Empty;
        var explicitDate = ExplicitDate().Match(text);
        if (explicitDate.Success)
        {
            if (DateTime.TryParseExact(explicitDate.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                date = parsed;
                dateToken = explicitDate.Value;
            }
            else unresolved.Add("due_date");
        }
        else
        {
            var relative = RelativeDay().Match(text);
            if (relative.Success)
            {
                date = nowLocal.Date.AddDays(relative.Value switch
                {
                    "今天" => 0,
                    "明天" => 1,
                    _ => 2
                });
                dateToken = relative.Value;
                assumptions.Add("relative_date_from_context_now");
            }
            else
            {
                var nextWeek = NextWeekday().Match(text);
                if (nextWeek.Success)
                {
                    var offset = "一二三四五六日天".IndexOf(nextWeek.Groups[1].Value[0]);
                    var monday = nowLocal.Date.AddDays(-(((int)nowLocal.DayOfWeek + 6) % 7));
                    date = monday.AddDays(7 + (offset == 6 ? 6 : offset));
                    dateToken = nextWeek.Value;
                    assumptions.Add("relative_date_from_context_now");
                }
            }
        }

        TimeSpan? time = null;
        var timeToken = string.Empty;
        var clock = Clock().Match(text);
        if (clock.Success)
        {
            var period = clock.Groups[1].Value;
            var hour = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            var minute = clock.Groups[3].Success
                ? int.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture)
                : clock.Groups[4].Success ? 30 : 0;
            if (period is "下午" or "晚上" && hour < 12) hour += 12;
            else if (period == "中午" && hour < 12) hour += 12;
            if (hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
            {
                time = new TimeSpan(hour, minute, 0);
                timeToken = clock.Value;
            }
            else unresolved.Add("due_time");
        }

        var title = text;
        if (dateToken.Length > 0) title = title.Replace(dateToken, "", StringComparison.Ordinal);
        if (timeToken.Length > 0) title = title.Replace(timeToken, "", StringComparison.Ordinal);
        title = Cleanup().Replace(title, " ").Trim().Trim(',', '，', '。', '.', '；', ';').Trim();
        if (title.Length == 0)
        {
            unresolved.Add("title");
            return Task.FromResult(new AiParseResponse("clarify", [], assumptions, unresolved));
        }

        DateTimeOffset? dueAt = null;
        if (date is not null && time is not null)
        {
            var local = DateTime.SpecifyKind(date.Value.Date + time.Value, DateTimeKind.Unspecified);
            if (RecurrenceExpander.TryResolveInstant(zone, local, out var instant)) dueAt = instant;
            else unresolved.Add("due_at");
        }
        else if (date is not null)
        {
            assumptions.Add("date_without_time");
            unresolved.Add("due_time");
        }
        else if (time is not null)
        {
            var local = DateTime.SpecifyKind(nowLocal.Date + time.Value, DateTimeKind.Unspecified);
            if (RecurrenceExpander.TryResolveInstant(zone, local, out var instant)) dueAt = instant;
            else unresolved.Add("due_at");
            assumptions.Add("assumed_today");
        }
        else
        {
            assumptions.Add("no_due_date");
        }

        var draft = new AiParseDraft(title.Length > 500 ? title[..500] : title, dueAt, input.TimeZone, []);
        return Task.FromResult(new AiParseResponse("create_task", [draft], assumptions, unresolved));
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex ExplicitDate();

    [GeneratedRegex("今天|明天|后天")]
    private static partial Regex RelativeDay();

    [GeneratedRegex("下(?:周|星期)([一二三四五六日天])")]
    private static partial Regex NextWeekday();

    [GeneratedRegex(@"(上午|下午|晚上|早上|中午)?\s*(\d{1,2})(?:[:：](\d{2})|点(半)?)")]
    private static partial Regex Clock();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Cleanup();
}
