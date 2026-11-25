using System.ComponentModel;
using System.Diagnostics;

namespace RoomKit;

/// <summary>The result of one git call: the exit code (-1 when git could not start or timed out) and its standard
/// output.</summary>
public sealed record GitResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Git calls in a project's own repo, each with a fixed argument list (never a shell), a timeout
/// (30 s, 60 s for push), no terminal prompt and no repo discovery above the project folder. Request text never
/// becomes an argument; the only variable arguments are the commit message (built from a number and an action)
/// and the file paths the server chose.</summary>
public static partial class ProjectGit
{
    public const string Branch = "develop";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(60);

    // Variables that would point git at another repo than the project's own.
    private static readonly string[] RepoVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_PREFIX",
    ];

    // Variables that would add config from the shell that started the server.
    private static readonly string[] ConfigVariables =
    [
        "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_CONFIG", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM",
        "GIT_CONFIG_NOSYSTEM",
    ];

    /// <summary>The current branch, or <c>null</c> when the project folder has no repo of its own or git
    /// fails.</summary>
    public static async Task<string?> CurrentBranch(string folder)
    {
        string dotGit = Path.Combine(folder, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
        {
            return null;
        }

        GitResult result = await Run(folder, ["branch", "--show-current"], Timeout);
        return result.Ok ? result.Output.Trim() : null;
    }

    /// <summary>The number of commits on local <c>develop</c> that are not on local <c>main</c>
    /// (<c>git rev-list --count refs/heads/main..refs/heads/develop</c>, read-only), or <c>null</c> when the folder
    /// has no repo of its own, either branch is missing or git fails or times out.</summary>
    public static async Task<int?> AheadOfMain(string folder)
    {
        string dotGit = Path.Combine(folder, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
        {
            return null;
        }

        GitResult result = await Run(
            folder, ["rev-list", "--count", "refs/heads/main..refs/heads/" + Branch], Timeout);
        if (!result.Ok)
        {
            return null;
        }

        return int.TryParse(
            result.Output.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int count)
            ? count
            : null;
    }

    /// <summary>True when the text is a release tag: exactly <c>vX.Y.Z</c> with no leading zeros (so it never starts
    /// with <c>-</c> and never holds <c>..</c>). Only such a tag ever becomes part of a git argument.</summary>
    public static bool IsReleaseTag(string? text)
    {
        return text is not null && ReleaseTagPattern().IsMatch(text);
    }

    /// <summary>Every local tag as its short name and creator date (Unix seconds, <c>null</c> when git prints none)
    /// from <c>git for-each-ref --format=%(refname:strip=2)%09%(creatordate:unix) refs/tags/</c> (read-only), or
    /// <c>null</c> when the folder has no repo of its own or git fails or times out.</summary>
    public static async Task<IReadOnlyList<(string Name, long? Unix)>?> Tags(string folder)
    {
        if (!HasOwnRepo(folder))
        {
            return null;
        }

        GitResult result = await Run(
            folder, ["for-each-ref", "--format=%(refname:strip=2)%09%(creatordate:unix)", "refs/tags/"], Timeout);
        if (!result.Ok)
        {
            return null;
        }

        List<(string Name, long? Unix)> tags = [];
        foreach (string line in result.Output.Split('\n'))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            long? unix = long.TryParse(
                line[(tab + 1)..].Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out long seconds)
                ? seconds
                : null;
            tags.Add((line[..tab], unix));
        }

        return tags;
    }

    /// <summary>The commits on local <c>develop</c> since the release tag (<c>git rev-list --count
    /// refs/tags/&lt;tag&gt;..refs/heads/develop</c>), or all of <c>develop</c> when <paramref name="tag"/> is
    /// <c>null</c>; <c>null</c> when the tag is no release tag, the folder has no repo of its own, <c>develop</c> is
    /// missing or git fails or times out.</summary>
    public static async Task<int?> CommitsSince(string folder, string? tag)
    {
        string? range = ReleaseRange(tag);
        if (range is null || !HasOwnRepo(folder))
        {
            return null;
        }

        GitResult result = await Run(folder, ["rev-list", "--count", range], Timeout);
        if (!result.Ok)
        {
            return null;
        }

        return int.TryParse(
            result.Output.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int count)
            ? count
            : null;
    }

    /// <summary>The first-parent commits on local <c>develop</c> since the release tag, newest first, as
    /// <c>(abbreviated sha, subject)</c> from <c>git log --first-parent --format=%h%x1f%s --max-count=1000
    /// &lt;range&gt;</c> (the range as in <see cref="CommitsSince"/>); <c>null</c> under the same rules.</summary>
    public static async Task<IReadOnlyList<(string Sha, string Subject)>?> LogSince(string folder, string? tag)
    {
        string? range = ReleaseRange(tag);
        if (range is null || !HasOwnRepo(folder))
        {
            return null;
        }

        GitResult result = await Run(
            folder, ["log", "--first-parent", "--format=%h%x1f%s", "--max-count=" + MaxLog, range], Timeout);
        if (!result.Ok)
        {
            return null;
        }

        List<(string Sha, string Subject)> rows = [];
        foreach (string line in result.Output.Split('\n'))
        {
            int separator = line.IndexOf('\x1f', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            rows.Add((line[..separator], line[(separator + 1)..]));
        }

        return rows;
    }

    /// <summary>The most rows <see cref="LogSince"/> asks git for.</summary>
    public const int MaxLog = 1000;

    /// <summary><c>refs/tags/&lt;tag&gt;..refs/heads/develop</c>, <c>refs/heads/develop</c> without a tag, or
    /// <c>null</c> when the tag is no release tag.</summary>
    private static string? ReleaseRange(string? tag)
    {
        if (tag is null)
        {
            return "refs/heads/" + Branch;
        }

        return IsReleaseTag(tag) ? "refs/tags/" + tag + "..refs/heads/" + Branch : null;
    }

    private static bool HasOwnRepo(string folder)
    {
        string dotGit = Path.Combine(folder, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ReleaseTagPattern();

    /// <summary>True when <c>git status --porcelain -- backlog.md backlog</c> prints nothing (a failed call counts
    /// as not clean). <paramref name="prefix"/> is the backlog's folder in the repo (<c>factory/</c> for the factory's
    /// own backlog, else empty).</summary>
    public static async Task<bool> BacklogIsClean(string folder, string prefix = "")
    {
        GitResult result = await Run(
            folder,
            ["status", "--porcelain", "--", prefix + BacklogReader.FileName, prefix + StoryReader.FolderName],
            Timeout);
        return result.Ok && result.Output.Trim().Length == 0;
    }

    /// <summary>Stages the files (new and deleted ones too) and commits exactly them.</summary>
    public static async Task<bool> Commit(string folder, string message, IReadOnlyList<string> files)
    {
        GitResult add = await Run(folder, ["add", "--", .. files], Timeout);
        if (!add.Ok)
        {
            return false;
        }

        GitResult commit = await Run(folder, ["commit", "-q", "-m", message, "--", .. files], Timeout);
        return commit.Ok;
    }

    /// <summary>Resets the index for the files to <c>HEAD</c> (after a failed commit).</summary>
    public static async Task Unstage(string folder, IReadOnlyList<string> files)
    {
        await Run(folder, ["reset", "-q", "--", .. files], Timeout);
    }

    public static async Task<bool> HasOrigin(string folder)
    {
        GitResult result = await Run(folder, ["remote"], Timeout);
        return result.Ok && result.Output.Split('\n').Any(line => line.Trim() == "origin");
    }

    /// <summary>The ref prefix of the item claims on <c>origin</c> (factory#54).</summary>
    public const string ClaimsPrefix = "refs/factory/claims/";

    /// <summary>Where <see cref="FetchClaims"/> stores the fetched claims in the repo.</summary>
    public const string RemoteClaimsPrefix = "refs/factory/remote-claims/";

    /// <summary>Every claim ref on <c>origin</c> as <c>(sha, ref name)</c> from the read-only
    /// <c>git ls-remote origin refs/factory/claims/*</c>, keeping only lines whose sha is 40 or 64 lower-case hex;
    /// <c>null</c> when the folder has no repo of its own, has no <c>origin</c> (checked first with
    /// <c>git remote</c>) or git fails or times out.</summary>
    public static async Task<IReadOnlyList<(string Sha, string Ref)>?> ClaimRefs(string folder)
    {
        if (!HasOwnRepo(folder) || !await HasOrigin(folder))
        {
            return null;
        }

        GitResult result = await Run(folder, ["ls-remote", "origin", ClaimsPrefix + "*"], Timeout);
        if (!result.Ok)
        {
            return null;
        }

        List<(string Sha, string Ref)> refs = [];
        foreach (string line in result.Output.Split('\n'))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            string sha = line[..tab];
            if (IsObjectId(sha))
            {
                refs.Add((sha, line[(tab + 1)..].TrimEnd('\r')));
            }
        }

        return refs;
    }

    /// <summary>Fetches every claim from <c>origin</c> into <c>refs/factory/remote-claims/*</c>
    /// (<c>git fetch --no-tags --quiet origin +refs/factory/claims/*:refs/factory/remote-claims/*</c>); false when
    /// the folder has no repo of its own or <c>origin</c>, or git fails or times out. It never pushes.</summary>
    public static async Task<bool> FetchClaims(string folder)
    {
        if (!HasOwnRepo(folder) || !await HasOrigin(folder))
        {
            return false;
        }

        GitResult result = await Run(
            folder,
            ["fetch", "--no-tags", "--quiet", "origin", "+" + ClaimsPrefix + "*:" + RemoteClaimsPrefix + "*"],
            Timeout);
        return result.Ok;
    }

    /// <summary>The type of a fetched object (<c>git cat-file -t &lt;sha&gt;</c>: <c>commit</c>, <c>tag</c>, <c>tree</c> or
    /// <c>blob</c>), or <c>null</c> when the sha is not 40 or 64 lower-case hex, the object is missing or git fails or
    /// times out. A claim is read only from a <c>commit</c>, so the object whose size is checked is the one whose message
    /// is read (an annotated tag would be peeled by <c>log</c>).</summary>
    public static async Task<string?> ObjectType(string folder, string sha)
    {
        if (!IsObjectId(sha) || !HasOwnRepo(folder))
        {
            return null;
        }

        GitResult result = await Run(folder, ["cat-file", "-t", sha], Timeout);
        return result.Ok ? result.Output.Trim() : null;
    }

    /// <summary>The size in bytes of a fetched object (<c>git cat-file -s &lt;sha&gt;</c>), or <c>null</c> when the
    /// sha is not 40 or 64 lower-case hex, the object is missing or git fails or times out.</summary>
    public static async Task<long?> ObjectSize(string folder, string sha)
    {
        if (!IsObjectId(sha) || !HasOwnRepo(folder))
        {
            return null;
        }

        GitResult result = await Run(folder, ["cat-file", "-s", sha], Timeout);
        if (!result.Ok)
        {
            return null;
        }

        return long.TryParse(
            result.Output.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long size)
            ? size
            : null;
    }

    /// <summary>The message of a fetched commit (<c>git log -1 --format=%B &lt;sha&gt;</c>), or <c>null</c> when the
    /// sha is not 40 or 64 lower-case hex or git fails or times out.</summary>
    public static async Task<string?> CommitMessage(string folder, string sha)
    {
        if (!IsObjectId(sha) || !HasOwnRepo(folder))
        {
            return null;
        }

        GitResult result = await Run(folder, ["log", "-1", "--format=%B", sha], Timeout);
        return result.Ok ? result.Output : null;
    }

    /// <summary>True when the text is a full object id: exactly 40 or 64 lower-case hex characters (so it never starts
    /// with <c>-</c>).</summary>
    public static bool IsObjectId(string? text)
    {
        return text is not null && ObjectIdPattern().IsMatch(text);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\A([0-9a-f]{40}|[0-9a-f]{64})\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ObjectIdPattern();

    public static async Task<bool> Push(string folder)
    {
        GitResult result = await Run(folder, ["push", "-q", "origin", Branch], PushTimeout);
        return result.Ok;
    }

    public static async Task<GitResult> Run(string folder, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string name in RepoVariables.Concat(ConfigVariables))
        {
            start.Environment.Remove(name);
        }

        foreach (string name in start.Environment.Keys
            .Where(key => key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal)
                || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal))
            .ToArray())
        {
            start.Environment.Remove(name);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(Path.GetFullPath(folder)) ?? "";

        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return new GitResult(-1, "");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new GitResult(-1, "");
        }

        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cancel = new(timeout);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            return new GitResult(-1, "");
        }

        await error;
        return new GitResult(process.ExitCode, await output);
    }
}
