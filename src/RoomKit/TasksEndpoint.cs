namespace RoomKit;

/// <summary>One unmet dependency of a task; <c>Project</c> is always filled in (the task's own project when the line
/// names none).</summary>
internal sealed record TaskDependency(string Project, int Number);

/// <summary>One <c>[h]</c> line in the <c>GET /api/tasks</c> response. <c>Version</c> and <c>Line</c> are what
/// <c>POST /api/backlog/change</c> needs; <c>Story</c> says whether the line links a numbered story; <c>After</c>
/// holds only the unmet dependencies.</summary>
internal sealed record TaskItem(
    string Project,
    string Version,
    int Line,
    int? Number,
    string Title,
    string Rest,
    bool Story,
    IReadOnlyList<TaskDependency> After);

/// <summary>The <c>GET /api/tasks</c> response: the user's tasks split into ready and waiting, and the names of the
/// projects whose <c>backlog.md</c> can't be read.</summary>
internal sealed record TasksResponse(
    IReadOnlyList<TaskItem> Ready, IReadOnlyList<TaskItem> Waiting, IReadOnlyList<string> Unreadable);

/// <summary><c>GET /api/tasks</c>: every <c>[h]</c> line of every discovered project, in project order (as from
/// <see cref="ProjectFinder"/>) and then file order, split by <see cref="BacklogWaiting"/>. Read-only, no route
/// parameters, no story bodies.</summary>
internal static class TasksEndpoint
{
    public const string Route = "/api/tasks";

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], async (
            FactoryRoot root, BacklogWorktree worktree, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(Build(await BacklogSource.ForAll(ProjectFinder.Find(root.Path), worktree)));
        });
    }

    /// <summary>The response from each project's backlog source folder (<see cref="BacklogSource"/>).</summary>
    public static TasksResponse Build(IReadOnlyList<ProjectFolder> sources)
    {
        List<(ProjectFolder Project, BacklogFile? File)> read = sources
            .Select(project => (project, BacklogReader.ReadFile(project)))
            .ToList();
        BacklogWaiting rule = BacklogWaiting.From(read.Select(pair => (pair.Project.Name, pair.File?.Backlog)));

        List<TaskItem> ready = [];
        List<TaskItem> waiting = [];
        List<string> unreadable = [];
        foreach ((ProjectFolder project, BacklogFile? file) in read)
        {
            if (file is null)
            {
                unreadable.Add(project.Name);
                continue;
            }

            foreach (BacklogEntry entry in file.Backlog.User)
            {
                List<TaskDependency> unmet = rule.Unmet(entry, project.Name)
                    .Select(dependency => new TaskDependency(rule.ProjectName(dependency, project.Name), dependency.Number))
                    .ToList();
                TaskItem item = new(
                    project.Name,
                    file.Version,
                    entry.Line,
                    entry.Number,
                    entry.Title,
                    entry.Rest,
                    entry.Number is not null && entry.Story is not null,
                    unmet);
                (unmet.Count == 0 ? ready : waiting).Add(item);
            }
        }

        return new TasksResponse(ready, waiting, unreadable);
    }
}
