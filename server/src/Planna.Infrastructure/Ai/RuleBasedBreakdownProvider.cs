using Planna.Application.Ai;

namespace Planna.Infrastructure.Ai;

// Deterministic splitter: splits the task title on common delimiters; a single part stays one step.
// No model inference, no hidden writes; users edit the draft before applying.
public sealed class RuleBasedBreakdownProvider : IAiBreakdownProvider
{
    private static readonly char[] Delimiters = ['，', ',', '、', '；', ';', '。'];

    public IReadOnlyList<BreakdownStepView> Generate(string title, string? guidance)
    {
        var source = string.IsNullOrWhiteSpace(guidance) ? title : guidance.Trim();
        var parts = source.Split(Delimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Select(x => x.Length > 500 ? x[..500] : x)
            .Take(20)
            .ToList();
        if (parts.Count == 0) parts.Add(title.Length > 500 ? title[..500] : title);
        return parts.Select((part, index) => new BreakdownStepView(Guid.NewGuid(), part, null, index)).ToList();
    }
}
