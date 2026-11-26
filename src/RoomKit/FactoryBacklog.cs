namespace RoomKit;

/// <summary>Where the factory's own backlog is: <c>Source</c> (named <c>factory</c>, the backlog folder) and
/// <c>Repo</c> (the git worktree that holds it, the folder git runs in), or the fixed <c>Reason</c> it can't be used
/// (<see cref="FactoryBacklog.NoWorktree"/> or <see cref="FactoryBacklog.NotDevelop"/>, both folders <c>null</c>).
/// <c>Pending</c> is true when <c>Source</c> is a worktree of the <c>backlog</c> branch.</summary>
public sealed record FactorySource(ProjectFolder? Source, string? Repo, string? Reason, bool Pending = false);

/// <summary>The factory's own backlog, in its develop worktree (like <c>job.py</c>): the folder is the config key
/// <see cref="ConfigKey"/> when set (ASP.NET config, by default also the environment variable), else
/// <c>&lt;parent of root&gt;/&lt;root name&gt;-factory-develop</c>. It counts only when the root repo's
/// <c>git worktree list --porcelain -z</c> has its real path on <c>refs/heads/develop</c>. The backlog is
/// <c>factory/backlog.md</c> and <c>factory/backlog/</c> in that worktree; nothing is ever created here. The path
/// comes from config only, never from a request.</summary>
public sealed class FactoryBacklog
{
    /// <summary>The backlog's name in the API, and the folder under the repo that holds it.</summary>
    public const string Name = "factory";

    public const string ConfigKey = "FACTORY_DEVELOP_DIR";

    /// <summary>The develop folder is missing.</summary>
    public const string NoWorktree = "no_worktree";

    /// <summary>The develop folder is not a worktree of the root's repo on <c>develop</c>.</summary>
    public const string NotDevelop = "not_develop";

    /// <summary>The git path prefix of the backlog files in the factory repo.</summary>
    public const string GitPrefix = Name + "/";

    private const string DevelopRef = "refs/heads/" + ProjectGit.Branch;

    public FactoryBacklog(string root, string? configured)
    {
        Root = System.IO.Path.GetFullPath(root);
        string trimmed = Root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        DevelopPath = string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(trimmed) ?? trimmed, System.IO.Path.GetFileName(trimmed) + "-factory-develop")
            : System.IO.Path.GetFullPath(configured);
    }

    /// <summary>The factory root, which is the factory repo.</summary>
    public string Root { get; }

    /// <summary>The develop worktree folder (it may be missing).</summary>
    public string DevelopPath { get; }

    /// <summary>The root repo as a <see cref="ProjectFolder"/> named <c>factory</c>, for <see cref="BacklogWorktree"/>
    /// (its worktree is <c>worktrees/factory</c>).</summary>
    public ProjectFolder Repo => new(Name, Root);

    /// <summary>The develop worktree's backlog folder, or the reason it can't be used. Read-only: one
    /// <c>git worktree list</c> in the root.</summary>
    public async Task<FactorySource> Develop()
    {
        if (!Directory.Exists(DevelopPath))
        {
            return NoFollowStat.Of(DevelopPath).Kind == PathKind.Missing
                ? new FactorySource(null, null, NoWorktree)
                : new FactorySource(null, null, NotDevelop);
        }

        string real;
        string root;
        try
        {
            real = BacklogWorktree.RealPath(DevelopPath);
            root = BacklogWorktree.RealPath(Root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FactorySource(null, null, NotDevelop);
        }

        if (!SamePath(real, root) && await BranchOf(real) == DevelopRef)
        {
            return new FactorySource(new ProjectFolder(Name, System.IO.Path.Combine(DevelopPath, Name)), DevelopPath, null);
        }

        return new FactorySource(null, null, NotDevelop);
    }

    /// <summary>True while some job of project <c>factory</c> is in an active or blocked stage of the app's job
    /// format (for the factory's own job format <c>spike|spec|build|review|approve|needs-human</c>, as <c>job.py
    /// factory_job_running</c>): its backlog edits then wait on the <c>backlog</c> branch.</summary>
    public bool JobRunning()
    {
        HashSet<string> stages = new(
            RoomContext.Jobs.ActiveStages.Concat(RoomContext.Jobs.BlockedStages), StringComparer.Ordinal);
        return RoomContext.Jobs.ReadAll(Root)
            .Any(job => job.Project == Name && job.Stage is not null && stages.Contains(job.Stage));
    }

    /// <summary>The path of a worktree of the root repo that has <paramref name="branchRef"/> checked out, as git
    /// lists it, or <c>null</c>.</summary>
    public async Task<string?> WorktreeOn(string branchRef)
    {
        foreach ((string path, string? branch) in await List())
        {
            if (branch == branchRef)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The branch ref the root repo's worktree at the real path <paramref name="real"/> has checked out
    /// (<c>null</c> when it is detached, not registered, or git fails).</summary>
    private async Task<string?> BranchOf(string real)
    {
        foreach ((string path, string? branch) in await List())
        {
            string listed;
            try
            {
                listed = BacklogWorktree.RealPath(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (SamePath(listed, real))
            {
                return branch;
            }
        }

        return null;
    }

    /// <summary>Every worktree of the root repo with its checked-out branch ref (<c>null</c> when detached), from
    /// <c>git worktree list --porcelain -z</c>; empty when git fails.</summary>
    private async Task<List<(string Path, string? Branch)>> List()
    {
        List<(string Path, string? Branch)> worktrees = [];
        GitResult list = await ProjectGit.Run(Root, ["worktree", "list", "--porcelain", "-z"], ProjectGit.Timeout);
        if (!list.Ok)
        {
            return worktrees;
        }

        foreach (string line in list.Output.Split('\0'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                worktrees.Add((line["worktree ".Length..], null));
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal) && worktrees.Count > 0)
            {
                worktrees[^1] = (worktrees[^1].Path, line["branch ".Length..]);
            }
        }

        return worktrees;
    }

    private static bool SamePath(string a, string b)
    {
        return OperatingSystem.IsWindows()
            ? string.Equals(a.Replace('/', '\\'), b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.Ordinal);
    }
}
