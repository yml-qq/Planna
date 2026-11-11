using Planna.Application.Ai;

namespace Planna.Infrastructure.Ai;

// Deterministic scheduler: sequential slots inside the requested range; overlaps with existing
// time blocks become conflicts and are not scheduled. Max 20 operations per plan (contract).
public sealed class RuleBasedPlanScheduler : IPlanScheduler
{
    private const int MaxOperations = 20;

    public PlanSchedule Build(IReadOnlyList<SchedulableTask> tasks, DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd, IReadOnlyList<ExistingBlock> existing)
    {
        var operations = new List<PlanOperationView>();
        var conflicts = new List<string>();
        var unplanned = new List<Guid>();
        var cursor = rangeStart;
        foreach (var task in tasks)
        {
            if (operations.Count >= MaxOperations)
            {
                unplanned.Add(task.Id);
                continue;
            }
            var duration = TimeSpan.FromMinutes(task.EstimateMinutes ?? 30);
            var end = cursor + duration;
            if (end > rangeEnd)
            {
                unplanned.Add(task.Id);
                continue;
            }
            if (existing.Any(block => cursor < block.End && end > block.Start))
            {
                conflicts.Add("task " + task.Id + " overlaps an existing time block");
                unplanned.Add(task.Id);
                cursor = end;
                continue;
            }
            operations.Add(new PlanOperationView(Guid.NewGuid(), task.Id, task.Version, "set_time_block",
                null, new PlanAfterView(cursor, end), "sequential slot for " + duration.TotalMinutes + " minutes"));
            cursor = end;
        }
        var explanation = "scheduled " + operations.Count + " task(s) sequentially from the range start; " +
                          conflicts.Count + " conflict(s), " + unplanned.Count + " unplanned.";
        return new PlanSchedule(operations, conflicts, unplanned, explanation);
    }
}
