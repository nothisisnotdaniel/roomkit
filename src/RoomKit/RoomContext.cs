using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>The one app this process runs: its <see cref="RoomKitOptions"/> and <see cref="IJobFormat"/>, set by
/// <see cref="RoomKitApp"/> before any command or server starts (tests set it once per test assembly). Reading it
/// before it is set throws.</summary>
internal static partial class RoomContext
{
    private static Configured? current;

    public static RoomKitOptions Options => Current.Options;

    public static IJobFormat Jobs => Current.Jobs;

    /// <summary>The fixed start command in help texts: <c>dotnet run --project src/&lt;short name&gt;.Server</c>.</summary>
    public static string RunCommand => "dotnet run --project src/" + Options.ShortName + ".Server";

    private static Configured Current =>
        Volatile.Read(ref current) ?? throw new InvalidOperationException("RoomKit is not configured.");

    /// <summary>Checks the options and makes them (and the job format) the process's app.</summary>
    public static void Use(RoomKitOptions options, IJobFormat jobs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jobs);
        Check(options);
        Check(jobs);
        Volatile.Write(ref current, new Configured(options, jobs));
    }

    /// <summary>Throws <see cref="ArgumentException"/> for an option RoomKit can't use safely.</summary>
    public static void Check(RoomKitOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppName))
        {
            throw new ArgumentException("AppName is empty.", nameof(options));
        }

        if (!NamePattern().IsMatch(options.ShortName))
        {
            throw new ArgumentException("ShortName must be letters, digits, '.' or '-'.", nameof(options));
        }

        if (!NamePattern().IsMatch(options.DataFolderName) || options.DataFolderName is "." or "..")
        {
            throw new ArgumentException("DataFolderName must be one folder name.", nameof(options));
        }

        if (!NamePattern().IsMatch(options.SolutionFileName) || options.SolutionFileName is "." or "..")
        {
            throw new ArgumentException("SolutionFileName must be one file name.", nameof(options));
        }

        if (options.DefaultPort is < 1 or > 65535)
        {
            throw new ArgumentException("DefaultPort must be 1 to 65535.", nameof(options));
        }

        if (!RepositoryPattern().IsMatch(options.ReleaseRepository))
        {
            throw new ArgumentException("ReleaseRepository must be owner/name.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.CaptureNote) || options.CaptureNote.Any(char.IsControl))
        {
            throw new ArgumentException("CaptureNote must be one line of text.", nameof(options));
        }
    }

    /// <summary>Throws <see cref="ArgumentException"/> for a jobs folder that is not a plain relative path: it is
    /// passed to <c>git status</c> after <c>--</c> and joined to the factory root.</summary>
    public static void Check(IJobFormat jobs)
    {
        string folder = jobs.JobsFolderPath;
        if (!FolderPattern().IsMatch(folder) || folder.Split('/').Any(part => part is "." or ".."))
        {
            throw new ArgumentException("JobsFolderPath must be a relative path of plain names.", nameof(jobs));
        }

        if (string.IsNullOrEmpty(jobs.DoneStage) || jobs.IdPattern is null)
        {
            throw new ArgumentException("The job format needs a done stage and an id pattern.", nameof(jobs));
        }

        if (jobs.RecordFileNames.Count == 0
            || jobs.RecordFileNames.Any(name => !NamePattern().IsMatch(name) || name is "." or ".."))
        {
            throw new ArgumentException("RecordFileNames must be plain file names.", nameof(jobs));
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._-]*(/[A-Za-z0-9_][A-Za-z0-9._-]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex FolderPattern();

    private sealed record Configured(RoomKitOptions Options, IJobFormat Jobs);
}
