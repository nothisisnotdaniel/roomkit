namespace RoomKit;

/// <summary>The Ready/Waiting rule shared by the backlogs and tasks views: a dependency is met only when it is a
/// done (<c>[x]</c>) line of the named project, or of the same project when it names none. A project name is matched
/// exactly first, then against the single discovered project with the same name ignoring case (like
/// <c>find_project</c> in <c>job.py</c>). An unknown project or number counts as not met.</summary>
internal sealed class BacklogWaiting
{
    private const string FactoryName = "factory";

    private readonly Dictionary<string, HashSet<int>> doneNumbers;

    // Every numbered line (any mark) per project; null for a project whose backlog can't be read.
    private readonly Dictionary<string, HashSet<int>?> allNumbers;

    private BacklogWaiting(Dictionary<string, HashSet<int>> doneNumbers, Dictionary<string, HashSet<int>?> allNumbers)
    {
        this.doneNumbers = doneNumbers;
        this.allNumbers = allNumbers;
    }

    /// <summary>The done and all numbers of every project; an unreadable backlog has no done numbers.</summary>
    public static BacklogWaiting From(IEnumerable<(string Project, Backlog? Backlog)> backlogs)
    {
        Dictionary<string, HashSet<int>> done = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<int>?> all = new(StringComparer.Ordinal);
        foreach ((string project, Backlog? backlog) in backlogs)
        {
            done[project] = Numbers((backlog ?? Backlog.Empty).Done);
            all[project] = backlog is null ? null : Numbers(backlog.All);
        }

        return new BacklogWaiting(done, all);
    }

    /// <summary>The discovered project a dependency names: <paramref name="projectName"/> when it names none, the
    /// exact name when discovered, else the single discovered name equal ignoring case, else <c>null</c>.</summary>
    public string? Resolve(BacklogDependency dependency, string projectName)
    {
        string name = dependency.Project ?? projectName;
        if (allNumbers.ContainsKey(name))
        {
            return name;
        }

        List<string> matches = allNumbers.Keys
            .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The project name to show for a dependency: the discovered name when it resolves, else the name as
    /// written (the item's own project when it names none).</summary>
    public string ProjectName(BacklogDependency dependency, string projectName)
    {
        return Resolve(dependency, projectName) ?? dependency.Project ?? projectName;
    }

    public bool IsMet(BacklogDependency dependency, string projectName)
    {
        string? project = Resolve(dependency, projectName);
        return project is not null
            && doneNumbers.TryGetValue(project, out HashSet<int>? done)
            && done.Contains(dependency.Number);
    }

    /// <summary>True when a dependency names no existing item: its project is not discovered, or that project's
    /// readable backlog has no numbered line with the number. An unreadable backlog and <c>factory</c> are never
    /// missing.</summary>
    public bool IsMissing(BacklogDependency dependency, string projectName)
    {
        if (string.Equals(dependency.Project, FactoryName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? project = Resolve(dependency, projectName);
        if (project is null)
        {
            return true;
        }

        HashSet<int>? numbers = allNumbers[project];
        return numbers is not null && !numbers.Contains(dependency.Number);
    }

    /// <summary>The dependencies of an entry of <paramref name="projectName"/> that are not met, in line order.</summary>
    public IReadOnlyList<BacklogDependency> Unmet(BacklogEntry entry, string projectName)
    {
        return entry.After.Where(dependency => !IsMet(dependency, projectName)).ToList();
    }

    public bool IsWaiting(BacklogEntry entry, string projectName)
    {
        return entry.After.Any(dependency => !IsMet(dependency, projectName));
    }

    private static HashSet<int> Numbers(IEnumerable<BacklogEntry> entries)
    {
        return entries
            .Where(entry => entry.Number is not null)
            .Select(entry => entry.Number!.Value)
            .ToHashSet();
    }
}
