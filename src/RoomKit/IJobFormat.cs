using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>A job folder: its name (the job id) and full path.</summary>
public sealed record RoomJobFolder(string Id, string Path);

/// <summary>One job as RoomKit's shared views need it. A value the format can't read is <c>null</c>.
/// <c>Tokens</c> is <c>null</c> when unknown (never 0 for unknown); <c>Updated</c> is the last update time in
/// UTC.</summary>
public sealed record RoomJob(
    string Id,
    string? Project,
    string? Title,
    string? Stage,
    string? Mode,
    string? Path,
    int? Round,
    string? Decision,
    string? Note,
    string? CiStatus,
    long? Tokens,
    DateTimeOffset? Updated);

/// <summary>How a factory keeps its jobs. RoomKit's health view, history, push watcher and change fingerprint read
/// jobs only through this interface; paths come only from <see cref="JobFolders"/>.</summary>
public interface IJobFormat
{
    /// <summary>The jobs folder relative to the factory root, with <c>/</c> separators (no leading or trailing
    /// one).</summary>
    public string JobsFolderPath { get; }

    /// <summary>The names of the files in a job folder whose changes the change fingerprint watches.</summary>
    public IReadOnlyList<string> RecordFileNames { get; }

    /// <summary>Every stage a job can be in; a payload names only one of these.</summary>
    public IReadOnlyList<string> Stages { get; }

    /// <summary>The stages of a job that is still running (checked for being stuck).</summary>
    public IReadOnlyList<string> ActiveStages { get; }

    /// <summary>The stages of a job that waits for the user (a push when a job moves into one).</summary>
    public IReadOnlyList<string> BlockedStages { get; }

    /// <summary>The stage of a finished, merged job.</summary>
    public string DoneStage { get; }

    /// <summary>The reviewer keys, in display order.</summary>
    public IReadOnlyList<string> ReviewKeys { get; }

    /// <summary>The pattern a job id (and so a job folder name) must match.</summary>
    public Regex IdPattern { get; }

    /// <summary>Every job folder (no symlink, name matching <see cref="IdPattern"/>), ordered by id (ordinal).</summary>
    public IReadOnlyList<RoomJobFolder> JobFolders(string factoryRoot);

    /// <summary>Every readable job, ordered by id (ordinal).</summary>
    public IReadOnlyList<RoomJob> ReadAll(string factoryRoot);
}
