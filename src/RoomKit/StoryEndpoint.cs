using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;

namespace RoomKit;

/// <summary>The <c>GET /api/story</c> response: the block tree, plus the raw <c>Markdown</c> and its
/// <c>StoryVersion</c> (SHA-256 of the file bytes) for the story editor, and its <c>Category</c>
/// (<see cref="StoryCategory"/>, <c>null</c> without one).</summary>
internal sealed record StoryResponse(
    string Project,
    int Number,
    string Title,
    IReadOnlyList<StoryBlock> Blocks,
    string Markdown,
    string StoryVersion,
    string? Category);

/// <summary><c>GET /api/story?project=&lt;name&gt;&amp;number=&lt;N&gt;</c>: one numbered backlog item's story as a
/// block tree. Only <c>project</c> (matched exactly against the discovered projects) and <c>number</c> are read;
/// the file path comes from the backlog line. Every refusal is the same fixed 404.</summary>
internal static partial class StoryEndpoint
{
    public const string Route = "/api/story";

    public const string NotFoundBody = "{\"error\":\"not found\"}";

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], async (
            FactoryRoot root, BacklogWorktree worktree, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            StoryResponse? story = await Build(
                root.Path, worktree, context.Request.Query["project"], context.Request.Query["number"]);
            if (story is null)
            {
                return Results.Content(NotFoundBody, "application/json", statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(story);
        });
    }

    /// <summary>The story, read from the project's backlog source folder (<see cref="BacklogSource"/>), or
    /// <c>null</c>.</summary>
    public static async Task<StoryResponse?> Build(
        string factoryRoot, BacklogWorktree worktree, StringValues projectValue, StringValues numberValue)
    {
        if (projectValue.Count != 1 || numberValue.Count != 1)
        {
            return null;
        }

        string projectName = projectValue[0] ?? "";
        string numberText = numberValue[0] ?? "";
        if (!Number().IsMatch(numberText) || !BacklogParser.TryParseNumber(numberText, out int number))
        {
            return null;
        }

        ProjectFolder? found = ProjectFinder.Find(factoryRoot)
            .FirstOrDefault(folder => string.Equals(folder.Name, projectName, StringComparison.Ordinal));
        if (found is null)
        {
            return null;
        }

        ProjectFolder project = await BacklogSource.For(found, worktree);

        Backlog? backlog = BacklogReader.Read(project);
        BacklogEntry? entry = backlog?.All.FirstOrDefault(line => line.Number == number);
        if (entry is null)
        {
            return null;
        }

        byte[]? bytes = StoryReader.ReadBytes(project, entry);
        if (bytes is null)
        {
            return null;
        }

        string text = SafeFileReader.Decode(bytes);
        return new StoryResponse(
            project.Name, number, entry.Title, StoryMarkdown.Parse(text), text, SafeFileReader.Version(bytes),
            StoryCategory.Parse(text));
    }

    [GeneratedRegex(@"\A[0-9]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
}
