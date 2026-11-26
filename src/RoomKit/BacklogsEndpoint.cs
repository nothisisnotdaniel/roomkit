namespace RoomKit;

/// <summary>The factory root the server reads, fixed at start.</summary>
public sealed record FactoryRoot(string Path);

/// <summary>One project in the <c>GET /api/backlogs</c> response. <c>Version</c> is the SHA-256 of the
/// <c>backlog.md</c> bytes (<c>null</c> when unreadable), sent back with every change. <c>Next</c>, <c>Stopped</c> and
/// <c>Blocked</c> come from <see cref="BacklogPlan"/> (<c>null</c>, <c>false</c> and empty when unreadable).
/// <c>StopLine</c> is the 0-based index of the first exact stop line (<c>null</c> without one or when unreadable).
/// <c>Pending</c> is true when the backlog is read from the <see cref="BacklogWorktree"/> (<see cref="BacklogSource"/>).
/// <c>Reason</c> is set only on the <c>factory</c> entry when its develop worktree can't be used
/// (<see cref="FactoryBacklog.NoWorktree"/>, <see cref="FactoryBacklog.NotDevelop"/>; left out otherwise).
/// <c>Claims</c>, <c>ClaimsChecked</c> and <c>ClaimsStale</c> come from the <see cref="ClaimWatcher"/>'s memory.</summary>
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
    int? StopLine = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null)
{
    /// <summary>Other machines' claims on this backlog's numbered items (<see cref="ClaimWatcher"/>), lowest item
    /// first: only items with a numbered line, never one of this factory root's own jobs.</summary>
    public IReadOnlyList<ItemClaim> Claims { get; init; } = [];

    /// <summary>The UTC time of the last successful claim read, or <c>null</c>.</summary>
    public DateTimeOffset? ClaimsChecked { get; init; }

    /// <summary>True when the last claim read failed and <see cref="Claims"/> are the last known ones.</summary>
    public bool ClaimsStale { get; init; }
}

/// <summary>The <c>GET /api/backlogs</c> response.</summary>
public sealed record BacklogsResponse(IReadOnlyList<ProjectBacklog> Projects);

/// <summary><c>GET /api/backlogs</c>: every project's backlog, grouped by mark, then the factory's own
/// (<see cref="FactoryBacklog"/>) as <c>factory</c>. Read-only, no route parameters.</summary>
public static class BacklogsEndpoint
{
    public const string Route = "/api/backlogs";

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], async (
            FactoryRoot root, BacklogWorktree worktree, FactoryBacklog factory, ClaimWatcher claims, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            IReadOnlyList<ProjectFolder> projects = ProjectFinder.Find(root.Path);
            BacklogsResponse response = Build(
                projects, await BacklogSource.ForAll(projects, worktree), await BacklogSource.ForFactory(factory, worktree));
            return Results.Json(WithClaims(response, claims, root));
        });
    }

    /// <summary>Adds each project's claims from the watcher's memory (never git): only claims on an item that has a
    /// numbered line in that backlog, and none whose job is a job folder of this factory root (this machine's own
    /// job, <see cref="IJobFormat.JobFolders"/>).</summary>
    public static BacklogsResponse WithClaims(BacklogsResponse response, ClaimWatcher claims, FactoryRoot root)
    {
        HashSet<string> ownJobs = new(RoomContext.Jobs.JobFolders(root.Path).Select(folder => folder.Id), StringComparer.Ordinal);
        List<ProjectBacklog> projects = [];
        foreach (ProjectBacklog project in response.Projects)
        {
            ClaimState state = claims.State(project.Name);
            HashSet<int> numbers = [.. project.Open.Concat(project.Approval).Concat(project.User).Concat(project.Done)
                .Where(entry => entry.Number is not null)
                .Select(entry => entry.Number!.Value)];
            projects.Add(project with
            {
                Claims = [.. state.Claims
                    .Where(claim => numbers.Contains(claim.Item) && (claim.Job is null || !ownJobs.Contains(claim.Job)))
                    .OrderBy(claim => claim.Item)],
                ClaimsChecked = state.Checked,
                ClaimsStale = state.Stale,
            });
        }

        return new BacklogsResponse(projects);
    }

    /// <summary>The response for the projects, each read from its source folder (same order).</summary>
    public static BacklogsResponse Build(IReadOnlyList<ProjectFolder> projectList, IReadOnlyList<ProjectFolder> sources)
    {
        return Build(projectList, sources, null);
    }

    /// <summary>The response for the projects, each read from its source folder (same order), then, when given, the
    /// <c>factory</c> entry from its <see cref="FactorySource"/> (unreadable with its reason when it has one). Every
    /// readable backlog, the factory's too, counts for the <c>(after: …)</c> rule.</summary>
    public static BacklogsResponse Build(
        IReadOnlyList<ProjectFolder> projectList, IReadOnlyList<ProjectFolder> sources, FactorySource? factory)
    {
        List<(ProjectFolder Project, BacklogFile? File, bool Pending, string? Reason)> read = [];
        for (int index = 0; index < sources.Count; index++)
        {
            read.Add((sources[index], BacklogReader.ReadFile(sources[index]),
                BacklogSource.IsPending(projectList[index], sources[index]), null));
        }

        if (factory is not null)
        {
            read.Add(factory.Source is null
                ? (new ProjectFolder(FactoryBacklog.Name, ""), null, false, factory.Reason)
                : (factory.Source, BacklogReader.ReadFile(factory.Source), factory.Pending, null));
        }

        BacklogWaiting waiting = BacklogWaiting.From(read.Select(item => (item.Project.Name, item.File?.Backlog)));

        List<ProjectBacklog> projects = [];
        foreach ((ProjectFolder project, BacklogFile? file, bool pending, string? reason) in read)
        {
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
                pending,
                file?.Backlog.StopLine,
                reason));
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
