namespace RoomKit;

/// <summary>The factory root the server reads, fixed at start.</summary>
public sealed record FactoryRoot(string Path);

/// <summary>One project in the <c>GET /api/backlogs</c> response. <c>Version</c> is the SHA-256 of the
/// <c>backlog.md</c> bytes (<c>null</c> when unreadable), sent back with every change. <c>Next</c>, <c>Stopped</c> and
/// <c>Blocked</c> come from <see cref="BacklogPlan"/> (<c>null</c>, <c>false</c> and empty when unreadable).
/// <c>StopLine</c> is the 0-based index of the first exact stop line (<c>null</c> without one or when unreadable).
/// <c>Pending</c> is true when the backlog is read from the <see cref="BacklogWorktree"/> (<see cref="BacklogSource"/>).</summary>
public sealed record ProjectBacklog(
    string Name,
    bool Readable,
    IReadOnlyList<BacklogEntry> Open,
    IReadOnlyList<BacklogEntry> Approval,
    IReadOnlyList<BacklogEntry> User,
    IReadOnlyList<BacklogEntry> Done,
    string? Version,
    PlanItem? Next,
    bool Stopped,
    IReadOnlyList<BlockedItem> Blocked,
    bool Pending = false,
    int? StopLine = null);

/// <summary>The <c>GET /api/backlogs</c> response.</summary>
public sealed record BacklogsResponse(IReadOnlyList<ProjectBacklog> Projects);

/// <summary><c>GET /api/backlogs</c>: every project's backlog, grouped by mark. Read-only, no route
/// parameters.</summary>
public static class BacklogsEndpoint
{
    public const string Route = "/api/backlogs";

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], async (
            FactoryRoot root, BacklogWorktree worktree, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            IReadOnlyList<ProjectFolder> projects = ProjectFinder.Find(root.Path);
            return Results.Json(Build(projects, await BacklogSource.ForAll(projects, worktree)));
        });
    }

    /// <summary>The response for the projects, each read from its source folder (same order).</summary>
    public static BacklogsResponse Build(IReadOnlyList<ProjectFolder> projectList, IReadOnlyList<ProjectFolder> sources)
    {
        List<(ProjectFolder Project, BacklogFile? File)> read = sources
            .Select(source => (source, BacklogReader.ReadFile(source)))
            .ToList();
        BacklogWaiting waiting = BacklogWaiting.From(read.Select(pair => (pair.Project.Name, pair.File?.Backlog)));

        List<ProjectBacklog> projects = [];
        for (int index = 0; index < read.Count; index++)
        {
            (ProjectFolder project, BacklogFile? file) = read[index];
            Backlog groups = file?.Backlog ?? Backlog.Empty;
            ProjectPlan plan = BacklogPlan.For(file?.Backlog, project.Name, waiting);
            projects.Add(new ProjectBacklog(
                project.Name,
                file is not null,
                WithWaiting(groups.Open, project, waiting),
                WithWaiting(groups.Approval, project, waiting),
                WithWaiting(groups.User, project, waiting),
                WithWaiting(groups.Done, project, waiting),
                file?.Version,
                plan.Next,
                plan.Stopped,
                plan.Blocked,
                BacklogSource.IsPending(projectList[index], project),
                file?.Backlog.StopLine));
        }

        return new BacklogsResponse(projects);
    }

    /// <summary>Marks an entry waiting when any of its dependencies is not met (<see cref="BacklogWaiting"/>), and
    /// fills in its category from its story, read once through <see cref="StoryReader"/> from the source
    /// folder.</summary>
    private static List<BacklogEntry> WithWaiting(
        IReadOnlyList<BacklogEntry> entries, ProjectFolder project, BacklogWaiting waiting)
    {
        return entries
            .Select(entry => entry with
            {
                Waiting = waiting.IsWaiting(entry, project.Name),
                Category = CategoryOf(project, entry),
            })
            .ToList();
    }

    /// <summary>The category of a numbered line's story, or <c>null</c> for any other line or a story that can't be
    /// read.</summary>
    public static string? CategoryOf(ProjectFolder project, BacklogEntry entry)
    {
        return entry.Number is null || entry.Story is null ? null : StoryCategory.Parse(StoryReader.Read(project, entry));
    }
}
