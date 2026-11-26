using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>An active job with no update for over a day: its id, project, stage and whole hours since its
/// <c>updated</c> time (<c>null</c> when it has none).</summary>
internal sealed record DoctorStuckJob(string Id, string? Project, string Stage, long? Hours);

/// <summary>A local <c>factory/&lt;id&gt;</c> branch of a done job that is merged into the project's base
/// branch.</summary>
internal sealed record DoctorBranch(string Project, string Branch);

/// <summary>One backlog story problem: a fixed <c>Problem</c> code, the item number (<c>null</c> for
/// <c>no_story</c> and <c>orphan</c>) and the line's title (the file name for <c>orphan</c>), at most
/// <see cref="DoctorCheck.MaxLine"/> characters.</summary>
internal sealed record DoctorStory(string Project, int? Number, string Problem, string Line);

/// <summary>The <c>GET /api/doctor</c> response.</summary>
internal sealed record DoctorReport(
    IReadOnlyList<DoctorStuckJob> Stuck,
    IReadOnlyList<string> Uncommitted,
    IReadOnlyList<DoctorBranch> MergedBranches,
    IReadOnlyList<DoctorStory> Stories,
    IReadOnlyList<string> Unreadable);

/// <summary>The factory's health problems, worked out like <c>job.py doctor</c> without running it: stuck active
/// jobs (from <see cref="IJobFormat.ReadAll"/> and <see cref="TimeProvider"/>), uncommitted job records (a read-only
/// <c>git status</c> in the factory root), merged leftover <c>factory/*</c> branches of done jobs (read-only
/// <c>git rev-parse</c> and <c>git branch</c> per project) and backlog story problems (from
/// <see cref="BacklogReader"/>, with story files only <c>lstat</c>ed through <see cref="NoFollowStat"/>, never
/// opened). Every git call has a fixed argument list through <see cref="ProjectGit.Run"/>; nothing is written.</summary>
internal static partial class DoctorCheck
{
    public const int MaxLine = 80;

    public const string FactoryName = "factory";

    public const string NoStory = "no_story";

    public const string Duplicate = "duplicate";

    public const string Missing = "missing";

    public const string BadLink = "bad_link";

    public const string Orphan = "orphan";

    /// <summary>A job in one of these stages is still running for the factory (the job format's
    /// <see cref="IJobFormat.ActiveStages"/>).</summary>
    public static IReadOnlyList<string> ActiveStages => RoomContext.Jobs.ActiveStages;

    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(24);

    private const string BranchPrefix = "factory/";

    private static string JobsPrefix => RoomContext.Jobs.JobsFolderPath + "/";

    public static async Task<DoctorReport> Build(string factoryRoot, DateTimeOffset now)
    {
        IReadOnlyList<RoomJob> jobs = RoomContext.Jobs.ReadAll(factoryRoot);
        Dictionary<string, RoomJob> byId = new(StringComparer.Ordinal);
        foreach (RoomJob job in jobs)
        {
            byId[job.Id] = job;
        }

        SortedSet<string> unreadable = new(StringComparer.Ordinal);
        IReadOnlyList<string>? uncommitted = await Uncommitted(factoryRoot);
        if (uncommitted is null)
        {
            unreadable.Add(FactoryName);
        }

        List<DoctorBranch> branches = [];
        List<DoctorStory> stories = [];
        List<ProjectFolder> projects = ProjectFinder.Find(factoryRoot).ToList();
        projects.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (ProjectFolder project in projects)
        {
            Backlog? backlog = BacklogReader.Read(project);
            if (backlog is null)
            {
                unreadable.Add(project.Name);
                continue;
            }

            List<DoctorBranch>? merged = await MergedBranches(project, byId);
            if (merged is null)
            {
                unreadable.Add(project.Name);
                continue;
            }

            branches.AddRange(merged);
            stories.AddRange(Stories(project, backlog));
        }

        return new DoctorReport(Stuck(jobs, now), uncommitted ?? [], branches, stories, unreadable.ToList());
    }

    /// <summary>Active jobs whose <c>updated</c> is over <see cref="StuckAfter"/> before <paramref name="now"/> or
    /// missing; those without a time first, then oldest first, ties by id (ordinal).</summary>
    public static List<DoctorStuckJob> Stuck(IReadOnlyList<RoomJob> jobs, DateTimeOffset now)
    {
        List<(RoomJob Job, DoctorStuckJob Entry)> stuck = [];
        foreach (RoomJob job in jobs)
        {
            if (job.Stage is not string stage || !ActiveStages.Contains(stage, StringComparer.Ordinal))
            {
                continue;
            }

            if (job.Updated is DateTimeOffset updated)
            {
                TimeSpan age = now - updated;
                if (age <= StuckAfter)
                {
                    continue;
                }

                stuck.Add((job, new DoctorStuckJob(job.Id, job.Project, stage, (long)Math.Floor(age.TotalHours))));
            }
            else
            {
                stuck.Add((job, new DoctorStuckJob(job.Id, job.Project, stage, null)));
            }
        }

        stuck.Sort((a, b) =>
        {
            if (a.Job.Updated is null || b.Job.Updated is null)
            {
                int byMissing = (b.Job.Updated is null ? 1 : 0) - (a.Job.Updated is null ? 1 : 0);
                if (byMissing != 0)
                {
                    return byMissing;
                }
            }
            else
            {
                int byTime = a.Job.Updated.Value.CompareTo(b.Job.Updated.Value);
                if (byTime != 0)
                {
                    return byTime;
                }
            }

            return string.CompareOrdinal(a.Job.Id, b.Job.Id);
        });
        return stuck.Select(item => item.Entry).ToList();
    }

    /// <summary>The job ids under the jobs folder (<see cref="IJobFormat.JobsFolderPath"/>) that <c>git status</c> in the factory root reports as
    /// untracked or changed, sorted ordinally; <c>null</c> when the root has no <c>.git</c> or git fails.</summary>
    private static async Task<IReadOnlyList<string>?> Uncommitted(string factoryRoot)
    {
        if (!HasGit(factoryRoot))
        {
            return null;
        }

        GitResult status = await ProjectGit.Run(
            factoryRoot, ["status", "--porcelain", "--untracked-files=all", "--", RoomContext.Jobs.JobsFolderPath], ProjectGit.Timeout);
        if (!status.Ok)
        {
            return null;
        }

        SortedSet<string> ids = new(StringComparer.Ordinal);
        foreach (string line in status.Output.Split('\n'))
        {
            string entry = line.TrimEnd('\r');
            if (entry.Length <= 3)
            {
                continue;
            }

            foreach (string path in entry[3..].Split(" -> "))
            {
                string? id = JobIdOf(path);
                if (id is not null)
                {
                    ids.Add(id);
                }
            }
        }

        return ids.ToList();
    }

    /// <summary>The path segment after the jobs folder when it is a valid job id (and not a dot name), else
    /// <c>null</c>. A quoted path loses its quotes; an escaped id then fails the id rule.</summary>
    public static string? JobIdOf(string path)
    {
        string plain = path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1] : path;
        if (!plain.StartsWith(JobsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = plain[JobsPrefix.Length..].Split('/');
        if (parts.Length < 2)
        {
            return null;
        }

        string id = parts[0];
        return !id.StartsWith('.') && RoomContext.Jobs.IdPattern.IsMatch(id) ? id : null;
    }

    /// <summary>The project's merged <c>factory/&lt;id&gt;</c> branches of done jobs, by branch (ordinal); empty
    /// without a <c>.git</c>, <c>null</c> when a git call fails.</summary>
    private static async Task<List<DoctorBranch>?> MergedBranches(
        ProjectFolder project, IReadOnlyDictionary<string, RoomJob> jobs)
    {
        List<DoctorBranch> branches = [];
        if (!HasGit(project.Path))
        {
            return branches;
        }

        GitResult develop = await ProjectGit.Run(
            project.Path, ["rev-parse", "--verify", "--quiet", "refs/heads/" + ProjectGit.Branch], ProjectGit.Timeout);
        if (develop.ExitCode < 0)
        {
            return null;
        }

        string target = develop.Ok ? ProjectGit.Branch : "main";
        GitResult merged = await ProjectGit.Run(
            project.Path, ["branch", "--format=%(refname:short)", "--merged", target], ProjectGit.Timeout);
        if (!merged.Ok)
        {
            return null;
        }

        foreach (string line in merged.Output.Split('\n'))
        {
            string branch = line.Trim();
            if (!branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string id = branch[BranchPrefix.Length..];
            if (RoomContext.Jobs.IdPattern.IsMatch(id)
                && jobs.TryGetValue(id, out RoomJob? job)
                && string.Equals(job.Stage, RoomContext.Jobs.DoneStage, StringComparison.Ordinal))
            {
                branches.Add(new DoctorBranch(project.Name, branch));
            }
        }

        branches.Sort((a, b) => string.CompareOrdinal(a.Branch, b.Branch));
        return branches;
    }

    /// <summary>The story problems of one backlog in file order, then the orphan story files by name.</summary>
    public static List<DoctorStory> Stories(ProjectFolder project, Backlog backlog)
    {
        List<DoctorStory> problems = [];
        string folder = Path.Combine(project.Path, ChangeFingerprint.StoriesFolderName);
        bool hasFolder = NoFollowStat.Of(folder).Kind == PathKind.Directory;
        HashSet<int> numbers = [];
        HashSet<string> linked = new(StringComparer.Ordinal);
        foreach (BacklogEntry entry in backlog.All.OrderBy(entry => entry.Line))
        {
            string line = Cut(entry.Title);
            if (entry.Number is not int number)
            {
                if (!entry.IsDone)
                {
                    problems.Add(new DoctorStory(project.Name, null, NoStory, line));
                }

                continue;
            }

            if (!numbers.Add(number))
            {
                problems.Add(new DoctorStory(project.Name, number, Duplicate, line));
            }

            string target = entry.Story ?? "";
            if (!StoryTarget().IsMatch(target))
            {
                problems.Add(new DoctorStory(project.Name, number, BadLink, line));
                continue;
            }

            string name = target[(ChangeFingerprint.StoriesFolderName.Length + 1)..];
            linked.Add(name);
            if (!hasFolder || NoFollowStat.Of(Path.Combine(folder, name)).Kind != PathKind.File)
            {
                problems.Add(new DoctorStory(project.Name, number, Missing, line));
            }
        }

        if (!hasFolder)
        {
            return problems;
        }

        IReadOnlyList<(string Name, PathStat Stat)>? files = NoFollowStat.ListFolder(
            folder, ChangeFingerprint.StoryExtension, NoFollowStat.MaxEntries, out bool tooMany);
        if (files is null || tooMany)
        {
            return problems;
        }

        foreach ((string name, PathStat _) in files)
        {
            if (!linked.Contains(name))
            {
                problems.Add(new DoctorStory(project.Name, null, Orphan, Cut(name)));
            }
        }

        return problems;
    }

    /// <summary>True when the folder has a <c>.git</c> folder or file of its own (a symlink does not count).</summary>
    private static bool HasGit(string folder)
    {
        PathKind kind = NoFollowStat.Of(Path.Combine(folder, ".git")).Kind;
        return kind is PathKind.Directory or PathKind.File;
    }

    /// <summary>At most <see cref="MaxLine"/> characters, never ending inside a surrogate pair.</summary>
    private static string Cut(string text)
    {
        if (text.Length <= MaxLine)
        {
            return text;
        }

        int length = char.IsHighSurrogate(text[MaxLine - 1]) ? MaxLine - 1 : MaxLine;
        return text[..length];
    }

    [GeneratedRegex(@"\Abacklog/[0-9]{3}-[a-z0-9]+(-[a-z0-9]+)*\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex StoryTarget();
}
