using System.Globalization;
using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>One <c>[?]</c> line in the <c>GET /api/inbox</c> response. <c>Version</c> and <c>Line</c> are what
/// <c>POST /api/backlog/change</c> needs; <c>Job</c> is the proposing job's id from the story's Notes, or
/// <c>null</c>.</summary>
internal sealed record InboxItem(
    string Project,
    string Version,
    int Line,
    int? Number,
    string Title,
    string Rest,
    string? Job);

/// <summary>The <c>GET /api/inbox</c> response: every waiting proposal and the names of the projects whose
/// <c>backlog.md</c> can't be read.</summary>
internal sealed record InboxResponse(IReadOnlyList<InboxItem> Items, IReadOnlyList<string> Unreadable);

/// <summary><c>GET /api/inbox</c>: every <c>[?]</c> line of every discovered project, proposals with a known job
/// first (oldest job number first), then the rest; ties by project name (ordinal), then line. Read-only, no route
/// parameters. Paths come only from <see cref="ProjectFinder"/> and the backlog line (<see cref="StoryReader"/>).</summary>
internal static partial class InboxEndpoint
{
    public const string Route = "/api/inbox";

    /// <summary>Most characters of a job id before its optional <c>.</c> + 32 hex suffix.</summary>
    public const int MaxJobLength = 80;

    private const string NotesHeading = "## Notes";

    private const string ProposedBy = "Proposed by job ";

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
    public static InboxResponse Build(IReadOnlyList<ProjectFolder> sources)
    {
        List<InboxItem> items = [];
        List<string> unreadable = [];
        foreach (ProjectFolder project in sources)
        {
            BacklogFile? file = BacklogReader.ReadFile(project);
            if (file is null)
            {
                unreadable.Add(project.Name);
                continue;
            }

            foreach (BacklogEntry entry in file.Backlog.Approval)
            {
                string? story = entry.Number is null ? null : StoryReader.Read(project, entry);
                items.Add(new InboxItem(
                    project.Name,
                    file.Version,
                    entry.Line,
                    entry.Number,
                    entry.Title,
                    entry.Rest,
                    story is null ? null : ProposingJob(story)));
            }
        }

        List<InboxItem> sorted = items
            .OrderBy(item => item.Job is null ? 1 : 0)
            .ThenBy(item => item.Job is null ? 0 : JobNumber(item.Job))
            .ThenBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ToList();
        return new InboxResponse(sorted, unreadable);
    }

    /// <summary>The id after the first <c>Proposed by job </c> in the story's first <c>## Notes</c> section (up to
    /// the next level 1 or 2 heading), or <c>null</c> when there is none or it is not a valid job id. A trailing
    /// <c>.</c>, <c>,</c>, <c>;</c> or <c>:</c> ends the sentence and is not part of the id.</summary>
    public static string? ProposingJob(string story)
    {
        bool inNotes = false;
        foreach (string raw in story.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inNotes)
                {
                    return null;
                }

                inNotes = string.Equals(line.TrimEnd(), NotesHeading, StringComparison.Ordinal);
                continue;
            }

            if (!inNotes)
            {
                continue;
            }

            int index = line.IndexOf(ProposedBy, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            string tail = line[(index + ProposedBy.Length)..];
            int end = 0;
            while (end < tail.Length && !char.IsWhiteSpace(tail[end]))
            {
                end++;
            }

            string id = tail[..end].TrimEnd('.', ',', ';', ':');
            return IsJobId(id) ? id : null;
        }

        return null;
    }

    public static bool IsJobId(string id)
    {
        if (!JobId().IsMatch(id))
        {
            return false;
        }

        int dot = id.IndexOf('.', StringComparison.Ordinal);
        return (dot < 0 ? id.Length : dot) <= MaxJobLength;
    }

    private static int JobNumber(string id)
    {
        return int.Parse(id[..id.IndexOf('-', StringComparison.Ordinal)], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\A[0-9]{1,6}-[a-z0-9]+(-[a-z0-9]+)*(\.[0-9a-f]{32})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex JobId();
}
