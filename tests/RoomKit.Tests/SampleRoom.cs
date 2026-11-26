using System.Globalization;
using System.Text.RegularExpressions;

namespace RoomKit.Tests;

/// <summary>A test-only app on RoomKit: other names, port 8201, repository <c>example/sampleroom</c> and
/// <see cref="SampleJobFormat"/>. It maps no endpoints of its own.</summary>
internal static class SampleRoom
{
    public const string Version = "1.4.2";

    public static RoomKitOptions Options { get; } = new()
    {
        AppName = "Sample Room",
        ShortName = "SampleRoom",
        DataFolderName = "SampleRoom",
        SolutionFileName = "SampleRoom.slnx",
        DefaultPort = 8201,
        ReleaseRepository = "example/sampleroom",
        CaptureNote = "Draft from SampleRoom",
        InformationalVersion = Version,
    };

    public static SampleJobFormat Jobs { get; } = new();

    public static void Map(RoomRoutes routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
    }
}

/// <summary>A job format unlike the factory's: jobs in <c>work/jobs/&lt;id&gt;/job.txt</c>, one
/// <c>key=value</c> per line (stage, project, title, mode, tokens, updated), stages plan, make, wait and done.</summary>
internal sealed partial class SampleJobFormat : IJobFormat
{
    public const string FileName = "job.txt";

    private static readonly string[] Active = ["plan", "make"];

    private static readonly string[] Blocked = ["wait"];

    private static readonly string[] All = ["plan", "make", "wait", "done"];

    private static readonly string[] Records = [FileName];

    private static readonly string[] Keys = ["review"];

    public string JobsFolderPath => "work/jobs";

    public IReadOnlyList<string> RecordFileNames => Records;

    public IReadOnlyList<string> Stages => All;

    public IReadOnlyList<string> ActiveStages => Active;

    public IReadOnlyList<string> BlockedStages => Blocked;

    public string DoneStage => "done";

    public IReadOnlyList<string> ReviewKeys => Keys;

    public Regex IdPattern => Id();

    /// <summary>Writes a job's record file into a temp root and returns its folder.</summary>
    public static string Write(string factoryRoot, string id, string stage, DateTimeOffset updated, long? tokens = null)
    {
        string folder = Path.Combine(factoryRoot, "work", "jobs", id);
        Directory.CreateDirectory(folder);
        string text = "stage=" + stage + "\nproject=Alpha\ntitle=Job " + id + "\nmode=fast\nupdated="
            + updated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\n"
            + (tokens is null ? "" : "tokens=" + tokens.Value.ToString(CultureInfo.InvariantCulture) + "\n");
        File.WriteAllText(Path.Combine(folder, FileName), text);
        return folder;
    }

    public IReadOnlyList<RoomJobFolder> JobFolders(string factoryRoot)
    {
        string jobs = Path.Combine(factoryRoot, "work", "jobs");
        if (!Directory.Exists(jobs))
        {
            return [];
        }

        return [.. new DirectoryInfo(jobs).EnumerateDirectories()
            .Where(folder => folder.LinkTarget is null && Id().IsMatch(folder.Name))
            .OrderBy(folder => folder.Name, StringComparer.Ordinal)
            .Select(folder => new RoomJobFolder(folder.Name, folder.FullName))];
    }

    public IReadOnlyList<RoomJob> ReadAll(string factoryRoot)
    {
        List<RoomJob> jobs = [];
        foreach (RoomJobFolder folder in JobFolders(factoryRoot))
        {
            string file = Path.Combine(folder.Path, FileName);
            if (!File.Exists(file))
            {
                continue;
            }

            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(file))
            {
                int equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    values[line[..equals]] = line[(equals + 1)..];
                }
            }

            jobs.Add(new RoomJob(
                folder.Id,
                values.GetValueOrDefault("project"),
                values.GetValueOrDefault("title"),
                values.GetValueOrDefault("stage"),
                values.GetValueOrDefault("mode"),
                null,
                null,
                null,
                null,
                null,
                values.TryGetValue("tokens", out string? tokens) ? long.Parse(tokens, CultureInfo.InvariantCulture) : null,
                values.TryGetValue("updated", out string? updated)
                    ? DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                    : null));
        }

        return jobs;
    }

    [GeneratedRegex(@"\A[0-9]{1,6}-[a-z0-9-]{1,80}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Id();
}
