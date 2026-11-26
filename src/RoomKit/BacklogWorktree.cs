using System.Globalization;

namespace RoomKit;

/// <summary>What <see cref="BacklogWorktree.Ensure"/> found: the worktree is ready, its backlog files are dirty (a
/// fast-forward could not run over them) or something broke a rule (nothing is written then).</summary>
public enum WorktreeState
{
    Ready,
    Dirty,
    Failed,
}

/// <summary>The server's own git worktree of a project's local <c>backlog</c> branch, at
/// <c>&lt;token folder&gt;/worktrees/&lt;project&gt;</c> (outside the factory root). Backlog changes go there while
/// the project checkout is on another branch than <c>develop</c>; <c>job.py</c> merges <c>backlog</c> into develop
/// between jobs. The <c>worktrees</c> folder follows the <see cref="PrivateFolder"/> rules; the project name comes
/// only from <see cref="ProjectFinder"/>. Every git call goes through <see cref="ProjectGit.Run"/> with a fixed
/// argument list (the worktree path is the only variable one).</summary>
public sealed class BacklogWorktree
{
    public const string BranchName = "backlog";

    public const string FolderName = "worktrees";

    private const string BranchRef = "refs/heads/" + BranchName;

    private const string DevelopRef = "refs/heads/" + ProjectGit.Branch;

    private const int MaxLinkDepth = 40;

    private readonly PrivateFolder folder;

    public BacklogWorktree(string tokenFolder, uint? owner)
    {
        folder = new PrivateFolder(System.IO.Path.Combine(tokenFolder, FolderName), owner);
    }

    /// <summary>The <c>worktrees</c> folder.</summary>
    public string Folder => folder.Path;

    /// <summary>The worktree path of a discovered project.</summary>
    public string PathFor(ProjectFolder project)
    {
        return System.IO.Path.Combine(folder.Path, project.Name);
    }

    /// <summary>True when the <c>worktrees</c> folder follows the private-folder rules and the project's worktree
    /// path is a real folder (<c>lstat</c>, no symlink followed). No git.</summary>
    public bool Exists(ProjectFolder project)
    {
        return folder.IsReady() && NoFollowStat.Of(PathFor(project)).Kind == PathKind.Directory;
    }

    /// <summary>True when <c>backlog</c> has commits <c>develop</c> lacks and the worktree folder exists.</summary>
    public async Task<bool> Pending(ProjectFolder project)
    {
        if (!Exists(project))
        {
            return false;
        }

        GitResult result = await ProjectGit.Run(
            project.Path, ["rev-list", "--count", DevelopRef + ".." + BranchRef], ProjectGit.Timeout);
        return result.Ok
            && int.TryParse(result.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            && count > 0;
    }

    /// <summary>Makes the worktree ready: <c>worktrees/</c> checked (created <c>0700</c> when missing), the path not
    /// a symlink, <c>backlog</c> created from develop when missing, stale registrations pruned, the worktree added
    /// when its folder is missing, then checked against <c>git worktree list --porcelain</c> and
    /// <c>branch --show-current</c>, and fast-forwarded to develop when <c>backlog</c> has nothing of its own.</summary>
    /// <paramref name="gitPrefix"/> is the backlog's folder in the repo (<c>factory/</c> for the factory's own
    /// backlog, else empty).
    public async Task<WorktreeState> Ensure(ProjectFolder project, string gitPrefix = "")
    {
        string path;
        try
        {
            folder.EnsureFolder();
            path = System.IO.Path.Combine(RealPath(folder.Path), project.Name);
        }
        catch (Exception exception) when (exception is PrivateFileException or IOException
                                              or UnauthorizedAccessException)
        {
            return WorktreeState.Failed;
        }

        PathKind kind = NoFollowStat.Of(path).Kind;
        if (kind != PathKind.Missing && kind != PathKind.Directory)
        {
            return WorktreeState.Failed;
        }

        GitResult hasBranch = await ProjectGit.Run(
            project.Path, ["rev-parse", "--verify", "--quiet", BranchRef], ProjectGit.Timeout);
        if (!hasBranch.Ok
            && !(await ProjectGit.Run(project.Path, ["branch", BranchName, DevelopRef], ProjectGit.Timeout)).Ok)
        {
            return WorktreeState.Failed;
        }

        if (!(await ProjectGit.Run(project.Path, ["worktree", "prune"], ProjectGit.Timeout)).Ok)
        {
            return WorktreeState.Failed;
        }

        if (kind == PathKind.Missing
            && !(await ProjectGit.Run(project.Path, ["worktree", "add", "-q", path, BranchName], ProjectGit.Timeout)).Ok)
        {
            return WorktreeState.Failed;
        }

        if (!await IsRegistered(project, path) || NoFollowStat.Of(path).Kind != PathKind.Directory)
        {
            return WorktreeState.Failed;
        }

        GitResult branch = await ProjectGit.Run(path, ["branch", "--show-current"], ProjectGit.Timeout);
        if (!branch.Ok || branch.Output.Trim() != BranchName)
        {
            return WorktreeState.Failed;
        }

        GitResult ahead = await ProjectGit.Run(
            project.Path, ["rev-list", "--count", DevelopRef + ".." + BranchRef], ProjectGit.Timeout);
        if (!ahead.Ok)
        {
            return WorktreeState.Failed;
        }

        if (ahead.Output.Trim() == "0"
            && !(await ProjectGit.Run(path, ["merge", "-q", "--ff-only", DevelopRef], ProjectGit.Timeout)).Ok)
        {
            return await ProjectGit.BacklogIsClean(path, gitPrefix) ? WorktreeState.Failed : WorktreeState.Dirty;
        }

        return WorktreeState.Ready;
    }

    /// <summary>The worktree of the factory repo's <c>backlog</c> branch for a change while a factory job runs: the
    /// worktree that already has <c>backlog</c> checked out (a real folder, fast-forwarded to develop when
    /// <c>backlog</c> has nothing of its own), else this server's own <c>worktrees/factory</c> through
    /// <see cref="Ensure"/>. <c>Path</c> is the worktree (the repo folder git runs in), <c>null</c> unless
    /// <c>Ready</c>.</summary>
    public async Task<(WorktreeState State, string? Path)> EnsureFactory(FactoryBacklog factory)
    {
        string? found = await ForeignFactoryWorktree(factory);
        if (found is null)
        {
            WorktreeState state = await Ensure(factory.Repo, FactoryBacklog.GitPrefix);
            return (state, state == WorktreeState.Ready ? PathFor(factory.Repo) : null);
        }

        if (NoFollowStat.Of(found).Kind != PathKind.Directory)
        {
            return (WorktreeState.Failed, null);
        }

        GitResult branch = await ProjectGit.Run(found, ["branch", "--show-current"], ProjectGit.Timeout);
        GitResult ahead = await ProjectGit.Run(
            factory.Root, ["rev-list", "--count", DevelopRef + ".." + BranchRef], ProjectGit.Timeout);
        if (!branch.Ok || branch.Output.Trim() != BranchName || !ahead.Ok)
        {
            return (WorktreeState.Failed, null);
        }

        if (ahead.Output.Trim() == "0"
            && !(await ProjectGit.Run(found, ["merge", "-q", "--ff-only", DevelopRef], ProjectGit.Timeout)).Ok)
        {
            return (await ProjectGit.BacklogIsClean(found, FactoryBacklog.GitPrefix)
                ? WorktreeState.Failed
                : WorktreeState.Dirty, null);
        }

        return (WorktreeState.Ready, found);
    }

    /// <summary>The worktree to read the factory backlog from while a factory job runs: the one that has
    /// <c>backlog</c> checked out (or this server's own when it exists), but only when <c>backlog</c> has commits
    /// develop lacks; else <c>null</c>.</summary>
    public async Task<string?> FactoryPending(FactoryBacklog factory)
    {
        string? found = await ForeignFactoryWorktree(factory);
        string? path = found ?? (Exists(factory.Repo) ? PathFor(factory.Repo) : null);
        if (path is null || NoFollowStat.Of(path).Kind != PathKind.Directory)
        {
            return null;
        }

        GitResult result = await ProjectGit.Run(
            factory.Root, ["rev-list", "--count", DevelopRef + ".." + BranchRef], ProjectGit.Timeout);
        return result.Ok
            && int.TryParse(result.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            && count > 0
                ? path
                : null;
    }

    /// <summary>A worktree of the factory repo with <c>backlog</c> checked out that is neither this server's own nor
    /// the root checkout, or <c>null</c>.</summary>
    private async Task<string?> ForeignFactoryWorktree(FactoryBacklog factory)
    {
        string? found = await factory.WorktreeOn(BranchRef);
        if (found is null)
        {
            return null;
        }

        try
        {
            // Never our own worktree, and never the root checkout itself (it is never written).
            StringComparison comparison =
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string real = RealPath(found);
            string ours = System.IO.Path.Combine(RealPath(folder.Path), factory.Repo.Name);
            return string.Equals(real, ours, comparison) || string.Equals(real, RealPath(factory.Root), comparison)
                ? null
                : found;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the project's <c>git worktree list --porcelain</c> has <c>branch refs/heads/backlog</c>
    /// on a worktree at exactly <paramref name="path"/>.</summary>
    private static async Task<bool> IsRegistered(ProjectFolder project, string path)
    {
        GitResult list = await ProjectGit.Run(project.Path, ["worktree", "list", "--porcelain", "-z"], ProjectGit.Timeout);
        if (!list.Ok)
        {
            return false;
        }

        string? current = null;
        foreach (string line in list.Output.Split('\0'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = line["worktree ".Length..];
            }
            else if (line == "branch " + BranchRef)
            {
                return OperatingSystem.IsWindows()
                    ? string.Equals(current?.Replace('/', '\\'), path, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(current, path, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>The path with every symlinked folder on it resolved (git records worktree paths that way), at most
    /// <see cref="MaxLinkDepth"/> links deep.</summary>
    internal static string RealPath(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string? root = System.IO.Path.GetPathRoot(full);
        string current = string.IsNullOrEmpty(root) ? "/" : root;
        int links = 0;
        foreach (string part in full[current.Length..].Split(
            System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = System.IO.Path.Combine(current, part);
            while (new DirectoryInfo(next).LinkTarget is not null)
            {
                links++;
                FileSystemInfo? target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: false);
                if (target is null || links > MaxLinkDepth)
                {
                    throw new IOException("link loop");
                }

                next = RealPath(target.FullName);
            }

            current = next;
        }

        return current;
    }
}

/// <summary>Where the backlog of a project is read: the <see cref="BacklogWorktree"/> while the checkout is on
/// another branch than <c>develop</c> and <c>backlog</c> holds edits develop lacks, else the project itself.</summary>
public static class BacklogSource
{
    /// <summary>The folder to read <c>backlog.md</c> and story files from, under the project's own name.</summary>
    public static async Task<ProjectFolder> For(ProjectFolder project, BacklogWorktree worktree)
    {
        string? branch = await ProjectGit.CurrentBranch(project.Path);
        if (branch is null || branch == ProjectGit.Branch || !await worktree.Pending(project))
        {
            return project;
        }

        return new ProjectFolder(project.Name, worktree.PathFor(project));
    }

    /// <summary><see cref="For"/> for every project, in the same order.</summary>
    public static async Task<IReadOnlyList<ProjectFolder>> ForAll(
        IReadOnlyList<ProjectFolder> projects, BacklogWorktree worktree)
    {
        List<ProjectFolder> sources = [];
        foreach (ProjectFolder project in projects)
        {
            sources.Add(await For(project, worktree));
        }

        return sources;
    }

    /// <summary>Where the factory's own backlog is read and written (<see cref="FactoryBacklog"/>): the develop
    /// worktree's <c>factory/</c>, or, while a factory job runs and <c>backlog</c> holds edits develop lacks, that
    /// branch's worktree (<see cref="BacklogWorktree.FactoryPending"/>, <c>Pending</c> true); else the reason.</summary>
    public static async Task<FactorySource> ForFactory(FactoryBacklog factory, BacklogWorktree worktree)
    {
        FactorySource develop = await factory.Develop();
        if (develop.Reason is not null || !factory.JobRunning())
        {
            return develop;
        }

        string? pending = await worktree.FactoryPending(factory);
        return pending is null
            ? develop
            : new FactorySource(
                new ProjectFolder(FactoryBacklog.Name, System.IO.Path.Combine(pending, FactoryBacklog.Name)),
                pending,
                null,
                true);
    }

    /// <summary>True when the source is the worktree rather than the project.</summary>
    public static bool IsPending(ProjectFolder project, ProjectFolder source)
    {
        return !string.Equals(project.Path, source.Path, StringComparison.Ordinal);
    }
}
