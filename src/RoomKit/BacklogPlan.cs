using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>The item <c>/factory next</c> would take now; <c>Spike</c> when the title has the word <c>spike</c>;
/// <c>BelowStop</c> when its line is after the backlog's stop line, so <c>/factory auto</c> stops before it.</summary>
public sealed record PlanItem(int Number, string Title, string? Story, bool Spike, bool BelowStop = false);

/// <summary>One unmet dependency of a blocked item. <c>Project</c> is always set (the item's own project for
/// <c>#N</c>); <c>Missing</c> when it names no existing item.</summary>
public sealed record PlanDependency(string Project, int Number, bool Missing);

/// <summary>An open numbered item with at least one unmet dependency; <c>After</c> holds only the unmet ones.</summary>
public sealed record BlockedItem(int Number, string Title, string? Story, IReadOnlyList<PlanDependency> After);

/// <summary>A project's plan: its next item, whether a missing dependency stops <c>job.py next</c>, and its
/// blocked items.</summary>
internal sealed record ProjectPlan(PlanItem? Next, bool Stopped, IReadOnlyList<BlockedItem> Blocked)
{
    public static readonly ProjectPlan Empty = new(null, false, []);
}

/// <summary>The rule of <c>job.py next</c>: only numbered <c>[ ]</c> lines count, in file order; a line is blocked
/// while a dependency is unmet (<see cref="BacklogWaiting"/>); the first line that is not blocked is next. Spikes
/// count too. The stop line is ignored here (like <c>job.py next</c> without <c>--no-spikes</c>); it only sets
/// <see cref="PlanItem.BelowStop"/>.</summary>
internal static partial class BacklogPlan
{
    public static ProjectPlan For(Backlog? backlog, string projectName, BacklogWaiting waiting)
    {
        if (backlog is null)
        {
            return ProjectPlan.Empty;
        }

        PlanItem? next = null;
        List<BlockedItem> blocked = [];
        foreach (BacklogEntry entry in backlog.Open)
        {
            if (entry.Number is not int number)
            {
                continue;
            }

            List<PlanDependency> unmet = waiting.Unmet(entry, projectName)
                .Select(dependency => new PlanDependency(
                    waiting.ProjectName(dependency, projectName),
                    dependency.Number,
                    waiting.IsMissing(dependency, projectName)))
                .ToList();
            if (unmet.Count > 0)
            {
                blocked.Add(new BlockedItem(number, entry.Title, entry.Story, unmet));
            }
            else if (next is null)
            {
                next = new PlanItem(
                    number, entry.Title, entry.Story, IsSpike(entry.Title), backlog.StopLine is int stop && entry.Line > stop);
            }
        }

        bool stopped = blocked.Any(item => item.After.Any(dependency => dependency.Missing));
        return new ProjectPlan(next, stopped, blocked);
    }

    public static bool IsSpike(string title)
    {
        return SpikeWord().IsMatch(title);
    }

    [GeneratedRegex(@"\bspike\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpikeWord();
}
